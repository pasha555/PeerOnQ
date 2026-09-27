using System.Net;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Application.Security;

namespace PeerOnQ.Application.Abstractions;

/// <summary>Live media statistics, surfaced in the viewer's connection panel.</summary>
public sealed record MediaStatistics
{
    public long FramesCaptured { get; init; }
    public long FramesEncoded { get; init; }
    public long FramesDropped { get; init; }
    public long FramesRendered { get; init; }
    public long BytesSent { get; init; }
    public long BytesReceived { get; init; }
    public double CurrentFps { get; init; }
    public double CurrentBitrateKbps { get; init; }
    public double CaptureFps { get; init; }
    public double EncodeFps { get; init; }
    public double DecodeFps { get; init; }
    public double RenderFps { get; init; }
    public double RttMs { get; init; }
    public double PacketLossPercent { get; init; }
    public double JitterMs { get; init; }
    public double AvailableOutgoingBitrateKbps { get; init; }
    public double CaptureToEncodeLatencyMs { get; init; }
    public double DecodeToRenderLatencyMs { get; init; }
    public double CaptureToEncodeLatencyP50Ms { get; init; }
    public double CaptureToEncodeLatencyP95Ms { get; init; }
    public double CaptureToEncodeLatencyP99Ms { get; init; }
    public double DecodeToRenderLatencyP50Ms { get; init; }
    public double DecodeToRenderLatencyP95Ms { get; init; }
    public double DecodeToRenderLatencyP99Ms { get; init; }
    public double CaptureToPresentLatencyP50Ms { get; init; }
    public double CaptureToPresentLatencyP95Ms { get; init; }
    public double CaptureToPresentLatencyP99Ms { get; init; }
    public double FrameAgeClockUncertaintyMs { get; init; }
    public double InputToInjectionLatencyP50Ms { get; init; }
    public double InputToInjectionLatencyP95Ms { get; init; }
    public double InputToInjectionLatencyP99Ms { get; init; }
    public double InputClockUncertaintyMs { get; init; }
    public bool InputDataLaneNegotiated { get; init; }
    public bool InputDataLaneReady { get; init; }
    public long InputDataRecordsSent { get; init; }
    public bool BulkDataLaneNegotiated { get; init; }
    public bool BulkDataLaneReady { get; init; }
    public bool NativeBulkTransportNegotiated { get; init; }
    public bool NativeBulkTransportReady { get; init; }
    public int NativeBulkBudgetKbps { get; init; }
    public double NativeBulkGoodputKbps { get; init; }
    public long NativeBulkFeedbackSamples { get; init; }
    public long SctpAssociationBufferedBytes { get; init; }
    public long InteractiveSctpBufferedBytes { get; init; }
    public long InputSctpBufferedBytes { get; init; }
    public long BulkSctpBufferedBytes { get; init; }
    public int BulkDataFragmentBytes { get; init; }
    public long BulkQueueBudgetBytes { get; init; }
    public long BulkDataRecordsSent { get; init; }
    public long BulkDataFragmentsSent { get; init; }
    public ConnectionHealth ConnectionHealth { get; init; } = ConnectionHealth.Unknown;
    public QualityProfile? ActiveQualityProfile { get; init; }
    public int? AdaptiveQualityLevel { get; init; }
    public string? QualityChangeReason { get; init; }
    public int TargetFps { get; init; }
    public int TargetBitrateKbps { get; init; }
    public int EncoderQueueDepth { get; init; }
    public TransferPriorityMode TransferPriorityMode { get; init; } = TransferPriorityMode.Balanced;
    public ConnectionPath ConnectionPath { get; init; } = ConnectionPath.UnknownNegotiating;
    public string? RelayServerId { get; init; }
    public string? RelayRegion { get; init; }
    public ReconnectState ReconnectState { get; init; } = ReconnectState.Connected;
    public int ReconnectAttempts { get; init; }
    public string? LastInterruptionReason { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public int RequestedWidth { get; init; }
    public int RequestedHeight { get; init; }
    public int EncodedWidth { get; init; }
    public int EncodedHeight { get; init; }
    public int DecodedWidth { get; init; }
    public int DecodedHeight { get; init; }
    public int RenderedWidth { get; init; }
    public int RenderedHeight { get; init; }
    public string? EncoderName { get; init; }
    public bool? EncoderHardwareAccelerated { get; init; }
    public string? DecoderName { get; init; }
    public bool? DecoderHardwareAccelerated { get; init; }
    public TimeSpan Elapsed { get; init; }

    public static MediaStatistics Empty { get; } = new();
}

public readonly record struct PeerClockEstimate(
    long RemoteMinusLocalOffsetMicroseconds,
    long UncertaintyMicroseconds);

public readonly record struct InputLatencyMeasurement(
    double LatencyMilliseconds,
    double ClockUncertaintyMilliseconds);

/// <summary>
/// Address-free viewer presentation measurements returned to the active sharer. These values
/// tune media quality only; they never grant a capability or bypass session authorization.
/// </summary>
public sealed record RemoteSessionQualityFeedback(
    double DecodeFps,
    double RenderFps,
    double DecodeToRenderLatencyP95Ms,
    double CaptureToPresentLatencyP95Ms,
    double FrameAgeClockUncertaintyMs,
    double InputToInjectionLatencyP95Ms,
    double InputClockUncertaintyMs);

public readonly record struct PresentedVideoFrame(
    long DecodedTimestamp,
    int Width,
    int Height,
    long CaptureTimestampUnixMicroseconds,
    long CaptureSequenceNumber,
    PeerClockEstimate? PeerClockEstimate);

public enum MediaConnectionState
{
    New = 0,
    Connecting = 1,
    Connected = 2,
    Disconnected = 3,
    Failed = 4,
    Closed = 5,
}

public enum RemotePixelFormat
{
    /// <summary>Packed 24-bit BGR, one byte per channel. What the VP8 decoder hands back.</summary>
    Bgr24 = 0,

    /// <summary>Planar Y + U + V, 12 bits per pixel.</summary>
    I420 = 1,
}

/// <summary>
/// A decoded frame ready for the viewer to draw. The pixel format is carried explicitly so a
/// renderer never has to guess at the buffer layout.
/// </summary>
public sealed record RemoteVideoFrame(
    int Width,
    int Height,
    ReadOnlyMemory<byte> Pixels,
    RemotePixelFormat Format,
    TimeSpan Timestamp)
{
    /// <summary>
    /// True only when the producer gives the renderer sole ownership of the backing buffer.
    /// This avoids a second full-frame copy for decoded 4K frames.
    /// </summary>
    public bool BufferOwnershipCanTransfer { get; init; }

    /// <summary>
    /// Local monotonic timestamp assigned after this exact decoded frame is available. The viewer
    /// carries it through latest-frame coalescing so render latency is never attributed to another
    /// frame. It is process-local and is not an end-to-end cross-device timestamp.
    /// </summary>
    public long PipelineDecodedTimestamp { get; init; }

    /// <summary>Authenticated sender capture time. Zero means the peer did not negotiate frame timing.</summary>
    public long CaptureTimestampUnixMicroseconds { get; init; }

    /// <summary>Authenticated capture sequence used to preserve per-frame timing correlation.</summary>
    public long CaptureSequenceNumber { get; init; }

    /// <summary>Best encrypted peer-clock estimate available when this exact frame was decoded.</summary>
    public PeerClockEstimate? PeerClockEstimate { get; init; }

    /// <summary>Bytes one full frame occupies in this format.</summary>
    public int ExpectedLength => Format switch
    {
        RemotePixelFormat.Bgr24 => Width * Height * 3,
        RemotePixelFormat.I420 => Width * Height * 3 / 2,
        _ => 0,
    };
}

/// <summary>
/// One logical media session. Media and collaboration payloads never travel over signaling. Every
/// session creates one ordered primary channel for the mandatory authenticated hybrid handshake;
/// negotiated peers isolate input and file records on separate peer connections/SCTP associations
/// while application messages remain permission-gated.
/// </summary>
public enum DataMessagePriority
{
    Interactive = 0,
    Normal = 1,
    Bulk = 2,
}

public interface IMediaSession : IAsyncDisposable
{
    SessionId SessionId { get; }
    SessionRole Role { get; }
    MediaConnectionState State { get; }

    event EventHandler<MediaConnectionState>? StateChanged;
    event EventHandler<(string Candidate, string? SdpMid, ushort SdpMLineIndex)>? LocalIceCandidate;
    event EventHandler<RemoteVideoFrame>? RemoteFrameReceived;
    event EventHandler? DataChannelReady
    {
        add { }
        remove { }
    }

    event EventHandler<ReadOnlyMemory<byte>>? DataMessageReceived
    {
        add { }
        remove { }
    }

    event EventHandler<string>? SecurityError
    {
        add { }
        remove { }
    }

    bool IsDataChannelReady => false;

    void SetTrafficProtector(ISessionTrafficProtector? protector) { }

    void SetTransferPriorityMode(TransferPriorityMode mode) { }

    bool IsVideoFrameTelemetryNegotiated => false;

    bool IsInputAcknowledgementNegotiated => false;

    bool IsInputDataLaneNegotiated => false;

    bool IsInputDataLaneReady => false;

    bool IsBulkDataLaneNegotiated => false;

    bool IsBulkDataLaneReady => false;

    bool IsNativeBulkTransportNegotiated => false;

    /// <summary>
    /// Selected peer address used only to attempt an authenticated same-LAN native bulk path.
    /// It is never included in diagnostics or application messages.
    /// </summary>
    IPAddress? GetSelectedRemoteAddress() => null;

    void SetPeerClockEstimate(PeerClockEstimate? estimate) { }

    PeerClockEstimate? GetPeerClockEstimate() => null;

    void ReportInputLatency(InputLatencyMeasurement measurement) { }

    void ReportRemoteQualityFeedback(RemoteSessionQualityFeedback feedback) { }

    Task SendDataAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default,
        DataMessagePriority priority = DataMessagePriority.Normal) =>
        Task.FromException(new InvalidOperationException("This session has no authorized data channel."));

    /// <summary>Sharer side: produces the SDP offer that carries the single outbound video track.</summary>
    Task<string> CreateOfferAsync(CancellationToken cancellationToken = default);

    /// <summary>Viewer side: answers the sharer's offer.</summary>
    Task<string> CreateAnswerAsync(string remoteOffer, CancellationToken cancellationToken = default);

    Task ApplyRemoteAnswerAsync(string answer, CancellationToken cancellationToken = default);

    Task AddRemoteIceCandidateAsync(string candidate, string? sdpMid, ushort sdpMLineIndex, CancellationToken cancellationToken = default);

    /// <summary>Starts ICE restart and returns the new offer. The sharer remains the offerer.</summary>
    Task<string> RestartIceAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new NotSupportedException("This media engine does not support ICE restart."));

    /// <summary>Called only after the UI successfully paints a decoded frame.</summary>
    void ReportFrameRendered(long decodedTimestamp = 0, int width = 0, int height = 0) { }

    /// <summary>Correlates the exact authenticated capture timestamp with a successfully painted frame.</summary>
    void ReportFrameRendered(PresentedVideoFrame frame) =>
        ReportFrameRendered(frame.DecodedTimestamp, frame.Width, frame.Height);

    MediaStatistics GetStatistics();

    Task CloseAsync(CancellationToken cancellationToken = default);
}

public interface IMediaEngine
{
    /// <summary>Sharer: sends one video track fed by the capture source. No input, no audio.</summary>
    Task<IMediaSession> CreateSharerSessionAsync(
        SessionId sessionId,
        IScreenCaptureSource capture,
        MediaProfile profile,
        CancellationToken cancellationToken = default);

    Task<IMediaSession> CreateSharerSessionAsync(
        SessionId sessionId,
        IScreenCaptureSource capture,
        MediaProfile profile,
        IceConfiguration iceConfiguration,
        CancellationToken cancellationToken = default) =>
        CreateSharerSessionAsync(sessionId, capture, profile, cancellationToken);

    /// <summary>Viewer: receive-only.</summary>
    Task<IMediaSession> CreateViewerSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<IMediaSession> CreateViewerSessionAsync(
        SessionId sessionId,
        IceConfiguration iceConfiguration,
        CancellationToken cancellationToken = default) =>
        CreateViewerSessionAsync(sessionId, cancellationToken);

    Task<IMediaSession> CreateSharerSessionAsync(
        SessionId sessionId,
        IScreenCaptureSource? capture,
        MediaProfile profile,
        IceConfiguration iceConfiguration,
        SessionPermission permissions,
        CancellationToken cancellationToken = default) =>
        capture is null
            ? Task.FromException<IMediaSession>(new InvalidOperationException("This media engine requires a screen capture source."))
            : CreateSharerSessionAsync(sessionId, capture, profile, iceConfiguration, cancellationToken);

    Task<IMediaSession> CreateViewerSessionAsync(
        SessionId sessionId,
        IceConfiguration iceConfiguration,
        SessionPermission permissions,
        CancellationToken cancellationToken = default) =>
        CreateViewerSessionAsync(sessionId, iceConfiguration, cancellationToken);
}

public enum QualityProfile
{
    Automatic = 0,
    Performance = 1,
    Balanced = 2,
    Quality = 3,
    Office = 4,
    LowBandwidth = 5,
}

public sealed record MediaProfile
{
    public QualityProfile Quality { get; init; } = QualityProfile.Automatic;
    public int TargetFps { get; init; } = 30;
    public CaptureResolution Resolution { get; init; } = CaptureResolution.Automatic;
    public int MaxBitrateKbps { get; init; } = 4000;
    public bool IncludeCursor { get; init; } = true;

    /// <summary>The current protocol does not negotiate audio.</summary>
    public static bool AudioEnabled => false;

    public static MediaProfile Conservative { get; } = new();

    public static MediaProfile For(QualityProfile quality) => quality switch
    {
        QualityProfile.Automatic => new MediaProfile
        {
            Quality = quality,
            TargetFps = 30,
            Resolution = CaptureResolution.Automatic,
            MaxBitrateKbps = 4000,
        },
        QualityProfile.Performance => new MediaProfile
        {
            Quality = quality,
            TargetFps = 60,
            Resolution = CaptureResolution.P1080,
            MaxBitrateKbps = 12_000,
        },
        QualityProfile.Balanced => new MediaProfile
        {
            Quality = quality,
            TargetFps = 30,
            Resolution = CaptureResolution.P1080,
            MaxBitrateKbps = 5000,
        },
        QualityProfile.Quality => new MediaProfile
        {
            Quality = quality,
            TargetFps = 30,
            Resolution = CaptureResolution.P2160,
            MaxBitrateKbps = 36_000,
        },
        QualityProfile.Office => new MediaProfile
        {
            Quality = quality,
            TargetFps = 30,
            Resolution = CaptureResolution.P1080,
            MaxBitrateKbps = 4500,
        },
        QualityProfile.LowBandwidth => new MediaProfile
        {
            Quality = quality,
            TargetFps = 15,
            Resolution = CaptureResolution.P720,
            MaxBitrateKbps = 1200,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(quality)),
    };
}
