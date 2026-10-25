using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Pipeline;
using SIPSorcery.Net;

namespace PeerOnQ.Media;

/// <summary>
/// Video RTP delivery: the sharer paces packets and answers NACKs; a negotiated viewer reorders,
/// requests and reassembles them. The packets themselves stay identical to SIPSorcery's VP8
/// packetization, so an older peer that never negotiated retransmission is unaffected.
/// </summary>
public sealed partial class WebRtcMediaSession
{
    private const string VideoRetransmissionSdpAttribute = "a=x-peeronq-video-rtx:1";

    /// <summary>SIPSorcery drops reassembled frames above this; its 1 MiB default loses 4K key frames.</summary>
    internal const int MaximumReconstructedVideoFrameBytes = 8 * 1024 * 1024;

    private const int MaximumNackPacketsPerStep = 32;
    private const int AssembledVideoQueueCapacity = 32;
    private static readonly TimeSpan VideoAssemblyTickInterval = TimeSpan.FromMilliseconds(10);

    private PacedVideoRtpSender? _videoPacer;
    private int _videoRetransmissionNegotiated;
    private VideoRtpFrameAssembler? _videoAssembler;
    private readonly Lock _videoAssemblyGate = new();
    private readonly VideoAssemblyOutput _videoAssemblyOutput = new();
    private Channel<byte[]>? _assembledVideoFrames;
    private Task? _assembledVideoLoop;
    private Timer? _videoAssemblyTimer;

    public bool IsVideoRetransmissionNegotiated => Volatile.Read(ref _videoRetransmissionNegotiated) != 0;

    /// <summary>Test seam: returns true for an outgoing video packet that the network "loses".</summary>
    internal Func<ushort, byte[], bool>? VideoPacketLossForTesting { get; set; }

    private long VideoPacketsRecovered
    {
        get
        {
            if (_videoAssembler is not { } assembler) return 0;
            lock (_videoAssemblyGate) return assembler.RecoveredPackets;
        }
    }

    private long VideoFramesSkipped
    {
        get
        {
            if (_videoAssembler is not { } assembler) return 0;
            lock (_videoAssemblyGate) return assembler.SkippedFrames;
        }
    }

    private void InitializeVideoTransport()
    {
        if (!_permissions.HasFlag(SessionPermission.ViewScreen)) return;

        if (Role == SessionRole.Sharer)
        {
            _videoPacer = new PacedVideoRtpSender(
                new SipsorceryVideoRtpSink(_peer, (sequence, payload) =>
                    VideoPacketLossForTesting?.Invoke(sequence, payload) == true),
                Math.Clamp(_profile.MaxBitrateKbps, 100, 50_000));
            _videoPacer.QueueOverflowed += (_, _) => _lossRecovery.RequestKeyFrame();
            return;
        }

        if (_peer.VideoStream is { } stream)
            stream.MaxReconstructedVideoFrameSize = MaximumReconstructedVideoFrameBytes;
        _videoAssembler = new VideoRtpFrameAssembler();
        _assembledVideoFrames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(AssembledVideoQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _assembledVideoLoop = Task.Run(() => DispatchAssembledVideoAsync(_assembledVideoFrames.Reader, _lifetime.Token));
    }

    internal static bool HasVideoRetransmission(string sdp) =>
        sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line, VideoRetransmissionSdpAttribute, StringComparison.Ordinal));

    internal static string AdvertiseVideoRetransmission(string sdp)
    {
        if (HasVideoRetransmission(sdp)) return sdp;
        var newline = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Concat(sdp.TrimEnd('\r', '\n'), newline, VideoRetransmissionSdpAttribute, newline);
    }

    private void SetVideoRetransmissionNegotiated(bool negotiated)
    {
        var enabled = negotiated && (_videoPacer is not null || _videoAssembler is not null);
        if (Interlocked.Exchange(ref _videoRetransmissionNegotiated, enabled ? 1 : 0) == (enabled ? 1 : 0)) return;
        if (_videoPacer is { } pacer) pacer.RetransmissionEnabled = enabled;
        if (_videoAssembler is not { } assembler) return;

        lock (_videoAssemblyGate) assembler.Reset();
        if (enabled && !_lifetime.IsCancellationRequested)
            _videoAssemblyTimer ??= new Timer(_ => TickVideoAssembly(), null, VideoAssemblyTickInterval, VideoAssemblyTickInterval);
        // Either framer may hold half a frame from before the switch; start from a key frame.
        RequestPictureLossIndication();
    }

    private void AssembleVideoPacket(RTPPacket packet, bool sourceChanged)
    {
        if (_videoAssembler is not { } assembler) return;
        var payload = packet.GetPayloadBytes();
        List<ushort>? nacks;
        bool keyFrame;
        lock (_videoAssemblyGate)
        {
            if (sourceChanged) assembler.Reset();
            _videoAssemblyOutput.Clear();
            assembler.Push(packet.Header.SequenceNumber, packet.Header.Timestamp, packet.Header.MarkerBit != 0,
                payload, _videoAssemblyOutput);
            PublishVideoAssemblyLocked(out nacks, out keyFrame);
        }

        CompleteVideoAssemblyStep(nacks, keyFrame);
    }

    private void TickVideoAssembly()
    {
        try
        {
            if (_videoAssembler is not { } assembler || State != MediaConnectionState.Connected
                || !IsVideoRetransmissionNegotiated)
                return;
            var rtt = GetIceRttMs();
            List<ushort>? nacks;
            bool keyFrame;
            lock (_videoAssemblyGate)
            {
                assembler.RoundTripTime = rtt > 0 ? TimeSpan.FromMilliseconds(rtt) : TimeSpan.Zero;
                _videoAssemblyOutput.Clear();
                assembler.Tick(_videoAssemblyOutput);
                PublishVideoAssemblyLocked(out nacks, out keyFrame);
            }

            CompleteVideoAssemblyStep(nacks, keyFrame);
        }
        catch (Exception ex)
        {
            // A timer callback must never throw; the next tick retries.
            LogVideoAssemblyError(_log, ex);
        }
    }

    /// <summary>Hands complete frames to the ordered decode worker while the assembler is locked.</summary>
    private void PublishVideoAssemblyLocked(out List<ushort>? nacks, out bool keyFrame)
    {
        keyFrame = _videoAssemblyOutput.KeyFrameRequested;
        foreach (var assembled in _videoAssemblyOutput.Frames)
        {
            // A decoder that cannot keep up would only fall further behind; skip to a key frame.
            if (_assembledVideoFrames?.Writer.TryWrite(assembled.Frame) != true) keyFrame = true;
        }
        nacks = _videoAssemblyOutput.Nacks.Count == 0 ? null : [.. _videoAssemblyOutput.Nacks];
    }

    private void CompleteVideoAssemblyStep(List<ushort>? nacks, bool keyFrame)
    {
        if (nacks is not null) SendVideoNacks(nacks);
        if (keyFrame) RequestPictureLossIndication();
    }

    private async Task DispatchAssembledVideoAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    ProcessReceivedVideoFrame(frame);
                }
                catch (Exception ex)
                {
                    LogUndecodableFrame(_log, ex);
                    RequestPictureLossIndication();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session teardown.
        }
    }

    /// <summary>Packs ascending sequence numbers into RFC 4585 generic NACK items.</summary>
    internal static List<(ushort PacketId, ushort LostBitmask)> PackNacks(IReadOnlyList<ushort> sequences)
    {
        var items = new List<(ushort, ushort)>();
        var index = 0;
        while (index < sequences.Count)
        {
            var packetId = sequences[index++];
            ushort bitmask = 0;
            while (index < sequences.Count)
            {
                var distance = unchecked((ushort)(sequences[index] - packetId));
                if (distance is 0 or > 16) break;
                bitmask |= (ushort)(1 << (distance - 1));
                index++;
            }
            items.Add((packetId, bitmask));
        }
        return items;
    }

    private void SendVideoNacks(List<ushort> sequences)
    {
        if (State != MediaConnectionState.Connected
            || !TryGetVideoFeedbackSsrcs(out var senderSsrc, out var mediaSsrc))
            return;
        try
        {
            foreach (var (packetId, bitmask) in PackNacks(sequences).Take(MaximumNackPacketsPerStep))
            {
                _peer.SendRtcpFeedback(
                    SDPMediaTypesEnum.video,
                    new RTCPFeedback(senderSsrc, mediaSsrc, RTCPFeedbackTypesEnum.NACK, packetId, bitmask));
            }
        }
        catch (Exception ex)
        {
            LogVideoAssemblyError(_log, ex);
        }
    }

    private void CloseVideoTransport()
    {
        Interlocked.Exchange(ref _videoAssemblyTimer, null)?.Dispose();
        _assembledVideoFrames?.Writer.TryComplete();
        _videoPacer?.Dispose();
        if (_videoAssembler is { } assembler)
            lock (_videoAssemblyGate) assembler.Reset();
    }

    [LoggerMessage(1400, LogLevel.Debug, "Video packet assembly step failed")]
    private static partial void LogVideoAssemblyError(ILogger logger, Exception exception);

    /// <summary>Sends prepared payloads with SIPSorcery's own sequence numbers and SRTP context.</summary>
    private sealed class SipsorceryVideoRtpSink(RTCPeerConnection peer, Func<ushort, byte[], bool> isLost)
        : IVideoRtpPacketSink
    {
        private int _payloadType = -1;

        public uint InitialTimestamp => peer.VideoStream?.LocalTrack?.Timestamp ?? 0;

        public ushort? Send(byte[] payload, uint timestamp, bool marker)
        {
            var stream = peer.VideoStream;
            var track = stream?.LocalTrack;
            if (stream is null || track is null) return null;
            if (_payloadType < 0)
            {
                try
                {
                    _payloadType = stream.GetSendingFormat().ID;
                }
                catch (Exception)
                {
                    // Not negotiated yet: SIPSorcery would not send either.
                    return null;
                }
            }

            var sequence = track.GetNextSeqNum();
            if (isLost(sequence, payload)) return sequence;
            stream.SetRtpHeaderExtensionValue(TransportWideCCExtension.RTP_HEADER_EXTENSION_URI, null);
            stream.SendRtpRaw(payload, timestamp, marker ? 1 : 0, _payloadType, sequence);
            return sequence;
        }
    }
}
