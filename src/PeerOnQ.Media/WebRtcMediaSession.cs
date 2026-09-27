using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Codecs;
using PeerOnQ.Media.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Net;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace PeerOnQ.Media;

/// <summary>
/// A real WebRTC media/control peer connection plus an optional physically separate bulk-data
/// peer connection. Both remain permission-scoped and carry only authenticated application data.
/// </summary>
public sealed partial class WebRtcMediaSession : IMediaSession
{
    private const int MaximumQueuedBulkRecords = 8;
    private readonly RTCPeerConnection _peer;
    private readonly RTCPeerConnection? _inputPeer;
    private readonly RTCPeerConnection? _bulkPeer;
    private readonly MediaStatisticsCollector _statistics = new();
    private readonly VideoFrameQueue _queue;
    // A capacity-one signal wakes the encoder as soon as a newer desktop frame replaces the
    // previous one. This removes the otherwise unavoidable half-frame polling delay without
    // allowing stale work to accumulate.
    private readonly SemaphoreSlim _encodeWake = new(0, 1);
    private readonly FrameRateLimiter _rateLimiter;
    private readonly IScreenCaptureSource? _capture;
    private readonly Vp8ScreenEncoder? _encoder;
    private readonly Vp8ScreenDecoder? _decoder;
    private readonly IEncodedVideoFrameSink? _encodedVideoSink;
    private readonly Lock _decoderGate = new();
    private readonly MediaProfile _profile;
    private readonly IceConfiguration _iceConfiguration;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SessionPermission _permissions;
    private readonly Vp8LossRecoveryController _lossRecovery = new();
    private readonly RtcpNetworkFeedback _networkFeedback = new();
    private readonly IConnectionPolicyProvider _connectionPolicy = new ConnectionPolicyProvider();
    private readonly VideoSecurityFailureBudget _videoSecurityFailures = new(maxConsecutiveFailures: 8);
    private readonly BulkDataFrameReassembler _bulkDataReassembler = new();
    private readonly Channel<ReadOnlyMemory<byte>> _bulkInboundRecords = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(MaximumQueuedBulkRecords)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly Lock _auxiliaryIceGate = new();
    private readonly Dictionary<DataLane, List<(string Candidate, string? SdpMid, ushort SdpMLineIndex)>>
        _pendingAuxiliaryIceCandidates = new()
        {
            [DataLane.Input] = [],
            [DataLane.Bulk] = [],
        };
    private RTCDataChannel? _dataChannel;
    private RTCDataChannel? _inputDataChannel;
    private RTCDataChannel? _bulkDataChannel;
    private Task<RTCDataChannel>? _dataChannelCreation;
    private Task<RTCDataChannel>? _inputDataChannelCreation;
    private Task<RTCDataChannel>? _bulkDataChannelCreation;
    private ISessionTrafficProtector? _trafficProtector;

    private Task? _encodeLoop;
    private Task? _adaptLoop;
    private Task? _bulkInboundDispatchLoop;
    private int _disposed;
    private long _lastDrops;
    private long _lastEncoded;
    private long _remoteVideoSsrc;
    private int _targetBitrateKbps;
    private int _transferPriorityMode = (int)TransferPriorityMode.Balanced;
    private int _videoFrameTelemetryNegotiated;
    private int _inputAcknowledgementNegotiated;
    private int _inputDataLaneNegotiated;
    private int _bulkDataLaneNegotiated;
    private int _nativeBulkTransportNegotiated;
    private int _hasPeerClockEstimate;
    private long _peerClockOffsetMicroseconds;
    private long _peerClockUncertaintyMicroseconds;
    private long _peerClockEstimateExpiresAtUnixMicroseconds;
    private long _inputDataRecordsSent;
    private long _nextBulkRecordId;
    private int _bulkDataFragmentBytes;
    private long _bulkQueueBudgetBytes;
    private long _bulkDataRecordsSent;
    private long _bulkDataFragmentsSent;
    private RemoteSessionQualityFeedback? _remoteQualityFeedback;
    private long _remoteQualityFeedbackTimestamp;

    /// <summary>Adapts frame rate and resolution from RTCP loss and local backpressure.</summary>
    public AdaptiveQualityController? Adaptive { get; private set; }

    /// <summary>Which encoder this session runs on, and why. Never claims unused acceleration.</summary>
    public EncoderSelection Encoder { get; private set; } =
        new(VideoEncoderKind.SoftwareVp8, false, "software VP8");

    private WebRtcMediaSession(
        SessionId sessionId,
        SessionRole role,
        RTCPeerConnection peer,
        RTCPeerConnection? inputPeer,
        RTCPeerConnection? bulkPeer,
        MediaProfile profile,
        IceConfiguration iceConfiguration,
        IScreenCaptureSource? capture,
        SessionPermission permissions,
        ILogger log,
        IEncodedVideoFrameSink? encodedVideoSink = null)
    {
        SessionId = sessionId;
        Role = role;
        _peer = peer;
        _inputPeer = inputPeer;
        _bulkPeer = bulkPeer;
        _profile = profile;
        _iceConfiguration = iceConfiguration;
        _capture = capture;
        _permissions = permissions;
        _log = log;
        _encodedVideoSink = encodedVideoSink;
        if (_bulkPeer is not null)
            _bulkInboundDispatchLoop = Task.Run(() => DispatchBulkRecordsAsync(_lifetime.Token));
        // Remote control favors the newest desktop state over a backlog of stale 4K frames.
        _queue = new VideoFrameQueue(capacity: 1);
        _rateLimiter = new FrameRateLimiter(profile.TargetFps);
        _targetBitrateKbps = Math.Clamp(profile.MaxBitrateKbps, 100, 50_000);

        if (role == SessionRole.Sharer)
        {
            _encoder = new Vp8ScreenEncoder(profile.MaxBitrateKbps, profile.TargetFps);
        }
        else if (_encodedVideoSink is null)
        {
            _decoder = new Vp8ScreenDecoder();
        }
        else
        {
            _encodedVideoSink.FramePresented += OnEncodedVideoFramePresented;
        }

        WirePeerEvents();
    }

    public SessionId SessionId { get; }
    public SessionRole Role { get; }
    public MediaConnectionState State { get; private set; } = MediaConnectionState.New;

    public event EventHandler<MediaConnectionState>? StateChanged;
    public event EventHandler<(string Candidate, string? SdpMid, ushort SdpMLineIndex)>? LocalIceCandidate;
    public event EventHandler<RemoteVideoFrame>? RemoteFrameReceived;
    public event EventHandler? DataChannelReady;
    public event EventHandler<ReadOnlyMemory<byte>>? DataMessageReceived;
    public event EventHandler<string>? SecurityError;
    public bool IsDataChannelReady => _dataChannel?.readyState == RTCDataChannelState.open;
    public bool UsesPlatformVideoDecoder => _encodedVideoSink is not null;
    public bool IsVideoFrameTelemetryNegotiated =>
        Volatile.Read(ref _videoFrameTelemetryNegotiated) != 0;
    public bool IsInputAcknowledgementNegotiated =>
        Volatile.Read(ref _inputAcknowledgementNegotiated) != 0;
    public bool IsInputDataLaneNegotiated =>
        Volatile.Read(ref _inputDataLaneNegotiated) != 0;
    public bool IsInputDataLaneReady =>
        IsInputDataLaneNegotiated
        && Volatile.Read(ref _inputDataChannel)?.readyState == RTCDataChannelState.open;
    public bool IsBulkDataLaneNegotiated =>
        Volatile.Read(ref _bulkDataLaneNegotiated) != 0;
    public bool IsBulkDataLaneReady =>
        IsBulkDataLaneNegotiated
        && _bulkDataChannel?.readyState == RTCDataChannelState.open;
    public bool IsNativeBulkTransportNegotiated =>
        Volatile.Read(ref _nativeBulkTransportNegotiated) != 0;
    internal bool UsesDedicatedInputPeerConnection => _inputPeer is not null;
    internal bool UsesDedicatedBulkPeerConnection => _bulkPeer is not null;

    private const string CollaborationChannelLabel = "peeronq.secure.v1";
    private const string InputDataChannelLabel = "peeronq.input.v1";
    private const string BulkDataChannelLabel = "peeronq.bulk.v1";
    private const string CollaborationProtocol = "peeronq.hybrid-pq.v1";
    private const string VideoFrameTelemetrySdpAttribute = "a=x-peeronq-video-frame-timing:1";
    private const string InputAcknowledgementSdpAttribute = "a=x-peeronq-input-ack:1";
    private const string InputDataLaneSdpAttribute = "a=x-peeronq-input-data-lane:1";
    private const string BulkDataLaneSdpAttribute = "a=x-peeronq-bulk-data-lane:1";
    private const string NativeBulkTransportSdpAttribute = "a=x-peeronq-native-bulk:1";
    private const string InputDescriptionSdpPrefix = "a=x-peeronq-input-description:";
    private const string BulkDescriptionSdpPrefix = "a=x-peeronq-bulk-description:";
    private const string InputIceMidPrefix = "peeronq-input:";
    private const string BulkIceMidPrefix = "peeronq-bulk:";
    private const int MaximumAuxiliaryDescriptionBytes = 16 * 1024;
    private const int MaximumEncodedAuxiliaryDescriptionCharacters = 22 * 1024;
    private const int MaximumPendingAuxiliaryIceCandidates = 128;
    internal static readonly TimeSpan RemoteQualityFeedbackLifetime = TimeSpan.FromSeconds(5);
    private const int MaximumDataMessageBytes = 1024 * 1024;
    private const ulong MaximumBufferedBytes = 4UL * 1024 * 1024;
    internal const ulong MaximumInputBufferedBytes = 64UL * 1024;
    // This is a bounded memory/backpressure window, not a bandwidth cap. Keeping several MiB in
    // flight lets high-bandwidth LAN/WAN links run at line rate while interactive input can preempt.
    private readonly SemaphoreSlim _dataSendGate = new(1, 1);
    private readonly SemaphoreSlim _inputDataSendGate = new(1, 1);
    private readonly SemaphoreSlim _bulkDataSendGate = new(1, 1);
    private readonly SemaphoreSlim _bulkRecordSendGate = new(1, 1);
    private int _foregroundDataWaiters;

    private enum DataLane
    {
        Primary,
        Input,
        Bulk,
    }

    [LoggerMessage(1000, LogLevel.Information, "Session {SessionId} encoder: {Encoder}")]
    private static partial void LogEncoderSelected(ILogger logger, SessionId sessionId, EncoderSelection encoder);

    [LoggerMessage(1001, LogLevel.Information, "Session {SessionId} media state {State}")]
    private static partial void LogMediaStateChanged(
        ILogger logger,
        SessionId sessionId,
        MediaConnectionState state);

    [LoggerMessage(1002, LogLevel.Debug, "Ignoring an unreadable RTCP report")]
    private static partial void LogUnreadableRtcpReport(ILogger logger, Exception exception);

    [LoggerMessage(1003, LogLevel.Warning, "Dropping an undecodable video frame")]
    private static partial void LogUndecodableFrame(ILogger logger, Exception exception);

    [LoggerMessage(1004, LogLevel.Warning, "Rejected an unauthorized or unknown WebRTC data channel")]
    private static partial void LogRejectedDataChannel(ILogger logger);

    [LoggerMessage(1005, LogLevel.Warning, "Rejected collaboration message with {Length} bytes")]
    private static partial void LogRejectedCollaborationMessage(ILogger logger, int length);

    [LoggerMessage(1006, LogLevel.Warning, "Collaboration data channel error: {Error}")]
    private static partial void LogDataChannelError(ILogger logger, string error);

    [LoggerMessage(1007, LogLevel.Information, "Collaboration data channel closed")]
    private static partial void LogDataChannelClosed(ILogger logger);

    [LoggerMessage(1008, LogLevel.Information, "Session {SessionId} quality {Level}")]
    private static partial void LogQualityChanged(ILogger logger, SessionId sessionId, QualityLevel level);

    [LoggerMessage(1009, LogLevel.Warning, "Adaptive quality loop stopped")]
    private static partial void LogAdaptiveLoopStopped(ILogger logger, Exception exception);

    [LoggerMessage(1010, LogLevel.Warning, "Encoder iteration failed")]
    private static partial void LogEncoderIterationFailed(ILogger logger, Exception exception);

    [LoggerMessage(1011, LogLevel.Debug, "Ignoring an error while closing the peer connection")]
    private static partial void LogPeerCloseError(ILogger logger, Exception exception);

    [LoggerMessage(1012, LogLevel.Debug, "Unable to request a VP8 recovery key frame")]
    private static partial void LogPictureLossIndicationError(ILogger logger, Exception exception);

    [LoggerMessage(1013, LogLevel.Warning,
        "Rejected unauthenticated video frame ({Reason}); consecutive failures {Failures}/{Limit}")]
    private static partial void LogRejectedUnauthenticatedVideoFrame(
        ILogger logger,
        string reason,
        int failures,
        int limit);

    public static RTCConfiguration DefaultConfiguration => BuildConfiguration(IceConfiguration.DirectOnly);

    public static RTCConfiguration BuildConfiguration(
        IceConfiguration iceConfiguration,
        SessionPermission permissions = SessionPermission.None)
    {
        // A file-only session has no non-file payload that could safely benefit from TURN.
        // Mixed sessions keep TURN available for screen/input; file records still use the
        // separately configured direct-only bulk peer or the authenticated file relay.
        var directFileTransport = permissions == SessionPermission.FileTransfer;
        if (directFileTransport && iceConfiguration.TransportPolicy == IceTransportPolicy.RelayOnly)
            throw new InvalidOperationException("direct_p2p_required");

        // Signaling issues a fresh credential for this accepted session. Do not compare its
        // server-authored expiry with the untrusted client wall clock: a skewed workstation can
        // otherwise discard every TURN URL while direct LAN ICE keeps working. Coturn remains the
        // authoritative expiry and HMAC validator when an allocation is attempted.
        var servers = iceConfiguration.Servers
            .SelectMany(server => server.Urls
                .Where(url => !directFileTransport || !IsTurnUrl(url))
                .Select(url => new RTCIceServer
                {
                    urls = url,
                    username = server.Username,
                    credential = server.Credential,
                    credentialType = RTCIceCredentialType.password,
                }))
            .ToList();

        if (iceConfiguration.TransportPolicy == IceTransportPolicy.RelayOnly
            && servers.All(server => !server.urls.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                                     && !server.urls.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Relay-only policy requires a non-expired TURN server.");
        }

        return new RTCConfiguration
        {
            // ICE itself ranks host and server-reflexive candidates ahead of relays. Relay-only
            // is reserved for diagnostics/policy tests and never selected implicitly.
            iceServers = servers,
            iceTransportPolicy = iceConfiguration.TransportPolicy == IceTransportPolicy.RelayOnly
                ? RTCIceTransportPolicy.relay
                : RTCIceTransportPolicy.all,
            X_ICEIncludeAllInterfaceAddresses = true,
        };
    }

    private static bool IsTurnUrl(string url) =>
        url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase);

    public static WebRtcMediaSession CreateSharer(
        SessionId sessionId,
        IScreenCaptureSource? capture,
        MediaProfile profile,
        ILogger? logger = null,
        RTCConfiguration? configuration = null,
        VideoEncoderSelector? encoderSelector = null,
        IceConfiguration? iceConfiguration = null,
        SessionPermission permissions = SessionPermission.ViewScreen)
    {
        Phase1SessionScope.EnsureInteractiveMediaPermissions(permissions);
        var peerConfiguration = configuration ?? DefaultConfiguration;
        var peer = new RTCPeerConnection(peerConfiguration);
        var inputPeer = permissions.HasFlag(SessionPermission.ControlInput)
            ? new RTCPeerConnection(peerConfiguration)
            : null;
        var bulkPeer = CreateDirectBulkPeer(permissions, iceConfiguration);

        if (permissions.HasFlag(SessionPermission.ViewScreen))
        {
            var track = new MediaStreamTrack(
                new VideoFormat(VideoCodecsEnum.VP8, 96),
                MediaStreamStatusEnum.SendOnly);
            peer.addTrack(track);
        }

        var session = new WebRtcMediaSession(
            sessionId,
            SessionRole.Sharer,
            peer,
            inputPeer,
            bulkPeer,
            profile,
            iceConfiguration ?? IceConfiguration.DirectOnly,
            capture,
            permissions,
            logger ?? NullLogger.Instance);

        session.Encoder = (encoderSelector ?? new VideoEncoderSelector(() => false)).Select();
        LogEncoderSelected(session._log, sessionId, session.Encoder);

        // Every interactive profile supplies a maximum, not a promise to congest the link.
        // Keep the selected profile while its measured loss, jitter, RTT, or local queue pressure
        // is healthy; otherwise step down frame rate before resolution so current control input
        // reaches the peer instead of waiting behind stale video.
        session.Adaptive = new AdaptiveQualityController(
            profile.TargetFps,
            profile.MaxBitrateKbps,
            policy: session._connectionPolicy,
            allowResolutionDownscale: profile.Quality != QualityProfile.Quality);
        session.Adaptive.LevelChanged += session.OnQualityLevelChanged;

        if (capture is not null && permissions.HasFlag(SessionPermission.ViewScreen))
        {
            capture.FrameArrived += session.OnCaptureFrame;
            session._encodeLoop = Task.Run(() => session.EncodeLoopAsync(session._lifetime.Token));
            session._adaptLoop = Task.Run(() => session.AdaptLoopAsync(session._lifetime.Token));
        }

        session._dataChannelCreation = peer.createDataChannel(
            CollaborationChannelLabel,
            new RTCDataChannelInit { ordered = true, protocol = CollaborationProtocol });
        if (permissions.HasFlag(SessionPermission.ControlInput))
        {
            session._inputDataChannelCreation = inputPeer!.createDataChannel(
                InputDataChannelLabel,
                new RTCDataChannelInit { ordered = true, protocol = CollaborationProtocol });
        }
        if (permissions.HasFlag(SessionPermission.FileTransfer) && bulkPeer is not null)
        {
            session._bulkDataChannelCreation = bulkPeer.createDataChannel(
                BulkDataChannelLabel,
                new RTCDataChannelInit { ordered = true, protocol = CollaborationProtocol });
        }

        return session;
    }

    public static WebRtcMediaSession CreateViewer(
        SessionId sessionId,
        ILogger? logger = null,
        RTCConfiguration? configuration = null,
        IceConfiguration? iceConfiguration = null,
        SessionPermission permissions = SessionPermission.ViewScreen,
        IEncodedVideoFrameSink? encodedVideoSink = null)
    {
        Phase1SessionScope.EnsureInteractiveMediaPermissions(permissions);
        var peerConfiguration = configuration ?? DefaultConfiguration;
        var peer = new RTCPeerConnection(peerConfiguration);
        var inputPeer = permissions.HasFlag(SessionPermission.ControlInput)
            ? new RTCPeerConnection(peerConfiguration)
            : null;
        var bulkPeer = CreateDirectBulkPeer(permissions, iceConfiguration);

        if (permissions.HasFlag(SessionPermission.ViewScreen))
        {
            var track = new MediaStreamTrack(
                new VideoFormat(VideoCodecsEnum.VP8, 96),
                MediaStreamStatusEnum.RecvOnly);
            peer.addTrack(track);
        }

        return new WebRtcMediaSession(
            sessionId,
            SessionRole.Viewer,
            peer,
            inputPeer,
            bulkPeer,
            MediaProfile.Conservative,
            iceConfiguration ?? IceConfiguration.DirectOnly,
            null,
            permissions,
            logger ?? NullLogger.Instance,
            encodedVideoSink);
    }

    private static RTCPeerConnection? CreateDirectBulkPeer(
        SessionPermission permissions,
        IceConfiguration? iceConfiguration)
    {
        if (!permissions.HasFlag(SessionPermission.FileTransfer)
            || iceConfiguration?.TransportPolicy == IceTransportPolicy.RelayOnly)
        {
            return null;
        }

        var configuration = iceConfiguration is null
            ? DefaultConfiguration
            : BuildConfiguration(iceConfiguration, SessionPermission.FileTransfer);
        return new RTCPeerConnection(configuration);
    }

    private void WirePeerEvents()
    {
        _peer.ondatachannel += channel => ConfigureDataChannel(channel, DataLane.Primary);
        if (_inputPeer is not null)
        {
            _inputPeer.ondatachannel += channel => ConfigureDataChannel(channel, DataLane.Input);
            _inputPeer.onicecandidate += candidate =>
            {
                if (candidate is null) return;
                PublishOrQueueAuxiliaryIceCandidate(
                    DataLane.Input,
                    (candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
            };
        }
        if (_bulkPeer is not null)
        {
            _bulkPeer.ondatachannel += channel => ConfigureDataChannel(channel, DataLane.Bulk);
            _bulkPeer.onicecandidate += candidate =>
            {
                if (candidate is null) return;
                PublishOrQueueAuxiliaryIceCandidate(
                    DataLane.Bulk,
                    (candidate.candidate, candidate.sdpMid, candidate.sdpMLineIndex));
            };
        }

        _peer.onicecandidate += candidate =>
        {
            if (candidate is null) return;

            LocalIceCandidate?.Invoke(this, (
                candidate.candidate,
                candidate.sdpMid,
                candidate.sdpMLineIndex));
        };

        _peer.onconnectionstatechange += state =>
        {
            var mapped = state switch
            {
                RTCPeerConnectionState.@new => MediaConnectionState.New,
                RTCPeerConnectionState.connecting => MediaConnectionState.Connecting,
                RTCPeerConnectionState.connected => MediaConnectionState.Connected,
                RTCPeerConnectionState.disconnected => MediaConnectionState.Disconnected,
                RTCPeerConnectionState.failed => MediaConnectionState.Failed,
                RTCPeerConnectionState.closed => MediaConnectionState.Closed,
                _ => MediaConnectionState.New,
            };

            _networkFeedback.SetConnected(mapped == MediaConnectionState.Connected);
            State = mapped;
            if (mapped == MediaConnectionState.Connected && Role == SessionRole.Sharer)
                _encoder?.ForceKeyFrame();
            LogMediaStateChanged(_log, SessionId, mapped);
            StateChanged?.Invoke(this, mapped);
        };

        if (Role == SessionRole.Sharer)
        {
            // RTCP receiver reports carry the viewer's observed loss: the honest input for
            // deciding whether the link can carry the current quality.
            _peer.OnReceiveReport += (_, _, report) =>
            {
                try
                {
                    if (IsPictureLossIndication(report))
                    {
                        // RTCP arrives on the transport callback. Keep the native encoder on
                        // its single encode loop and only publish a coalescing atomic request.
                        _lossRecovery.RequestKeyFrame();
                    }

                    var reception = report?.ReceiverReport?.ReceptionReports?.FirstOrDefault();
                    if (reception is null) return;

                    _networkFeedback.Report(reception.FractionLost / 256.0, reception.Jitter / 90.0);
                }
                catch (Exception ex)
                {
                    LogUnreadableRtcpReport(_log, ex);
                }
            };
        }

        if (Role == SessionRole.Viewer)
        {
            _peer.OnRtpPacketReceived += (_, mediaType, packet) =>
            {
                if (mediaType == SDPMediaTypesEnum.video)
                {
                    // Keep the SSRC from the traffic that actually reached this viewer. This
                    // also works on TURN paths where the library does not update RemoteTrack.Ssrc.
                    Volatile.Write(ref _remoteVideoSsrc, packet.Header.SyncSource);
                }
            };

            _peer.OnVideoFrameReceived += (_, _, frame, format) =>
            {
                var protector = Volatile.Read(ref _trafficProtector);
                if (protector is null || !protector.IsEstablished) return;
                if (!protector.TryUnprotectVideoFrame(frame, out var encodedFrame, out var securityError))
                {
                    // Video is unordered/lossy. An authenticated newer epoch/frame can legitimately
                    // overtake an older one at a rekey boundary; reject the stale frame without
                    // letting a replay become a session-level denial of service.
                    if (string.Equals(securityError, "stale_video_record", StringComparison.Ordinal)) return;

                    // RTP is intentionally lossy. A truncated frame remains untrusted and is never
                    // decoded, but one bad frame must not end an otherwise authenticated session.
                    // Ask for a fresh key frame and retain the fail-closed session abort for a
                    // sustained run of failures that indicates key/state mismatch or active abuse.
                    var reason = securityError ?? "video_authentication_failed";
                    var failures = _videoSecurityFailures.RecordFailure();
                    LogRejectedUnauthenticatedVideoFrame(
                        _log,
                        reason,
                        failures,
                        _videoSecurityFailures.MaximumConsecutiveFailures);
                    RequestPictureLossIndication();
                    if (_videoSecurityFailures.IsExhausted)
                        SecurityError?.Invoke(this, reason);
                    return;
                }
                _videoSecurityFailures.RecordSuccess();
                byte[]? telemetryEncodedFrame = null;
                try
                {
                    var captureTimestampUnixMicroseconds = 0L;
                    var captureSequenceNumber = 0L;
                    var vp8Frame = encodedFrame;
                    if (IsVideoFrameTelemetryNegotiated
                        && VideoFrameTelemetryCodec.LooksLikeEnvelope(encodedFrame))
                    {
                        if (!VideoFrameTelemetryCodec.TryDecode(
                                encodedFrame,
                                out captureTimestampUnixMicroseconds,
                                out captureSequenceNumber,
                                out telemetryEncodedFrame))
                        {
                            RequestPictureLossIndication();
                            return;
                        }

                        vp8Frame = telemetryEncodedFrame;
                    }

                    if (_encodedVideoSink is not null)
                    {
                        if (!_encodedVideoSink.TrySubmit(new AuthenticatedVp8Frame(
                                vp8Frame,
                                captureTimestampUnixMicroseconds,
                                captureSequenceNumber,
                                GetPeerClockEstimate())))
                        {
                            RequestPictureLossIndication();
                        }
                        return;
                    }

                    // One decoder for the whole session: VP8 inter frames reference the
                    // decoder's previous state, so a per-frame decoder only ever produces
                    // key frames.
                    VideoSample[] images;
                    lock (_decoderGate)
                    {
                        // The VP8 decoder returns packed 24-bit BGR; the format travels with
                        // the frame so the renderer never has to infer the layout.
                        images = _decoder!.Decode(vp8Frame);
                    }

                    if (images.Length == 0)
                    {
                        RequestPictureLossIndication();
                        return;
                    }

                    foreach (var image in images)
                    {
                        var decodedTimestamp = Stopwatch.GetTimestamp();
                        _statistics.FrameDecoded(vp8Frame.Length, (int)image.Width, (int)image.Height);
                        RemoteFrameReceived?.Invoke(this, new RemoteVideoFrame(
                            (int)image.Width,
                            (int)image.Height,
                            image.Sample,
                            RemotePixelFormat.Bgr24,
                            TimeSpan.Zero)
                        {
                            BufferOwnershipCanTransfer = true,
                            PipelineDecodedTimestamp = decodedTimestamp,
                            CaptureTimestampUnixMicroseconds = captureTimestampUnixMicroseconds,
                            CaptureSequenceNumber = captureSequenceNumber,
                            PeerClockEstimate = GetPeerClockEstimate(),
                        });
                    }
                }
                catch (Exception ex)
                {
                    LogUndecodableFrame(_log, ex);
                    RequestPictureLossIndication();
                }
                finally
                {
                    if (telemetryEncodedFrame is not null)
                        CryptographicOperations.ZeroMemory(telemetryEncodedFrame);
                    CryptographicOperations.ZeroMemory(encodedFrame);
                }
            };
        }
    }

    private void PublishOrQueueAuxiliaryIceCandidate(
        DataLane lane,
        (string Candidate, string? SdpMid, ushort SdpMLineIndex) candidate)
    {
        if (lane is not (DataLane.Input or DataLane.Bulk))
            throw new ArgumentOutOfRangeException(nameof(lane));
        var routedCandidate = candidate with
        {
            SdpMid = string.Concat(
                lane == DataLane.Input ? InputIceMidPrefix : BulkIceMidPrefix,
                candidate.SdpMid),
        };
        lock (_auxiliaryIceGate)
        {
            if (!IsAuxiliaryDataLaneNegotiated(lane))
            {
                var pending = _pendingAuxiliaryIceCandidates[lane];
                if (pending.Count < MaximumPendingAuxiliaryIceCandidates)
                    pending.Add(routedCandidate);
                return;
            }
        }

        LocalIceCandidate?.Invoke(this, routedCandidate);
    }

    private void OnEncodedVideoFramePresented(
        object? sender,
        EncodedVideoFramePresentation presentation) =>
        _statistics.FrameDecoded(
            presentation.EncodedBytes,
            presentation.Frame.Width,
            presentation.Frame.Height);

    private void CompleteAuxiliaryDataLaneNegotiation(DataLane lane, bool negotiated)
    {
        if (lane is not (DataLane.Input or DataLane.Bulk))
            throw new ArgumentOutOfRangeException(nameof(lane));
        (string Candidate, string? SdpMid, ushort SdpMLineIndex)[] candidates;
        lock (_auxiliaryIceGate)
        {
            if (lane == DataLane.Input)
                Volatile.Write(ref _inputDataLaneNegotiated, negotiated ? 1 : 0);
            else
                Volatile.Write(ref _bulkDataLaneNegotiated, negotiated ? 1 : 0);
            var pending = _pendingAuxiliaryIceCandidates[lane];
            candidates = negotiated ? [.. pending] : [];
            pending.Clear();
        }

        foreach (var candidate in candidates)
            LocalIceCandidate?.Invoke(this, candidate);
    }

    private bool IsAuxiliaryDataLaneNegotiated(DataLane lane) => lane switch
    {
        DataLane.Input => IsInputDataLaneNegotiated,
        DataLane.Bulk => IsBulkDataLaneNegotiated,
        _ => throw new ArgumentOutOfRangeException(nameof(lane)),
    };

    private static bool IsPictureLossIndication(RTCPCompoundPacket? report) =>
        report?.Feedback?.Header.PacketType == RTCPReportTypesEnum.PSFB
        && report.Feedback.Header.PayloadFeedbackMessageType == PSFBFeedbackTypesEnum.PLI;

    private void RequestPictureLossIndication()
    {
        if (State != MediaConnectionState.Connected) return;

        var videoStream = _peer.VideoStream;
        var senderSsrc = videoStream?.LocalTrack?.Ssrc ?? 0;
        var mediaSsrc = unchecked((uint)Volatile.Read(ref _remoteVideoSsrc));
        if (mediaSsrc == 0)
        {
            mediaSsrc = videoStream?.RemoteTrack?.Ssrc ?? 0;
        }

        if (mediaSsrc == 0 && videoStream?.RemoteTrack?.SdpSsrc is { Count: > 0 } sdpSsrc)
        {
            mediaSsrc = sdpSsrc.Keys.First();
        }

        if (senderSsrc == 0 || mediaSsrc == 0
            || !_lossRecovery.TryAcquirePictureLossIndication(Stopwatch.GetTimestamp()))
        {
            return;
        }

        try
        {
            _peer.SendRtcpFeedback(
                SDPMediaTypesEnum.video,
                new RTCPFeedback(senderSsrc, mediaSsrc, PSFBFeedbackTypesEnum.PLI));
        }
        catch (Exception ex)
        {
            // A failed transport must not escape the RTP receive callback. The throttle is
            // deliberately retained so a broken path cannot create an exception storm.
            LogPictureLossIndicationError(_log, ex);
        }
    }

    private void ConfigureDataChannel(RTCDataChannel channel, DataLane lane)
    {
        var validLane = lane switch
        {
            DataLane.Primary => string.Equals(
                channel.label,
                CollaborationChannelLabel,
                StringComparison.Ordinal),
            DataLane.Input => _permissions.HasFlag(SessionPermission.ControlInput)
                              && string.Equals(channel.label, InputDataChannelLabel, StringComparison.Ordinal),
            DataLane.Bulk => _permissions.HasFlag(SessionPermission.FileTransfer)
                             && string.Equals(channel.label, BulkDataChannelLabel, StringComparison.Ordinal),
            _ => false,
        };
        if (!validLane
            || !string.Equals(channel.protocol, CollaborationProtocol, StringComparison.Ordinal))
        {
            LogRejectedDataChannel(_log);
            return;
        }

        var existing = lane switch
        {
            DataLane.Primary => _dataChannel,
            DataLane.Input => Volatile.Read(ref _inputDataChannel),
            DataLane.Bulk => _bulkDataChannel,
            _ => null,
        };
        if (existing is not null && !ReferenceEquals(existing, channel))
        {
            LogRejectedDataChannel(_log);
            return;
        }

        if (lane == DataLane.Primary) _dataChannel = channel;
        else if (lane == DataLane.Input) Volatile.Write(ref _inputDataChannel, channel);
        else _bulkDataChannel = channel;

        channel.bufferedAmountLowThreshold = MaximumBufferedBytes / 2;
        channel.onopen += () =>
        {
            if (lane == DataLane.Primary) DataChannelReady?.Invoke(this, EventArgs.Empty);
        };
        channel.onmessage += (_, _, payload) =>
        {
            if (payload.Length is 0 or > MaximumDataMessageBytes)
            {
                LogRejectedCollaborationMessage(_log, payload.Length);
                return;
            }

            ReadOnlyMemory<byte> protectedPayload;
            if (lane == DataLane.Bulk)
            {
                if (!IsBulkDataLaneNegotiated)
                {
                    SecurityError?.Invoke(this, "secure_data_lane_context_mismatch");
                    return;
                }

                if (!_bulkDataReassembler.TryAccept(payload, out var completedRecord, out var fragmentError))
                {
                    if (fragmentError is not null) SecurityError?.Invoke(this, fragmentError);
                    return;
                }

                protectedPayload = completedRecord!;
            }
            else
            {
                if (lane == DataLane.Input && !IsInputDataLaneNegotiated)
                {
                    SecurityError?.Invoke(this, "secure_data_lane_context_mismatch");
                    return;
                }
                protectedPayload = payload.ToArray();
            }

            var hasRoutingContext = SessionTrafficProtector.TryReadRoutingContext(
                protectedPayload.Span,
                out var routedChannel,
                out _);
            var laneMismatch = lane switch
            {
                DataLane.Input => !hasRoutingContext || routedChannel != SecureChannelKind.Input,
                DataLane.Bulk => !hasRoutingContext || routedChannel != SecureChannelKind.FileTransfer,
                DataLane.Primary => IsBulkDataLaneNegotiated
                                    && hasRoutingContext
                                    && routedChannel == SecureChannelKind.FileTransfer,
                _ => true,
            };
            if (laneMismatch)
            {
                SecurityError?.Invoke(this, "secure_data_lane_context_mismatch");
                return;
            }

            if (lane == DataLane.Bulk)
            {
                if (!_bulkInboundRecords.Writer.TryWrite(protectedPayload))
                    SecurityError?.Invoke(this, "bulk_receive_queue_overflow");
                return;
            }

            DataMessageReceived?.Invoke(this, protectedPayload);
        };
        channel.onerror += error => LogDataChannelError(_log, error);
        channel.onclose += () =>
        {
            if (lane == DataLane.Input)
                Interlocked.CompareExchange(ref _inputDataChannel, null, channel);
            LogDataChannelClosed(_log);
        };

        if (lane == DataLane.Primary && channel.readyState == RTCDataChannelState.open)
            DataChannelReady?.Invoke(this, EventArgs.Empty);
    }

    private async Task DispatchBulkRecordsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var protectedRecord in _bulkInboundRecords.Reader.ReadAllAsync(cancellationToken))
                DataMessageReceived?.Invoke(this, protectedRecord);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Session teardown.
        }
    }

    private void OnQualityLevelChanged(object? sender, QualityLevel level)
    {
        _rateLimiter.SetTargetFps(level.TargetFps);
        Volatile.Write(ref _targetBitrateKbps, Math.Clamp(level.MaxBitrateKbps, 100, 50_000));
        _encoder?.SetTargetFps(level.TargetFps);
        _encoder?.SetTargetKbps(level.MaxBitrateKbps);

        if (_capture is IAdaptiveCaptureSource adaptive)
        {
            adaptive.SetTargetFps(level.TargetFps);
            adaptive.SetDownscaleFactor(level.DownscaleFactor);
        }

        LogQualityChanged(_log, SessionId, level);
    }

    /// <summary>
    /// Periodically feeds real observations to the controller: packet loss the viewer
    /// reported over RTCP, and frames the local queue had to discard.
    /// </summary>
    private async Task AdaptLoopAsync(CancellationToken cancellationToken)
    {
        // A sustained two-second congestion window is visible during remote control. Evaluate
        // once per second; the quality controller still protects resolution with its ten-second
        // dwell and only recovers after multiple healthy observations.
        var interval = TimeSpan.FromSeconds(1);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);

                if (State != MediaConnectionState.Connected || Adaptive is null) continue;

                var drops = _queue.Dropped;
                var snapshot = _statistics.Snapshot();
                var encoded = snapshot.FramesEncoded;
                var network = _networkFeedback.GetFresh();
                var loss = network?.PacketLossFraction ?? 0;
                var rtt = GetIceRttMs();
                _networkFeedback.ObserveRtt(rtt);
                var jitter = network?.JitterMs ?? 0;
                var remoteQuality = GetFreshRemoteQualityFeedback();

                var sample = new QualitySample
                {
                    NetworkFeedbackAvailable = network is not null,
                    PacketLossFraction = loss,
                    QueueDrops = Math.Max(0, drops - _lastDrops),
                    FramesEncoded = Math.Max(0, encoded - _lastEncoded),
                    RttMs = rtt,
                    BaselineRttMs = _networkFeedback.GetBaselineRttMs(),
                    JitterMs = jitter,
                    AvailableOutgoingBitrateKbps = network is null ? 0 : EstimateAvailableOutgoingBitrateKbps(
                        snapshot.CurrentBitrateKbps,
                        loss,
                        rtt,
                        jitter,
                        Volatile.Read(ref _targetBitrateKbps)),
                    ReceiverFeedbackAvailable = remoteQuality is not null,
                    ReceiverDecodeFps = remoteQuality?.DecodeFps ?? 0,
                    ReceiverRenderFps = remoteQuality?.RenderFps ?? 0,
                    ReceiverDecodeToRenderLatencyP95Ms =
                        remoteQuality?.DecodeToRenderLatencyP95Ms ?? 0,
                    ReceiverCaptureToPresentLatencyP95Ms =
                        remoteQuality?.CaptureToPresentLatencyP95Ms ?? 0,
                    ReceiverFrameAgeClockUncertaintyMs =
                        remoteQuality?.FrameAgeClockUncertaintyMs ?? 0,
                    ReceiverCaptureToPresentTargetMs =
                        ConnectionPolicyProvider.GetTargetCaptureToPresentP95Ms(_profile.Resolution),
                    ReceiverInputToInjectionLatencyP95Ms =
                        remoteQuality?.InputToInjectionLatencyP95Ms ?? 0,
                    ReceiverInputClockUncertaintyMs = remoteQuality?.InputClockUncertaintyMs ?? 0,
                };

                _lastDrops = drops;
                _lastEncoded = encoded;

                Adaptive.Evaluate(sample);
                if (Adaptive.IsReceiverPresentationStalled)
                    _lossRecovery.RequestKeyFrame();
            }
        }
        catch (OperationCanceledException)
        {
            // Session ending.
        }
        catch (Exception ex)
        {
            LogAdaptiveLoopStopped(_log, ex);
        }
    }

    private void OnCaptureFrame(object? sender, CapturedFrame frame)
    {
        _statistics.FrameCaptured();

        // Adaptive capture sources enforce their target before GPU readback/conversion. Keep
        // this fallback for sources that cannot pace themselves, without applying two clocks.
        if (_capture is not IAdaptiveCaptureSource && !_rateLimiter.ShouldAccept())
        {
            return;
        }

        // Transfer the production capture buffer when its producer explicitly guarantees
        // single-frame ownership. Other capture implementations keep the safe copy boundary.
        var copy = new CapturedFrame
        {
            SourceWidth = frame.SourceWidth,
            SourceHeight = frame.SourceHeight,
            RequestedWidth = frame.RequestedWidth,
            RequestedHeight = frame.RequestedHeight,
            Width = frame.Width,
            Height = frame.Height,
            I420 = frame.BufferOwnershipCanTransfer ? frame.I420 : frame.I420.ToArray(),
            Timestamp = frame.Timestamp,
            SequenceNumber = frame.SequenceNumber,
            BufferOwnershipCanTransfer = true,
            PipelineEnteredTimestamp = Stopwatch.GetTimestamp(),
            CaptureTimestampUnixMicroseconds = GetCaptureTimestampUnixMicroseconds(frame.Timestamp),
        };

        if (!_queue.TryEnqueue(copy))
        {
            _statistics.FrameDropped();
        }

        WakeEncoder();
    }

    private void WakeEncoder()
    {
        try
        {
            _encodeWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // The encoder has already been notified and will take the newest queued frame.
        }
    }

    private async Task EncodeLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _profile.TargetFps));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var protector = Volatile.Read(ref _trafficProtector);
                if (protector is null
                    || !TryDequeueFrameForEncoding(
                        _queue,
                        State,
                        protector.IsEstablished,
                        out var frame))
                {
                    // Before the encrypted transport is ready, preserve the initial frame and
                    // recheck periodically: a change-driven capture source may stay still. Once
                    // ready, wait for a producer signal instead of adding a frame-period delay.
                    if (protector is null
                        || State != MediaConnectionState.Connected
                        || !protector.IsEstablished)
                    {
                        await Task.Delay(interval, cancellationToken);
                    }
                    else
                    {
                        await _encodeWake.WaitAsync(cancellationToken);
                    }
                    continue;
                }

                byte[] input;
                if (MemoryMarshal.TryGetArray(frame.I420, out ArraySegment<byte> segment)
                    && segment.Offset == 0
                    && segment.Array is { } owned
                    && segment.Count == owned.Length)
                {
                    input = owned;
                }
                else
                {
                    input = frame.I420.ToArray();
                }

                var encoded = EncodeFrameForTransport(
                    _encoder!,
                    _lossRecovery,
                    frame.Width,
                    frame.Height,
                    input);

                if (encoded is { Length: > 0 })
                {
                    // 90 kHz clock: one frame period in RTP units.
                    var durationRtpUnits = (uint)(90000 / Math.Max(1, _rateLimiter.TargetFps));
                    byte[]? telemetryEnvelope = null;
                    ReadOnlySpan<byte> plaintextFrame = encoded;
                    if (IsVideoFrameTelemetryNegotiated)
                    {
                        telemetryEnvelope = VideoFrameTelemetryCodec.Encode(
                            encoded,
                            frame.CaptureTimestampUnixMicroseconds,
                            frame.SequenceNumber);
                        plaintextFrame = telemetryEnvelope;
                    }

                    var protectedFrame = protector.ProtectVideoFrame(plaintextFrame);
                    if (telemetryEnvelope is not null)
                        CryptographicOperations.ZeroMemory(telemetryEnvelope);
                    _peer.SendVideo(durationRtpUnits, protectedFrame);
                    var pipelineLatency = frame.PipelineEnteredTimestamp > 0
                        ? Stopwatch.GetElapsedTime(frame.PipelineEnteredTimestamp).TotalMilliseconds
                        : 0;
                    _statistics.FrameEncoded(
                        encoded.Length,
                        frame.Width,
                        frame.Height,
                        pipelineLatency,
                        frame.SourceWidth,
                        frame.SourceHeight,
                        frame.RequestedWidth,
                        frame.RequestedHeight);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogEncoderIterationFailed(_log, ex);
                await Task.Delay(interval, CancellationToken.None);
            }
        }
    }

    internal static bool TryDequeueFrameForEncoding(
        VideoFrameQueue queue,
        MediaConnectionState state,
        bool secureTransportEstablished,
        out CapturedFrame frame)
    {
        // Keep the newest pre-connection frame queued. Windows.Graphics.Capture is
        // change-driven, so discarding its initial frame here can leave a static remote
        // desktop blank even though ICE, DTLS, and the secure session are connected.
        if (state != MediaConnectionState.Connected || !secureTransportEstablished)
        {
            frame = null!;
            return false;
        }

        if (queue.TryDequeue(out var ready) && ready is not null)
        {
            frame = ready;
            return true;
        }

        frame = null!;
        return false;
    }

    internal static byte[] EncodeFrameForTransport(
        Vp8ScreenEncoder encoder,
        Vp8LossRecoveryController lossRecovery,
        int width,
        int height,
        byte[] frame)
    {
        if (lossRecovery.ConsumeKeyFrameRequest())
        {
            encoder.ForceKeyFrame();
        }

        return encoder.EncodeI420(width, height, frame);
    }

    public async Task<string> CreateOfferAsync(CancellationToken cancellationToken = default)
    {
        if (_dataChannelCreation is not null)
        {
            ConfigureDataChannel(
                await _dataChannelCreation.WaitAsync(cancellationToken),
                DataLane.Primary);
            _dataChannelCreation = null;
        }
        if (_inputDataChannelCreation is not null)
        {
            ConfigureDataChannel(
                await _inputDataChannelCreation.WaitAsync(cancellationToken),
                DataLane.Input);
            _inputDataChannelCreation = null;
        }
        if (_bulkDataChannelCreation is not null)
        {
            ConfigureDataChannel(
                await _bulkDataChannelCreation.WaitAsync(cancellationToken),
                DataLane.Bulk);
            _bulkDataChannelCreation = null;
        }

        var offer = _peer.createOffer();
        await _peer.setLocalDescription(offer);
        var advertisedOffer = AdvertiseVideoFrameTelemetry(offer.sdp);
        if (_permissions.HasFlag(SessionPermission.ControlInput))
            advertisedOffer = AdvertiseInputAcknowledgement(advertisedOffer);
        if (_permissions.HasFlag(SessionPermission.FileTransfer))
            advertisedOffer = AdvertiseNativeBulkTransport(advertisedOffer);
        if (_permissions.HasFlag(SessionPermission.ControlInput) && _inputPeer is not null)
            advertisedOffer = AdvertiseInputDataLane(
                advertisedOffer,
                await CreateAuxiliaryOfferAsync(_inputPeer));
        if (_permissions.HasFlag(SessionPermission.FileTransfer) && _bulkPeer is not null)
            advertisedOffer = AdvertiseBulkDataLane(
                advertisedOffer,
                await CreateAuxiliaryOfferAsync(_bulkPeer));
        return advertisedOffer;
    }

    public async Task<string> CreateAnswerAsync(string remoteOffer, CancellationToken cancellationToken = default)
    {
        var telemetryOffered = HasVideoFrameTelemetry(remoteOffer);
        var inputAcknowledgementOffered = _permissions.HasFlag(SessionPermission.ControlInput)
                                           && HasInputAcknowledgement(remoteOffer);
        var nativeBulkTransportOffered = _permissions.HasFlag(SessionPermission.FileTransfer)
                                          && HasNativeBulkTransport(remoteOffer);
        var hasInputOfferDescription = TryGetInputDescription(remoteOffer, out var inputOfferSdp);
        var inputDataLaneOffered = _permissions.HasFlag(SessionPermission.ControlInput)
                                   && _inputPeer is not null
                                   && HasInputDataLane(remoteOffer)
                                   && hasInputOfferDescription;
        var hasBulkOfferDescription = TryGetBulkDescription(remoteOffer, out var bulkOfferSdp);
        var bulkDataLaneOffered = _permissions.HasFlag(SessionPermission.FileTransfer)
                                  && _bulkPeer is not null
                                  && HasBulkDataLane(remoteOffer)
                                  && hasBulkOfferDescription;
        var result = _peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = RemoveBulkDescription(RemoveInputDescription(remoteOffer)),
        });

        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Remote offer rejected: {result}");
        }

        var inputAnswerSdp = inputDataLaneOffered
            ? await CreateAuxiliaryAnswerAsync(_inputPeer!, inputOfferSdp, "input")
            : null;
        var bulkAnswerSdp = bulkDataLaneOffered
            ? await CreateAuxiliaryAnswerAsync(_bulkPeer!, bulkOfferSdp, "bulk")
            : null;

        var answer = _peer.createAnswer();
        await _peer.setLocalDescription(answer);
        Volatile.Write(ref _videoFrameTelemetryNegotiated, telemetryOffered ? 1 : 0);
        Volatile.Write(ref _inputAcknowledgementNegotiated, inputAcknowledgementOffered ? 1 : 0);
        Volatile.Write(ref _nativeBulkTransportNegotiated, nativeBulkTransportOffered ? 1 : 0);
        CompleteAuxiliaryDataLaneNegotiation(DataLane.Input, inputDataLaneOffered);
        CompleteAuxiliaryDataLaneNegotiation(DataLane.Bulk, bulkDataLaneOffered);
        var negotiatedAnswer = telemetryOffered
            ? AdvertiseVideoFrameTelemetry(answer.sdp)
            : answer.sdp;
        if (inputAcknowledgementOffered)
            negotiatedAnswer = AdvertiseInputAcknowledgement(negotiatedAnswer);
        if (nativeBulkTransportOffered)
            negotiatedAnswer = AdvertiseNativeBulkTransport(negotiatedAnswer);
        if (inputDataLaneOffered)
            negotiatedAnswer = AdvertiseInputDataLane(negotiatedAnswer, inputAnswerSdp!);
        if (bulkDataLaneOffered)
            negotiatedAnswer = AdvertiseBulkDataLane(negotiatedAnswer, bulkAnswerSdp!);
        return negotiatedAnswer;
    }

    public async Task ApplyRemoteAnswerAsync(string answer, CancellationToken cancellationToken = default)
    {
        var hasInputAnswerDescription = TryGetInputDescription(answer, out var inputAnswerSdp);
        var inputDataLaneAccepted = _permissions.HasFlag(SessionPermission.ControlInput)
                                    && _inputPeer is not null
                                    && HasInputDataLane(answer)
                                    && hasInputAnswerDescription;
        var hasBulkAnswerDescription = TryGetBulkDescription(answer, out var bulkAnswerSdp);
        var bulkDataLaneAccepted = _permissions.HasFlag(SessionPermission.FileTransfer)
                                   && _bulkPeer is not null
                                   && HasBulkDataLane(answer)
                                   && hasBulkAnswerDescription;
        var result = _peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = RemoveBulkDescription(RemoveInputDescription(answer)),
        });

        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Remote answer rejected: {result}");
        }

        Volatile.Write(
            ref _videoFrameTelemetryNegotiated,
            HasVideoFrameTelemetry(answer) ? 1 : 0);
        Volatile.Write(
            ref _inputAcknowledgementNegotiated,
            _permissions.HasFlag(SessionPermission.ControlInput)
            && HasInputAcknowledgement(answer) ? 1 : 0);
        Volatile.Write(
            ref _nativeBulkTransportNegotiated,
            _permissions.HasFlag(SessionPermission.FileTransfer)
            && HasNativeBulkTransport(answer) ? 1 : 0);
        if (inputDataLaneAccepted)
            ApplyAuxiliaryAnswer(_inputPeer!, inputAnswerSdp, "input");
        if (bulkDataLaneAccepted)
            ApplyAuxiliaryAnswer(_bulkPeer!, bulkAnswerSdp, "bulk");

        CompleteAuxiliaryDataLaneNegotiation(DataLane.Input, inputDataLaneAccepted);
        CompleteAuxiliaryDataLaneNegotiation(DataLane.Bulk, bulkDataLaneAccepted);
        await Task.CompletedTask;
    }

    private static async Task<string> CreateAuxiliaryOfferAsync(RTCPeerConnection peer)
    {
        var offer = peer.createOffer();
        await peer.setLocalDescription(offer);
        return offer.sdp;
    }

    private static async Task<string> CreateAuxiliaryAnswerAsync(
        RTCPeerConnection peer,
        string offerSdp,
        string laneName)
    {
        var result = peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerSdp,
        });
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"Remote {laneName} offer rejected: {result}");

        var answer = peer.createAnswer();
        await peer.setLocalDescription(answer);
        return answer.sdp;
    }

    private static void ApplyAuxiliaryAnswer(
        RTCPeerConnection peer,
        string answerSdp,
        string laneName)
    {
        var result = peer.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answerSdp,
        });
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"Remote {laneName} answer rejected: {result}");
    }

    public Task AddRemoteIceCandidateAsync(
        string candidate, string? sdpMid, ushort sdpMLineIndex, CancellationToken cancellationToken = default)
    {
        var lane = IsInputIceCandidateSdpMid(sdpMid)
            ? DataLane.Input
            : IsBulkIceCandidateSdpMid(sdpMid)
                ? DataLane.Bulk
                : DataLane.Primary;
        var peer = lane switch
        {
            DataLane.Input => _inputPeer,
            DataLane.Bulk => _bulkPeer,
            _ => _peer,
        };
        if (peer is null) return Task.CompletedTask;
        var routedMid = lane switch
        {
            DataLane.Input => sdpMid![InputIceMidPrefix.Length..],
            DataLane.Bulk => sdpMid![BulkIceMidPrefix.Length..],
            _ => sdpMid,
        };
        peer.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate,
            sdpMid = string.IsNullOrEmpty(routedMid) ? null : routedMid,
            sdpMLineIndex = sdpMLineIndex,
        });

        return Task.CompletedTask;
    }

    public async Task SendDataAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default,
        DataMessagePriority priority = DataMessagePriority.Normal)
    {
        if (payload.Length is 0 or > MaximumDataMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Payload must be 1-{MaximumDataMessageBytes} bytes.");

        var hasRoutingContext = SessionTrafficProtector.TryReadRoutingContext(
            payload.Span,
            out var routedChannel,
            out _);
        var useInputLane = priority == DataMessagePriority.Interactive
                           && IsInputDataLaneReady
                           && hasRoutingContext
                           && routedChannel == SecureChannelKind.Input;
        var useBulkLane = priority == DataMessagePriority.Bulk && IsBulkDataLaneNegotiated;
        var channel = useBulkLane
            ? await WaitForBulkDataLaneAsync(cancellationToken)
            : useInputLane
                ? Volatile.Read(ref _inputDataChannel)
                : _dataChannel;
        if (useInputLane && channel?.readyState != RTCDataChannelState.open)
        {
            // The optional input association may close between the readiness check and channel
            // selection. Fall back to the mandatory primary channel without dropping the event.
            useInputLane = false;
            channel = _dataChannel;
        }
        if (channel?.readyState != RTCDataChannelState.open)
            throw new InvalidOperationException("The secure session data channel is not open.");

        var foreground = priority != DataMessagePriority.Bulk;
        if (foreground) Interlocked.Increment(ref _foregroundDataWaiters);
        try
        {
            if (useBulkLane)
            {
                await _bulkRecordSendGate.WaitAsync(cancellationToken);
                try
                {
                    await SendFragmentedBulkRecordAsync(channel, payload, cancellationToken);
                }
                finally
                {
                    _bulkRecordSendGate.Release();
                }
                return;
            }

            if (useInputLane)
            {
                try
                {
                    await SendDataMessageAsync(
                        channel,
                        payload,
                        MaximumInputBufferedBytes,
                        _inputDataSendGate,
                        isBulk: false,
                        cancellationToken);
                    Interlocked.Increment(ref _inputDataRecordsSent);
                    return;
                }
                catch (InvalidOperationException) when (_dataChannel?.readyState == RTCDataChannelState.open)
                {
                    // An optional auxiliary connection may close independently. Keep authorized
                    // control alive on the primary secure channel without changing media state.
                    channel = _dataChannel;
                }
            }

            var threshold = priority == DataMessagePriority.Bulk
                ? GetTransferAllocation().MaximumBulkBufferedBytes
                : MaximumBufferedBytes;
            await SendDataMessageAsync(
                channel,
                payload,
                threshold,
                _dataSendGate,
                isBulk: priority == DataMessagePriority.Bulk,
                cancellationToken);
        }
        finally
        {
            if (foreground) Interlocked.Decrement(ref _foregroundDataWaiters);
        }
    }

    private async Task SendFragmentedBulkRecordAsync(
        RTCDataChannel channel,
        ReadOnlyMemory<byte> protectedRecord,
        CancellationToken cancellationToken)
    {
        var recordId = checked((ulong)Interlocked.Increment(ref _nextBulkRecordId));
        var offset = 0;
        while (offset < protectedRecord.Length)
        {
            var allocation = GetTransferAllocation();
            var hasInteractiveTraffic = _permissions.HasFlag(SessionPermission.ControlInput);
            var fragmentBytes = GetBulkFragmentPayloadBytes(allocation, hasInteractiveTraffic);
            var queueBudget = GetBulkQueueBudgetBytes(allocation, hasInteractiveTraffic);
            Volatile.Write(ref _bulkDataFragmentBytes, fragmentBytes);
            Volatile.Write(ref _bulkQueueBudgetBytes, checked((long)queueBudget));

            var count = Math.Min(fragmentBytes, protectedRecord.Length - offset);
            var fragment = BulkDataFrameCodec.Encode(
                protectedRecord.Span,
                recordId,
                offset,
                count);
            await SendDataMessageAsync(
                channel,
                fragment,
                queueBudget,
                _bulkDataSendGate,
                isBulk: true,
                cancellationToken);
            Interlocked.Increment(ref _bulkDataFragmentsSent);
            offset += count;
            await Task.Yield();
        }
        Interlocked.Increment(ref _bulkDataRecordsSent);
    }

    private async Task SendDataMessageAsync(
        RTCDataChannel channel,
        ReadOnlyMemory<byte> payload,
        ulong bufferedThreshold,
        SemaphoreSlim sendGate,
        bool isBulk,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (channel.readyState != RTCDataChannelState.open)
                throw new InvalidOperationException("The authorized collaboration data channel closed before the message was sent.");

            if (channel.bufferedAmount > bufferedThreshold
                || (isBulk && Volatile.Read(ref _foregroundDataWaiters) > 0))
            {
                await Task.Delay(1, cancellationToken);
                continue;
            }

            await sendGate.WaitAsync(cancellationToken);
            try
            {
                if (channel.bufferedAmount > bufferedThreshold
                    || (isBulk && Volatile.Read(ref _foregroundDataWaiters) > 0))
                {
                    continue;
                }

                var bytes = payload.ToArray();
                channel.send(bytes, 0, bytes.Length);
                return;
            }
            finally
            {
                sendGate.Release();
            }
        }
    }

    internal static int GetBulkFragmentPayloadBytes(
        TransferAllocationPolicy allocation,
        bool hasInteractiveTraffic)
    {
        if (!hasInteractiveTraffic) return BulkDataFrameCodec.MaximumFragmentPayloadBytes;
        if (allocation.MaximumBulkKbps <= 0) return 2 * 1024;
        var fiveMillisecondsOfBulk = allocation.MaximumBulkKbps * 1000d / 8d * 0.005;
        return (int)Math.Clamp(
            Math.Ceiling(fiveMillisecondsOfBulk),
            1024,
            BulkDataFrameCodec.MaximumFragmentPayloadBytes);
    }

    internal static ulong GetBulkQueueBudgetBytes(
        TransferAllocationPolicy allocation,
        bool hasInteractiveTraffic)
    {
        if (!hasInteractiveTraffic || allocation.MaximumBulkKbps <= 0)
            return hasInteractiveTraffic ? 0 : allocation.MaximumBulkBufferedBytes;
        var fiveMillisecondsOfBulk = allocation.MaximumBulkKbps * 1000d / 8d * 0.005;
        return Math.Min(
            allocation.MaximumBulkBufferedBytes,
            checked((ulong)Math.Ceiling(fiveMillisecondsOfBulk)));
    }

    private async Task<RTCDataChannel> WaitForBulkDataLaneAsync(CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (_bulkDataChannel?.readyState != RTCDataChannelState.open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_dataChannel?.readyState != RTCDataChannelState.open)
                throw new InvalidOperationException("The secure session data channel is not open.");
            if (_bulkDataChannel?.readyState is RTCDataChannelState.closing or RTCDataChannelState.closed)
                throw new InvalidOperationException("The negotiated bulk data lane closed.");
            if (Stopwatch.GetElapsedTime(startedAt) > TimeSpan.FromSeconds(30))
                throw new InvalidOperationException("The negotiated bulk data lane did not open.");
            await Task.Delay(5, cancellationToken);
        }

        return _bulkDataChannel;
    }

    public async Task<string> RestartIceAsync(CancellationToken cancellationToken = default)
    {
        _networkFeedback.Reset();
        _encoder?.ForceKeyFrame();
        _peer.restartIce();
        var offer = _peer.createOffer();
        await _peer.setLocalDescription(offer);
        var advertisedOffer = AdvertiseVideoFrameTelemetry(offer.sdp);
        if (_permissions.HasFlag(SessionPermission.ControlInput))
            advertisedOffer = AdvertiseInputAcknowledgement(advertisedOffer);
        if (_permissions.HasFlag(SessionPermission.FileTransfer))
            advertisedOffer = AdvertiseNativeBulkTransport(advertisedOffer);
        if (_permissions.HasFlag(SessionPermission.ControlInput) && _inputPeer is not null)
        {
            _inputPeer.restartIce();
            advertisedOffer = AdvertiseInputDataLane(
                advertisedOffer,
                await CreateAuxiliaryOfferAsync(_inputPeer));
        }
        if (_permissions.HasFlag(SessionPermission.FileTransfer) && _bulkPeer is not null)
        {
            _bulkPeer.restartIce();
            advertisedOffer = AdvertiseBulkDataLane(
                advertisedOffer,
                await CreateAuxiliaryOfferAsync(_bulkPeer));
        }
        return advertisedOffer;
    }

    public void ReportFrameRendered(long decodedTimestamp = 0, int width = 0, int height = 0)
    {
        var latency = decodedTimestamp > 0
            ? Stopwatch.GetElapsedTime(decodedTimestamp).TotalMilliseconds
            : 0;
        _statistics.FrameRendered(latency, width, height);
    }

    public void ReportFrameRendered(PresentedVideoFrame frame)
    {
        var decodeToRenderLatency = frame.DecodedTimestamp > 0
            ? Stopwatch.GetElapsedTime(frame.DecodedTimestamp).TotalMilliseconds
            : 0;
        var captureToPresentLatency = 0d;
        var uncertainty = 0d;
        if (frame.CaptureTimestampUnixMicroseconds > 0
            && frame.PeerClockEstimate is { } estimate
            && estimate.UncertaintyMicroseconds is >= 0 and <= 50_000)
        {
            var ageMicroseconds = (decimal)GetUnixMicroseconds(DateTimeOffset.UtcNow)
                                  + estimate.RemoteMinusLocalOffsetMicroseconds
                                  - frame.CaptureTimestampUnixMicroseconds;
            if (ageMicroseconds is > 0 and <= 60_000_000)
            {
                captureToPresentLatency = (double)(ageMicroseconds / 1_000m);
                uncertainty = estimate.UncertaintyMicroseconds / 1_000d;
            }
        }

        _statistics.FrameRendered(
            decodeToRenderLatency,
            frame.Width,
            frame.Height,
            captureToPresentLatency,
            uncertainty);
    }

    public void SetPeerClockEstimate(PeerClockEstimate? estimate)
    {
        if (estimate is not { UncertaintyMicroseconds: >= 0 and <= 5_000_000 } valid)
        {
            Volatile.Write(ref _hasPeerClockEstimate, 0);
            return;
        }

        Volatile.Write(ref _peerClockOffsetMicroseconds, valid.RemoteMinusLocalOffsetMicroseconds);
        Volatile.Write(ref _peerClockUncertaintyMicroseconds, valid.UncertaintyMicroseconds);
        Volatile.Write(
            ref _peerClockEstimateExpiresAtUnixMicroseconds,
            GetUnixMicroseconds(DateTimeOffset.UtcNow.AddMinutes(2)));
        Volatile.Write(ref _hasPeerClockEstimate, 1);
    }

    public void ReportInputLatency(InputLatencyMeasurement measurement) =>
        _statistics.InputInjected(
            measurement.LatencyMilliseconds,
            measurement.ClockUncertaintyMilliseconds);

    public void ReportRemoteQualityFeedback(RemoteSessionQualityFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        if (Role != SessionRole.Sharer) return;

        Volatile.Write(ref _remoteQualityFeedback, feedback);
        Volatile.Write(ref _remoteQualityFeedbackTimestamp, Stopwatch.GetTimestamp());
    }

    private RemoteSessionQualityFeedback? GetFreshRemoteQualityFeedback()
    {
        var receivedAt = Volatile.Read(ref _remoteQualityFeedbackTimestamp);
        if (receivedAt <= 0 || Stopwatch.GetElapsedTime(receivedAt) > RemoteQualityFeedbackLifetime)
            return null;
        return Volatile.Read(ref _remoteQualityFeedback);
    }

    internal static bool HasVideoFrameTelemetry(string sdp) =>
        sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line, VideoFrameTelemetrySdpAttribute, StringComparison.Ordinal));

    internal static string AdvertiseVideoFrameTelemetry(string sdp)
    {
        if (HasVideoFrameTelemetry(sdp)) return sdp;
        var newline = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Concat(sdp.TrimEnd('\r', '\n'), newline, VideoFrameTelemetrySdpAttribute, newline);
    }

    internal static bool HasInputAcknowledgement(string sdp) =>
        sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line, InputAcknowledgementSdpAttribute, StringComparison.Ordinal));

    internal static string AdvertiseInputAcknowledgement(string sdp)
    {
        if (HasInputAcknowledgement(sdp)) return sdp;
        var newline = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Concat(sdp.TrimEnd('\r', '\n'), newline, InputAcknowledgementSdpAttribute, newline);
    }

    internal static bool HasNativeBulkTransport(string sdp) =>
        sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line, NativeBulkTransportSdpAttribute, StringComparison.Ordinal));

    internal static string AdvertiseNativeBulkTransport(string sdp)
    {
        if (HasNativeBulkTransport(sdp)) return sdp;
        var newline = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Concat(sdp.TrimEnd('\r', '\n'), newline, NativeBulkTransportSdpAttribute, newline);
    }

    internal static bool HasInputDataLane(string sdp) =>
        HasSdpAttribute(sdp, InputDataLaneSdpAttribute);

    internal static bool HasBulkDataLane(string sdp) =>
        HasSdpAttribute(sdp, BulkDataLaneSdpAttribute);

    internal static string AdvertiseInputDataLane(string sdp, string inputDescription) =>
        AdvertiseAuxiliaryDataLane(
            sdp,
            inputDescription,
            InputDataLaneSdpAttribute,
            InputDescriptionSdpPrefix,
            "inputDescription");

    internal static string AdvertiseBulkDataLane(string sdp) =>
        AdvertiseSdpAttribute(sdp, BulkDataLaneSdpAttribute);

    internal static string AdvertiseBulkDataLane(string sdp, string bulkDescription) =>
        AdvertiseAuxiliaryDataLane(
            sdp,
            bulkDescription,
            BulkDataLaneSdpAttribute,
            BulkDescriptionSdpPrefix,
            "bulkDescription");

    internal static bool TryGetInputDescription(string sdp, out string description) =>
        TryGetAuxiliaryDescription(sdp, InputDescriptionSdpPrefix, out description);

    internal static bool TryGetBulkDescription(string sdp, out string description) =>
        TryGetAuxiliaryDescription(sdp, BulkDescriptionSdpPrefix, out description);

    internal static string RemoveInputDescription(string sdp) =>
        RemoveAuxiliaryDescription(sdp, InputDescriptionSdpPrefix);

    internal static string RemoveBulkDescription(string sdp) =>
        RemoveAuxiliaryDescription(sdp, BulkDescriptionSdpPrefix);

    internal static bool IsInputIceCandidateSdpMid(string? sdpMid) =>
        sdpMid?.StartsWith(InputIceMidPrefix, StringComparison.Ordinal) == true;

    internal static bool IsBulkIceCandidateSdpMid(string? sdpMid) =>
        sdpMid?.StartsWith(BulkIceMidPrefix, StringComparison.Ordinal) == true;

    private static bool HasSdpAttribute(string sdp, string attribute) =>
        sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line, attribute, StringComparison.Ordinal));

    private static string AdvertiseSdpAttribute(string sdp, string attribute)
    {
        if (HasSdpAttribute(sdp, attribute)) return sdp;
        var newline = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return string.Concat(sdp.TrimEnd('\r', '\n'), newline, attribute, newline);
    }

    private static string AdvertiseAuxiliaryDataLane(
        string sdp,
        string description,
        string attribute,
        string descriptionPrefix,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description, parameterName);
        if (Encoding.UTF8.GetByteCount(description) > MaximumAuxiliaryDescriptionBytes)
            throw new ArgumentException("The auxiliary SDP description exceeds its signaling bound.", parameterName);
        var advertised = AdvertiseSdpAttribute(
            RemoveAuxiliaryDescription(sdp, descriptionPrefix),
            attribute);
        var newline = advertised.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(description))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return string.Concat(
            advertised.TrimEnd('\r', '\n'),
            newline,
            descriptionPrefix,
            encoded,
            newline);
    }

    private static bool TryGetAuxiliaryDescription(
        string sdp,
        string descriptionPrefix,
        out string description)
    {
        description = string.Empty;
        var encodedDescriptions = sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(descriptionPrefix, StringComparison.Ordinal))
            .Select(line => line[descriptionPrefix.Length..])
            .ToArray();
        if (encodedDescriptions is not [var encoded]
            || encoded.Length is 0 or > MaximumEncodedAuxiliaryDescriptionCharacters)
            return false;

        try
        {
            var normalized = encoded.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var decoded = Convert.FromBase64String(normalized);
            if (decoded.Length > MaximumAuxiliaryDescriptionBytes) return false;
            description = new UTF8Encoding(false, true).GetString(decoded);
            return description.StartsWith("v=0", StringComparison.Ordinal)
                   && description.Length <= MaximumAuxiliaryDescriptionBytes;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            description = string.Empty;
            return false;
        }
    }

    private static string RemoveAuxiliaryDescription(string sdp, string descriptionPrefix)
    {
        var newline = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = sdp.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith(descriptionPrefix, StringComparison.Ordinal));
        return string.Concat(string.Join(newline, lines), newline);
    }

    public PeerClockEstimate? GetPeerClockEstimate()
    {
        if (Volatile.Read(ref _hasPeerClockEstimate) == 0
            || GetUnixMicroseconds(DateTimeOffset.UtcNow)
            > Volatile.Read(ref _peerClockEstimateExpiresAtUnixMicroseconds))
        {
            return null;
        }

        return new PeerClockEstimate(
            Volatile.Read(ref _peerClockOffsetMicroseconds),
            Volatile.Read(ref _peerClockUncertaintyMicroseconds));
    }

    private static long GetCaptureTimestampUnixMicroseconds(TimeSpan systemRelativeTime)
    {
        var now = DateTimeOffset.UtcNow;
        if (systemRelativeTime > TimeSpan.Zero)
        {
            var currentSystemRelative = TimeSpan.FromSeconds(
                Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
            var age = currentSystemRelative - systemRelativeTime;
            if (age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(5))
                now -= age;
        }

        return GetUnixMicroseconds(now);
    }

    private static long GetUnixMicroseconds(DateTimeOffset timestamp) =>
        (timestamp.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    public void SetTrafficProtector(ISessionTrafficProtector? protector) =>
        Volatile.Write(ref _trafficProtector, protector);

    public void SetTransferPriorityMode(TransferPriorityMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Volatile.Write(ref _transferPriorityMode, (int)mode);
    }

    private TransferAllocationPolicy GetTransferAllocation()
    {
        var statistics = GetStatistics();
        var hasLiveMedia = (_permissions & SessionPermission.ViewScreen) != 0
                           && (statistics.FramesCaptured > 0
                               || statistics.FramesEncoded > 0
                               || statistics.FramesRendered > 0
                               || statistics.CurrentBitrateKbps > 0);
        if (!hasLiveMedia)
        {
            statistics = statistics with
            {
                CurrentBitrateKbps = 0,
                TargetBitrateKbps = 0,
            };
        }

        return ConnectionPolicyProvider.GetTransferAllocation(
            (TransferPriorityMode)Volatile.Read(ref _transferPriorityMode),
            statistics,
            hasInteractiveTraffic: _permissions.HasFlag(SessionPermission.ControlInput));
    }

    public MediaStatistics GetStatistics()
    {
        var snapshot = _statistics.Snapshot();
        var path = GetSelectedPath();
        var network = _networkFeedback.GetFresh();
        var loss = network?.PacketLossFraction ?? 0;
        var rtt = GetIceRttMs();
        _networkFeedback.ObserveRtt(rtt);
        var jitter = network?.JitterMs ?? 0;

        // SIPSorcery does not expose TWCC bandwidth estimation. This conservative estimate is
        // derived from measured outgoing bitrate, loss, RTT and jitter and is labelled as such
        // in the UI; zero also means receiver feedback is absent, not recovered capacity.
        var availableEstimate = network is null ? 0 : EstimateAvailableOutgoingBitrateKbps(
            snapshot.CurrentBitrateKbps,
            loss,
            rtt,
            jitter,
            Volatile.Read(ref _targetBitrateKbps));

        _statistics.UpdateNetwork(
            path,
            _iceConfiguration.RelayServerId,
            _iceConfiguration.RelayRegion,
            rtt,
            loss * 100,
            jitter,
            availableEstimate);

        var measured = _statistics.Snapshot();
        var remoteQuality = GetFreshRemoteQualityFeedback();
        if (remoteQuality is not null)
        {
            measured = measured with
            {
                InputToInjectionLatencyP95Ms = remoteQuality.InputToInjectionLatencyP95Ms,
                InputClockUncertaintyMs = remoteQuality.InputClockUncertaintyMs,
            };
        }
        var primaryBufferedBytes = SaturatingInt64(_dataChannel?.bufferedAmount ?? 0);
        var inputBufferedBytes = SaturatingInt64(Volatile.Read(ref _inputDataChannel)?.bufferedAmount ?? 0);
        var bulkBufferedBytes = SaturatingInt64(_bulkDataChannel?.bufferedAmount ?? 0);
        var totalFrames = measured.FramesEncoded + measured.FramesDropped;
        var decision = Adaptive?.LastDecision ?? _connectionPolicy.Evaluate(new ConnectionPolicyInput
        {
            // Viewers do not send screen RTP and therefore do not receive these reports.
            NetworkFeedbackAvailable = Role != SessionRole.Sharer || network is not null,
            PacketLossFraction = loss,
            QueueDropRatio = totalFrames > 0 ? (double)measured.FramesDropped / totalFrames : 0,
            FramesObserved = totalFrames,
            RttMs = rtt,
            BaselineRttMs = _networkFeedback.GetBaselineRttMs(),
            JitterMs = jitter,
            AvailableOutgoingBitrateKbps = availableEstimate,
            CurrentTargetBitrateKbps = Volatile.Read(ref _targetBitrateKbps),
        });
        // Statistics can be read between adaptive ticks or while disconnected. Missing reports
        // must cap bulk immediately instead of pairing an old healthy decision with zero capacity.
        if (Role == SessionRole.Sharer && network is null
            && decision.Action != ConnectionPolicyAction.Degrade)
        {
            decision = new ConnectionPolicyDecision(
                ConnectionHealth.Unknown, ConnectionPolicyAction.Hold, "awaiting_network_feedback");
        }
        return measured with
        {
            ConnectionHealth = decision.Health,
            ActiveQualityProfile = _profile.Quality,
            AdaptiveQualityLevel = Adaptive?.Current.Index,
            QualityChangeReason = decision.ReasonCode,
            TargetFps = Adaptive?.Current.TargetFps ?? _profile.TargetFps,
            TargetBitrateKbps = Volatile.Read(ref _targetBitrateKbps),
            EncoderQueueDepth = _queue.Count,
            InputDataLaneNegotiated = IsInputDataLaneNegotiated,
            InputDataLaneReady = IsInputDataLaneReady,
            InputDataRecordsSent = Interlocked.Read(ref _inputDataRecordsSent),
            BulkDataLaneNegotiated = IsBulkDataLaneNegotiated,
            BulkDataLaneReady = IsBulkDataLaneReady,
            NativeBulkTransportNegotiated = IsNativeBulkTransportNegotiated,
            SctpAssociationBufferedBytes = SaturatingAdd(
                SaturatingAdd(primaryBufferedBytes, inputBufferedBytes),
                bulkBufferedBytes),
            InteractiveSctpBufferedBytes = SaturatingAdd(primaryBufferedBytes, inputBufferedBytes),
            InputSctpBufferedBytes = inputBufferedBytes,
            BulkSctpBufferedBytes = bulkBufferedBytes,
            BulkDataFragmentBytes = Volatile.Read(ref _bulkDataFragmentBytes),
            BulkQueueBudgetBytes = Volatile.Read(ref _bulkQueueBudgetBytes),
            BulkDataRecordsSent = Interlocked.Read(ref _bulkDataRecordsSent),
            BulkDataFragmentsSent = Interlocked.Read(ref _bulkDataFragmentsSent),
            TransferPriorityMode = (TransferPriorityMode)Volatile.Read(ref _transferPriorityMode),
            EncoderName = Role == SessionRole.Sharer ? Encoder.Kind.ToString() : null,
            EncoderHardwareAccelerated = Role == SessionRole.Sharer ? Encoder.IsHardware : null,
            DecoderName = Role == SessionRole.Viewer ? "libvpx VP8" : null,
            DecoderHardwareAccelerated = Role == SessionRole.Viewer ? false : null,
        };
    }

    private static long SaturatingInt64(ulong value) =>
        value > long.MaxValue ? long.MaxValue : (long)value;

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    internal static double EstimateAvailableOutgoingBitrateKbps(
        double measuredBitrateKbps,
        double lossFraction,
        double rttMs,
        double jitterMs,
        double targetBitrateKbps)
    {
        if (measuredBitrateKbps <= 0) return 0;

        // A static desktop often encodes far below its configured maximum. That observed output
        // is not a bandwidth probe, so treating it as one incorrectly drove healthy sessions
        // down the resolution ladder. Only estimate headroom once the encoder is actually using
        // most of its target rate; loss, jitter, RTT and encoder backpressure remain valid
        // degradation signals in every case.
        if (targetBitrateKbps <= 0 || measuredBitrateKbps < targetBitrateKbps * 0.8) return 0;

        var impairment = 1 + (lossFraction * 8) + (rttMs / 1000) + (jitterMs / 500);
        // The estimate may expose a small amount of inferred headroom for bulk traffic, but is
        // capped at 20% above the active target so a heuristic can never open an unbounded file
        // lane. Loss, RTT or jitter reduce that headroom immediately.
        return Math.Min(
            targetBitrateKbps * 1.2,
            measuredBitrateKbps * 1.5 / impairment);
    }

    private double GetIceRttMs()
    {
        var nominated = _peer.GetRtpChannel().NominatedEntry;
        if (nominated is null
            || nominated.LastCheckSentAt == default
            || nominated.LastConnectedResponseAt < nominated.LastCheckSentAt)
        {
            return 0;
        }

        var rtt = (nominated.LastConnectedResponseAt - nominated.LastCheckSentAt).TotalMilliseconds;
        return rtt is >= 0 and <= 60_000 ? rtt : 0;
    }

    private ConnectionPath GetSelectedPath()
    {
        var nominated = _peer.GetRtpChannel().NominatedEntry;
        if (nominated is null || !nominated.Nominated) return ConnectionPath.UnknownNegotiating;

        var local = nominated.LocalCandidate;
        var remote = nominated.RemoteCandidate;

        if (local.type == RTCIceCandidateType.relay || remote.type == RTCIceCandidateType.relay)
            return ConnectionPath.Relayed;

        // A viable local route can be nominated as peer-reflexive when checks beat trickled host
        // candidates. Classify the selected endpoints, not merely their candidate labels.
        if (!TryGetAddress(local.address, out var localAddress)
            || !TryGetAddress(remote.address, out var remoteAddress))
        {
            return ConnectionPath.UnknownNegotiating;
        }

        return IsPrivate(localAddress) && IsPrivate(remoteAddress)
            ? ConnectionPath.DirectLan
            : ConnectionPath.DirectInternet;
    }

    public IPAddress? GetSelectedRemoteAddress()
    {
        if (GetSelectedPath() != ConnectionPath.DirectLan) return null;
        var nominated = _peer.GetRtpChannel().NominatedEntry;
        return nominated is { Nominated: true }
               && TryGetAddress(nominated.RemoteCandidate.address, out var address)
            ? address
            : null;
    }

    private static bool TryGetAddress(string value, out IPAddress address) =>
        IPAddress.TryParse(value, out address!);

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                   || bytes[0] == 127
                   || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.IsIPv6LinkLocal
               || (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _lifetime.CancelAsync();

        if (_capture is not null)
        {
            _capture.FrameArrived -= OnCaptureFrame;
        }

        _queue.Clear();
        _bulkDataReassembler.Clear();
        _bulkInboundRecords.Writer.TryComplete();
        lock (_auxiliaryIceGate)
        {
            foreach (var pending in _pendingAuxiliaryIceCandidates.Values) pending.Clear();
        }
        Volatile.Write(ref _trafficProtector, null);
        _inputPeer?.close();
        _bulkPeer?.close();
        _peer.close();

        State = MediaConnectionState.Closed;
        StateChanged?.Invoke(this, State);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        try
        {
            await CloseAsync();
        }
        catch (Exception ex)
        {
            LogPeerCloseError(_log, ex);
        }

        foreach (var loop in new[] { _encodeLoop, _adaptLoop, _bulkInboundDispatchLoop })
        {
            if (loop is null) continue;

            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // The loop is exiting on its own.
            }
        }

        _encoder?.Dispose();

        lock (_decoderGate)
        {
            _decoder?.Dispose();
        }

        if (_encodedVideoSink is not null)
        {
            _encodedVideoSink.FramePresented -= OnEncodedVideoFramePresented;
            _encodedVideoSink.Dispose();
        }

        _peer.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>Creates peer connections for the session coordinator.</summary>
public sealed class WebRtcMediaEngine(
    ILoggerFactory? loggerFactory = null,
    Func<bool>? hardwareH264Available = null,
    Func<SessionId, IEncodedVideoFrameSink?>? viewerVideoSinkFactory = null) : IMediaEngine
{
    private readonly ILogger _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WebRtcMediaEngine>();

    /// <summary>
    /// Platform detection is injected so this layer stays free of Windows dependencies; the
    /// app passes the real Media Foundation probe.
    /// </summary>
    private readonly VideoEncoderSelector _encoderSelector = new(hardwareH264Available ?? (() => false));
    private readonly Func<SessionId, IEncodedVideoFrameSink?> _viewerVideoSinkFactory =
        viewerVideoSinkFactory ?? (_ => null);

    public Task<IMediaSession> CreateSharerSessionAsync(
        SessionId sessionId, IScreenCaptureSource capture, MediaProfile profile, CancellationToken cancellationToken = default) =>
        Task.FromResult<IMediaSession>(
            WebRtcMediaSession.CreateSharer(sessionId, capture, profile, _log, encoderSelector: _encoderSelector));

    public Task<IMediaSession> CreateViewerSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IMediaSession>(WebRtcMediaSession.CreateViewer(
            sessionId,
            _log,
            encodedVideoSink: _viewerVideoSinkFactory(sessionId)));

    public Task<IMediaSession> CreateSharerSessionAsync(
        SessionId sessionId,
        IScreenCaptureSource capture,
        MediaProfile profile,
        IceConfiguration iceConfiguration,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IMediaSession>(WebRtcMediaSession.CreateSharer(
            sessionId,
            capture,
            profile,
            _log,
            WebRtcMediaSession.BuildConfiguration(iceConfiguration),
            _encoderSelector,
            iceConfiguration));

    public Task<IMediaSession> CreateViewerSessionAsync(
        SessionId sessionId,
        IceConfiguration iceConfiguration,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IMediaSession>(WebRtcMediaSession.CreateViewer(
            sessionId,
            _log,
            WebRtcMediaSession.BuildConfiguration(iceConfiguration),
            iceConfiguration,
            encodedVideoSink: _viewerVideoSinkFactory(sessionId)));

    public Task<IMediaSession> CreateSharerSessionAsync(
        SessionId sessionId,
        IScreenCaptureSource? capture,
        MediaProfile profile,
        IceConfiguration iceConfiguration,
        SessionPermission permissions,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IMediaSession>(WebRtcMediaSession.CreateSharer(
            sessionId,
            capture,
            profile,
            _log,
            WebRtcMediaSession.BuildConfiguration(iceConfiguration, permissions),
            _encoderSelector,
            iceConfiguration,
            permissions));

    public Task<IMediaSession> CreateViewerSessionAsync(
        SessionId sessionId,
        IceConfiguration iceConfiguration,
        SessionPermission permissions,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IMediaSession>(WebRtcMediaSession.CreateViewer(
            sessionId,
            _log,
            WebRtcMediaSession.BuildConfiguration(iceConfiguration, permissions),
            iceConfiguration,
            permissions,
            _viewerVideoSinkFactory(sessionId)));
}

internal sealed class Vp8LossRecoveryController
{
    internal static readonly TimeSpan PictureLossIndicationInterval = TimeSpan.FromMilliseconds(500);

    private readonly Lock _pictureLossIndicationGate = new();
    private long _lastPictureLossIndicationTimestamp;
    private bool _hasSentPictureLossIndication;
    private int _pendingKeyFrameRequest;

    internal bool TryAcquirePictureLossIndication(long timestamp)
    {
        lock (_pictureLossIndicationGate)
        {
            if (_hasSentPictureLossIndication
                && Stopwatch.GetElapsedTime(_lastPictureLossIndicationTimestamp, timestamp)
                    < PictureLossIndicationInterval)
            {
                return false;
            }

            _hasSentPictureLossIndication = true;
            _lastPictureLossIndicationTimestamp = timestamp;
            return true;
        }
    }

    internal void RequestKeyFrame() => Interlocked.Exchange(ref _pendingKeyFrameRequest, 1);

    internal bool ConsumeKeyFrameRequest() =>
        Interlocked.Exchange(ref _pendingKeyFrameRequest, 0) == 1;
}

internal sealed class VideoSecurityFailureBudget(int maxConsecutiveFailures)
{
    private int _consecutiveFailures;

    internal int MaximumConsecutiveFailures { get; } = maxConsecutiveFailures > 0
        ? maxConsecutiveFailures
        : throw new ArgumentOutOfRangeException(nameof(maxConsecutiveFailures));

    internal bool IsExhausted =>
        Volatile.Read(ref _consecutiveFailures) >= MaximumConsecutiveFailures;

    internal int RecordFailure() => Interlocked.Increment(ref _consecutiveFailures);

    internal void RecordSuccess() => Interlocked.Exchange(ref _consecutiveFailures, 0);
}
