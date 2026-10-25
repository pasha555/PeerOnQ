namespace PeerOnQ.Media.Pipeline;

/// <summary>Sends one prepared RTP payload on the video stream.</summary>
internal interface IVideoRtpPacketSink
{
    /// <summary>The RTP timestamp the stream would have used for its first frame.</summary>
    uint InitialTimestamp { get; }

    /// <summary>Returns the sequence number used, or null when the stream cannot send now.</summary>
    ushort? Send(byte[] payload, uint timestamp, bool marker);
}

/// <summary>
/// Paces video RTP packets and answers negotiated NACKs from a short send history.
///
/// A 4K key frame is hundreds of packets. Sent as one line-rate burst it overflows shallow
/// router queues and the receiver's socket buffer, and the loss forces another key frame of the
/// same size: a freeze loop. Ordinary frames still leave at once through a small burst
/// allowance; larger ones are spread at a multiple of the target bitrate, and never beyond a
/// per-frame deadline, so pacing bounds rather than adds latency. Retransmissions jump the
/// queue (the viewer is waiting on them) and have their own bitrate budget so recovery cannot
/// become congestion.
/// </summary>
internal sealed class PacedVideoRtpSender : IDisposable
{
    public const double PacingFactor = 2.5;
    public const int MinimumPacingKbps = 5_000;
    public const int BurstPackets = 16;
    public const int MaximumQueuedBytes = 8 * 1024 * 1024;
    public const int HistoryCapacity = 4096;
    public const double RetransmissionBudgetFraction = 0.35;
    public static readonly TimeSpan MaximumQueueDelay = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan HistoryLifetime = TimeSpan.FromSeconds(1);

    private const int PacketOverheadBytes = 12 + 10;
    private const int PacketBytes = VideoRtpPacketizer.MaximumFragmentBytes + 1 + PacketOverheadBytes;

    private readonly IVideoRtpPacketSink _sink;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Queue<QueuedFrame> _frames = new();
    private readonly Queue<Retransmission> _retransmissions = new();
    private readonly HashSet<ushort> _queuedRepairs = [];
    private readonly (ushort Sequence, SentPacket? Packet)[] _history = new (ushort, SentPacket?)[HistoryCapacity];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly HighResolutionWaiter _waiter = new();
    private readonly Thread? _thread;
    private volatile bool _stopping;
    private int _targetBitrateKbps;
    private double _tokens;
    private double _retransmissionTokens;
    private long _lastRefill;
    private long _queuedBytes;
    private bool _timestampInitialized;
    private uint _nextTimestamp;
    private uint _lastMediaTimestamp;
    private long _framesSent;
    private long _retransmittedPackets;
    private long _nackedPackets;
    private long _unansweredNacks;
    private long _overflowedFrames;

    public PacedVideoRtpSender(
        IVideoRtpPacketSink sink,
        int targetBitrateKbps,
        bool startThread = true,
        TimeProvider? timeProvider = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _time = timeProvider ?? TimeProvider.System;
        _targetBitrateKbps = Math.Max(1, targetBitrateKbps);
        _tokens = BurstBytes(PacingBytesPerSecond(_targetBitrateKbps));
        _retransmissionTokens = RetransmissionCapacity(_targetBitrateKbps);
        _lastRefill = _time.GetTimestamp();
        if (!startThread) return;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PeerOnQ video pacer",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Raised when queued frames were discarded; a key frame must follow.</summary>
    public event EventHandler? QueueOverflowed;

    /// <summary>Answer NACKs only when the viewer negotiated in-band retransmission.</summary>
    public bool RetransmissionEnabled { get; set; }

    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long RetransmittedPackets => Interlocked.Read(ref _retransmittedPackets);
    public long NackedPackets => Interlocked.Read(ref _nackedPackets);
    public long UnansweredNacks => Interlocked.Read(ref _unansweredNacks);
    public long OverflowedFrames => Interlocked.Read(ref _overflowedFrames);
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    public void SetTargetBitrateKbps(int kbps) =>
        Volatile.Write(ref _targetBitrateKbps, Math.Max(1, kbps));

    /// <summary>Packetizes and queues one protected frame; the pacing thread sends it.</summary>
    public void EnqueueFrame(ReadOnlySpan<byte> frame, uint durationRtpUnits)
    {
        if (frame.IsEmpty || _stopping) return;
        var packets = VideoRtpPacketizer.Packetize(frame);
        var bytes = packets.Sum(packet => packet.Length + PacketOverheadBytes);
        var overflowed = false;
        lock (_gate)
        {
            if (!_timestampInitialized)
            {
                _nextTimestamp = _sink.InitialTimestamp;
                _timestampInitialized = true;
            }
            var timestamp = _nextTimestamp;
            _nextTimestamp = unchecked(_nextTimestamp + durationRtpUnits);

            if (Interlocked.Read(ref _queuedBytes) + bytes > MaximumQueuedBytes)
                overflowed = DropUnstartedFramesLocked();
            _frames.Enqueue(new QueuedFrame(packets, timestamp, _time.GetTimestamp(), bytes));
            Interlocked.Add(ref _queuedBytes, bytes);
        }

        if (overflowed) QueueOverflowed?.Invoke(this, EventArgs.Empty);
        Signal();
    }

    /// <summary>Queues retransmissions for one RTCP generic NACK (RFC 4585 PID + BLP).</summary>
    public void OnNack(ushort packetId, ushort lostBitmask)
    {
        if (!RetransmissionEnabled || _stopping) return;
        var queued = false;
        lock (_gate)
        {
            RefillLocked(_time.GetTimestamp());
            for (var bit = -1; bit < 16; bit++)
            {
                if (bit >= 0 && (lostBitmask & (1 << bit)) == 0) continue;
                var sequence = unchecked((ushort)(packetId + bit + 1));
                Interlocked.Increment(ref _nackedPackets);
                queued |= TryQueueRetransmissionLocked(sequence);
            }
        }

        if (queued) Signal();
    }

    private bool TryQueueRetransmissionLocked(ushort sequence)
    {
        var (stored, packet) = _history[sequence % HistoryCapacity];
        if (packet is null || stored != sequence
            || _time.GetElapsedTime(packet.SentAt) > HistoryLifetime
            || _queuedRepairs.Contains(sequence))
        {
            Interlocked.Increment(ref _unansweredNacks);
            return false;
        }

        var cost = packet.Payload.Length + VideoRetransmissionPayload.HeaderBytes + PacketOverheadBytes;
        if (_retransmissionTokens < cost)
        {
            // Over budget: let the viewer's recovery window expire into a key frame request
            // instead of adding retransmissions to a link that is already losing packets.
            Interlocked.Increment(ref _unansweredNacks);
            return false;
        }

        _retransmissionTokens -= cost;
        _queuedRepairs.Add(sequence);
        _retransmissions.Enqueue(new Retransmission(sequence, packet, cost));
        Interlocked.Add(ref _queuedBytes, cost);
        return true;
    }

    private bool DropUnstartedFramesLocked()
    {
        var dropped = false;
        var keep = new Queue<QueuedFrame>();
        while (_frames.TryDequeue(out var queued))
        {
            // A partly sent frame is finished so the viewer sees one damaged frame, not two.
            if (queued.Next > 0)
            {
                keep.Enqueue(queued);
                continue;
            }
            Interlocked.Add(ref _queuedBytes, -queued.RemainingBytes);
            Interlocked.Increment(ref _overflowedFrames);
            dropped = true;
        }
        while (keep.TryDequeue(out var queued)) _frames.Enqueue(queued);
        return dropped;
    }

    private void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a late frame or NACK arrived.
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
    }

    private void Run()
    {
        while (!_stopping)
        {
            try
            {
                if (!TrySendNext(out var wait))
                {
                    if (wait == Timeout.InfiniteTimeSpan) _signal.Wait(TimeSpan.FromMilliseconds(250));
                    else _waiter.Wait(wait);
                }
            }
            catch (ObjectDisposedException) when (_stopping)
            {
                break;
            }
            catch (Exception)
            {
                // The sink reports socket failures itself; a pacing thread must never die.
                _waiter.Wait(TimeSpan.FromMilliseconds(1));
            }
        }
    }

    /// <summary>Sends at most one packet; otherwise returns how long to wait.</summary>
    internal bool TrySendNext(out TimeSpan wait)
    {
        Retransmission? repair = null;
        QueuedFrame? frame = null;
        byte[] payload;
        uint timestamp;
        bool marker;
        lock (_gate)
        {
            if (_retransmissions.Count == 0 && _frames.Count == 0)
            {
                wait = Timeout.InfiniteTimeSpan;
                return false;
            }

            var now = _time.GetTimestamp();
            RefillLocked(now);
            if (_tokens < 0)
            {
                var rate = CurrentRateLocked(now);
                wait = TimeSpan.FromSeconds(Math.Min(0.005, -_tokens / rate));
                return false;
            }

            if (_retransmissions.TryDequeue(out var queuedRepair))
            {
                repair = queuedRepair;
                _queuedRepairs.Remove(queuedRepair.RepairedSequence);
                var original = queuedRepair.Packet;
                payload = VideoRetransmissionPayload.Encode(
                    queuedRepair.RepairedSequence, original.OriginalSequence, original.Timestamp,
                    original.Marker, original.Payload);
                timestamp = _lastMediaTimestamp;
                marker = false;
                _tokens -= queuedRepair.Cost;
                Interlocked.Add(ref _queuedBytes, -queuedRepair.Cost);
            }
            else
            {
                frame = _frames.Peek();
                payload = frame.Packets[frame.Next];
                timestamp = frame.Timestamp;
                marker = frame.Next == frame.Packets.Length - 1;
                var cost = payload.Length + PacketOverheadBytes;
                frame.Next++;
                frame.RemainingBytes -= cost;
                if (marker) _frames.Dequeue();
                _tokens -= cost;
                Interlocked.Add(ref _queuedBytes, -cost);
                _lastMediaTimestamp = timestamp;
            }
        }

        var sequence = _sink.Send(payload, timestamp, marker);
        wait = TimeSpan.Zero;
        if (sequence is not { } sent) return true;

        lock (_gate)
        {
            if (repair is { } answered)
            {
                // A NACK for this retransmission's own number repeats the same original.
                _history[sent % HistoryCapacity] = (sent, answered.Packet);
                Interlocked.Increment(ref _retransmittedPackets);
            }
            else
            {
                _history[sent % HistoryCapacity] = (sent, new SentPacket(payload, timestamp, marker, sent, _time.GetTimestamp()));
                if (marker) Interlocked.Increment(ref _framesSent);
            }
        }
        return true;
    }

    private void RefillLocked(long now)
    {
        var elapsed = _time.GetElapsedTime(_lastRefill, now).TotalSeconds;
        _lastRefill = now;
        var target = Volatile.Read(ref _targetBitrateKbps);
        var rate = CurrentRateLocked(now);
        if (double.IsPositiveInfinity(rate))
        {
            // A frame is at its deadline: forgive the pacing debt instead of storing infinity.
            _tokens = Math.Max(_tokens, 0);
        }
        else if (elapsed > 0)
        {
            _tokens = Math.Min(BurstBytes(rate), _tokens + rate * elapsed);
        }
        if (elapsed <= 0) return;
        _retransmissionTokens = Math.Min(
            RetransmissionCapacity(target),
            _retransmissionTokens + target * 1000 / 8.0 * RetransmissionBudgetFraction * elapsed);
    }

    /// <summary>
    /// Bytes per second: the paced rate, raised as far as needed for every queued frame to leave
    /// before its deadline. A frame already past its deadline is sent without pacing.
    /// </summary>
    private double CurrentRateLocked(long now)
    {
        var rate = PacingBytesPerSecond(Volatile.Read(ref _targetBitrateKbps));
        long cumulative = 0;
        foreach (var queued in _retransmissions) cumulative += queued.Cost;
        foreach (var queued in _frames)
        {
            cumulative += queued.RemainingBytes;
            var remaining = (MaximumQueueDelay - _time.GetElapsedTime(queued.EnqueuedAt, now)).TotalSeconds;
            if (remaining <= 0.0005) return double.PositiveInfinity;
            rate = Math.Max(rate, cumulative / remaining);
        }
        return rate;
    }

    private static double PacingBytesPerSecond(int targetKbps) =>
        Math.Max(MinimumPacingKbps, targetKbps * PacingFactor) * 1000 / 8.0;

    private static double BurstBytes(double bytesPerSecond) =>
        Math.Max(BurstPackets * PacketBytes, bytesPerSecond * 0.004);

    private static double RetransmissionCapacity(int targetKbps) =>
        Math.Max(64 * PacketBytes, targetKbps * 1000 / 8.0 * RetransmissionBudgetFraction / 4);

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        Signal();
        _thread?.Join(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            _frames.Clear();
            _retransmissions.Clear();
            _queuedRepairs.Clear();
            Array.Clear(_history);
            Interlocked.Exchange(ref _queuedBytes, 0);
        }
        _waiter.Dispose();
        _signal.Dispose();
    }

    private sealed class QueuedFrame(byte[][] packets, uint timestamp, long enqueuedAt, int bytes)
    {
        public byte[][] Packets { get; } = packets;
        public uint Timestamp { get; } = timestamp;
        public long EnqueuedAt { get; } = enqueuedAt;
        public int RemainingBytes { get; set; } = bytes;
        public int Next { get; set; }
    }

    private sealed record SentPacket(byte[] Payload, uint Timestamp, bool Marker, ushort OriginalSequence, long SentAt);

    private readonly record struct Retransmission(ushort RepairedSequence, SentPacket Packet, int Cost);
}
