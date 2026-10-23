namespace PeerOnQ.Media.Pipeline;

/// <summary>One reassembled protected frame, still authenticated by the session layer later.</summary>
internal readonly record struct AssembledVideoFrame(byte[] Frame, uint Timestamp);

/// <summary>What one assembler step produced: frames in order, NACKs and key frame demand.</summary>
internal sealed class VideoAssemblyOutput
{
    public List<AssembledVideoFrame> Frames { get; } = [];
    public List<ushort> Nacks { get; } = [];
    public bool KeyFrameRequested { get; set; }

    public void Clear()
    {
        Frames.Clear();
        Nacks.Clear();
        KeyFrameRequested = false;
    }
}

/// <summary>
/// Reorders video RTP packets by sequence number, asks for each missing one by NACK, and
/// releases complete frames strictly in order.
///
/// SIPSorcery's VP8 framer appends packets in arrival order, so one lost packet damages its
/// frame and every loss costs a key frame. Here a lost packet costs one retransmission round
/// trip. A frame that cannot be repaired within a short, RTT-derived window is skipped, and a
/// key frame is requested exactly as before; the window keeps a lossy link from turning into
/// latency. Not thread-safe: the caller serializes access.
/// </summary>
internal sealed class VideoRtpFrameAssembler
{
    public const int MaximumFrameBytes = 16 * 1024 * 1024;
    public const int MaximumBufferedPackets = 16_384;
    public const int MaximumGapPackets = 2_048;
    public const int MaximumOutstandingMissing = 512;
    public const int MaximumNackAttempts = 3;
    public const int MaximumTailProbes = 2;

    private const long ExtendedOrigin = 1L << 32;

    private readonly TimeProvider _time;
    private readonly Dictionary<long, Slot> _slots = [];
    private readonly SortedDictionary<long, MissingPacket> _missing = [];
    private readonly SortedSet<long> _markers = [];
    private bool _anchored;
    private bool _synced;
    private long _highest;
    private long _nextSequence;
    private long _contiguousEnd;
    private long _lastPacketAt;
    private int _tailProbes;
    private long _lastTailProbeAt;

    public VideoRtpFrameAssembler(TimeProvider? timeProvider = null) =>
        _time = timeProvider ?? TimeProvider.System;

    /// <summary>The latest path round-trip time; zero when unknown.</summary>
    public TimeSpan RoundTripTime { get; set; }

    public long RecoveredPackets { get; private set; }
    public long SkippedFrames { get; private set; }
    public int BufferedPackets => _slots.Count;
    public int OutstandingMissing => _missing.Count;

    /// <summary>How long a missing packet may hold back newer frames before it is given up.</summary>
    public TimeSpan HoldWindow
    {
        get
        {
            var rtt = RoundTripTime.TotalMilliseconds;
            return rtt <= 0
                ? TimeSpan.FromMilliseconds(120)
                : TimeSpan.FromMilliseconds(Math.Clamp(rtt * 2.5 + 30, 60, 300));
        }
    }

    /// <summary>Delay before the same missing packet is requested again.</summary>
    public TimeSpan RetryInterval
    {
        get
        {
            var rtt = RoundTripTime.TotalMilliseconds;
            return rtt <= 0
                ? TimeSpan.FromMilliseconds(40)
                : TimeSpan.FromMilliseconds(Math.Clamp(rtt * 1.5 + 10, 25, 150));
        }
    }

    public void Push(ushort sequence, uint timestamp, bool marker, ReadOnlySpan<byte> payload, VideoAssemblyOutput output)
    {
        if (payload.IsEmpty) return;
        var now = _time.GetTimestamp();
        _lastPacketAt = now;
        _tailProbes = 0;
        var packet = Extend(sequence);

        if (VideoRetransmissionPayload.IsRetransmission(payload))
        {
            if (!VideoRetransmissionPayload.TryDecode(payload, out var repaired, out var original,
                    out var originalTimestamp, out var originalMarker, out var media))
                return;
            // The original goes in first, then the numbers that carry no frame bytes: any lost
            // retransmission it repairs, and its own (always the newest), so neither looks lost.
            var originalPacket = Extend(original);
            var repairedPacket = Extend(repaired);
            if (_synced && originalPacket >= _nextSequence && !_slots.ContainsKey(originalPacket)) RecoveredPackets++;
            Insert(originalPacket, Slot.FromMedia(originalTimestamp, originalMarker, media), now, output);
            if (repaired != original) Insert(repairedPacket, Slot.Filler, now, output);
            Insert(packet, Slot.Filler, now, output);
        }
        else if (VideoRtpPacketizer.IsMediaDescriptor(payload[0]))
        {
            Insert(packet, Slot.FromMedia(timestamp, marker, payload), now, output);
        }
        else
        {
            return;
        }

        Deliver(now, output);
    }

    /// <summary>Retries NACKs, probes for a lost frame tail and expires unrecoverable frames.</summary>
    public void Tick(VideoAssemblyOutput output)
    {
        if (!_synced) return;
        var now = _time.GetTimestamp();
        var retry = RetryInterval;
        var updates = new List<KeyValuePair<long, MissingPacket>>();
        foreach (var (sequence, missing) in _missing)
        {
            if (missing.Attempts >= MaximumNackAttempts
                || _time.GetElapsedTime(missing.LastRequestedAt, now) < retry)
                continue;
            output.Nacks.Add(unchecked((ushort)sequence));
            updates.Add(new(sequence, missing with { Attempts = missing.Attempts + 1, LastRequestedAt = now }));
        }
        foreach (var (sequence, missing) in updates) _missing[sequence] = missing;

        // A lost frame tail leaves no gap to detect until the next frame. When an incomplete
        // frame stops growing, ask for the numbers that would have followed it.
        if (_missing.Count == 0
            && _contiguousEnd >= _nextSequence
            && _tailProbes < MaximumTailProbes
            && _time.GetElapsedTime(_lastPacketAt, now) > retry
            && (_tailProbes == 0 || _time.GetElapsedTime(_lastTailProbeAt, now) > retry))
        {
            for (var offset = 1; offset <= 17; offset++)
                output.Nacks.Add(unchecked((ushort)(_highest + offset)));
            _tailProbes++;
            _lastTailProbeAt = now;
            if (_tailProbes == 1) _missingTailSince = now;
        }

        if (_contiguousEnd >= _nextSequence && _missing.Count == 0 && _tailProbes > 0
            && _time.GetElapsedTime(_missingTailSince, now) > HoldWindow)
        {
            GiveUpHead(output);
            _tailProbes = 0;
        }

        Deliver(now, output);
    }

    private long _missingTailSince;
    private int _staleStreak;

    /// <summary>Forgets all state, e.g. when the sender's SSRC changes.</summary>
    public void Reset()
    {
        _slots.Clear();
        _missing.Clear();
        _markers.Clear();
        _anchored = false;
        _synced = false;
        _tailProbes = 0;
        _staleStreak = 0;
    }

    private long Extend(ushort sequence)
    {
        if (!_anchored)
        {
            _anchored = true;
            _highest = ExtendedOrigin + sequence - 1;
            return ExtendedOrigin + sequence;
        }
        var delta = unchecked((short)(sequence - (ushort)_highest));
        return _highest + delta;
    }

    private void Insert(long packet, Slot slot, long now, VideoAssemblyOutput output)
    {
        if (!_synced)
        {
            if (slot.IsFiller || !slot.Start)
            {
                _highest = Math.Max(_highest, packet);
                return;
            }
            // The first frame start anchors the stream; anything before it is unusable.
            _synced = true;
            _nextSequence = packet;
            _contiguousEnd = packet - 1;
            _highest = packet - 1;
        }

        if (packet < _nextSequence - MaximumGapPackets)
        {
            // A sender whose numbering jumped backwards would otherwise look stale forever.
            if (++_staleStreak > 64) ResetTo(packet, slot, output);
            return;
        }
        _staleStreak = 0;
        if (packet < _nextSequence || _slots.ContainsKey(packet)) return;

        if (packet > _highest)
        {
            var gap = packet - _highest - 1;
            if (gap > MaximumGapPackets || _missing.Count + gap > MaximumOutstandingMissing)
            {
                // A loss this large is cheaper to repair with a key frame than with NACKs.
                ResetTo(packet, slot, output);
                return;
            }
            for (var lost = _highest + 1; lost < packet; lost++)
            {
                _missing[lost] = new MissingPacket(now, now, 1);
                output.Nacks.Add(unchecked((ushort)lost));
            }
            _highest = packet;
        }
        else
        {
            _missing.Remove(packet);
        }

        _slots[packet] = slot;
        if (!slot.IsFiller && slot.Marker) _markers.Add(packet);
        while (_slots.ContainsKey(_contiguousEnd + 1)) _contiguousEnd++;

        if (_slots.Count > MaximumBufferedPackets) ResetTo(null, default, output);
    }

    private void Deliver(long now, VideoAssemblyOutput output)
    {
        while (true)
        {
            while (_markers.Count > 0 && _markers.Min <= _contiguousEnd)
            {
                var marker = _markers.Min;
                _markers.Remove(marker);
                EmitFrame(marker, output);
            }

            // The first gap holds every newer frame back, but only for the hold window.
            var head = _contiguousEnd + 1;
            if (head > _highest || !_missing.TryGetValue(head, out var missing)
                || _time.GetElapsedTime(missing.Since, now) <= HoldWindow)
                return;
            GiveUpHead(output);
        }
    }

    private void EmitFrame(long marker, VideoAssemblyOutput output)
    {
        var fragments = new List<byte[]>();
        var valid = true;
        var total = 0L;
        uint? frameTimestamp = null;
        for (var packet = _nextSequence; packet <= marker; packet++)
        {
            if (!_slots.Remove(packet, out var slot) || slot.IsFiller) continue;
            if (fragments.Count == 0 ? !slot.Start : slot.Start) valid = false;
            if (frameTimestamp is { } expected && expected != slot.Timestamp) valid = false;
            frameTimestamp ??= slot.Timestamp;
            total += slot.Data!.Length;
            fragments.Add(slot.Data);
        }
        _nextSequence = marker + 1;
        if (_contiguousEnd < marker) _contiguousEnd = marker;

        if (!valid || fragments.Count == 0 || total > MaximumFrameBytes)
        {
            SkippedFrames++;
            output.KeyFrameRequested = true;
            return;
        }

        var frame = GC.AllocateUninitializedArray<byte>((int)total);
        var offset = 0;
        foreach (var fragment in fragments)
        {
            fragment.CopyTo(frame, offset);
            offset += fragment.Length;
        }
        output.Frames.Add(new AssembledVideoFrame(frame, frameTimestamp!.Value));
    }

    /// <summary>Skips the blocked frame: resume at the next frame start after the first gap.</summary>
    private void GiveUpHead(VideoAssemblyOutput output)
    {
        var head = _contiguousEnd + 1;
        long? resume = null;
        for (var packet = head + 1; packet <= _highest; packet++)
        {
            if (_slots.TryGetValue(packet, out var slot) && !slot.IsFiller && slot.Start)
            {
                resume = packet;
                break;
            }
        }

        var end = resume ?? _highest + 1;
        for (var packet = _nextSequence; packet < end; packet++)
        {
            _slots.Remove(packet);
            _missing.Remove(packet);
        }
        while (_markers.Count > 0 && _markers.Min < end) _markers.Remove(_markers.Min);
        _nextSequence = end;
        _contiguousEnd = end - 1;
        while (_slots.ContainsKey(_contiguousEnd + 1)) _contiguousEnd++;
        SkippedFrames++;
        output.KeyFrameRequested = true;
    }

    private void ResetTo(long? packet, Slot slot, VideoAssemblyOutput output)
    {
        _slots.Clear();
        _missing.Clear();
        _markers.Clear();
        SkippedFrames++;
        output.KeyFrameRequested = true;
        if (packet is not { } resume)
        {
            _synced = false;
            return;
        }

        _highest = resume - 1;
        if (slot.IsFiller || !slot.Start)
        {
            _synced = false;
            _highest = resume;
            return;
        }
        _nextSequence = resume;
        _contiguousEnd = resume - 1;
        Insert(resume, slot, _time.GetTimestamp(), output);
    }

    private readonly record struct MissingPacket(long Since, long LastRequestedAt, int Attempts);

    private readonly record struct Slot(byte[]? Data, uint Timestamp, bool Start, bool Marker, bool IsFiller)
    {
        public static Slot Filler => new(null, 0, false, false, true);

        public static Slot FromMedia(uint timestamp, bool marker, ReadOnlySpan<byte> payload) =>
            new(payload[1..].ToArray(), timestamp, payload[0] == VideoRtpPacketizer.StartDescriptor, marker, false);
    }
}
