using System.Text.Json;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Abstractions;

public enum ConnectionPath
{
    UnknownNegotiating = 0,
    DirectLan = 1,
    DirectInternet = 2,
    Relayed = 3,
}

public enum IceTransportPolicy
{
    All = 0,
    RelayOnly = 1,
}

/// <summary>A STUN or TURN endpoint issued by signaling for one authenticated session.</summary>
public sealed record IceServerDefinition
{
    public required IReadOnlyList<string> Urls { get; init; }
    public string? Username { get; init; }
    public string? Credential { get; init; }
    public DateTimeOffset? CredentialExpiresAt { get; init; }
}

public sealed record IceConfiguration
{
    public IReadOnlyList<IceServerDefinition> Servers { get; init; } = [];
    public IceTransportPolicy TransportPolicy { get; init; } = IceTransportPolicy.All;
    public string? RelayServerId { get; init; }
    public string? RelayRegion { get; init; }

    public static IceConfiguration DirectOnly { get; } = new();
}

public sealed record SessionResumeResult(
    SessionId SessionId,
    bool Resumed,
    string Reason,
    DateTimeOffset? ResumeExpiresAt);

/// <summary>The other authenticated participant returned and must receive fresh ICE state.</summary>
public sealed record SessionPeerResumedNotification(SessionId SessionId);

public sealed record SessionQualityNotification(
    SessionId SessionId,
    double CaptureFps,
    double EncodeFps,
    double PacketLossPercent,
    double JitterMs,
    double AvailableOutgoingBitrateKbps,
    long FramesDropped,
    int SourceWidth = 0,
    int SourceHeight = 0,
    int RequestedWidth = 0,
    int RequestedHeight = 0,
    int EncodedWidth = 0,
    int EncodedHeight = 0,
    string? EncoderName = null,
    bool? EncoderHardwareAccelerated = null,
    ConnectionHealth ConnectionHealth = ConnectionHealth.Unknown,
    QualityProfile? ActiveQualityProfile = null,
    int? AdaptiveQualityLevel = null,
    string? QualityChangeReason = null,
    int TargetFps = 0,
    int TargetBitrateKbps = 0,
    int EncoderQueueDepth = 0,
    double DecodeFps = 0,
    double RenderFps = 0,
    double DecodeToRenderLatencyP95Ms = 0,
    double CaptureToPresentLatencyP95Ms = 0,
    double FrameAgeClockUncertaintyMs = 0,
    double InputToInjectionLatencyP95Ms = 0,
    double InputClockUncertaintyMs = 0);

public sealed record SessionTimelineEntry(
    DateTimeOffset OccurredAtUtc,
    string Code,
    string Description);

public sealed record SessionRemoteFrame(SessionId SessionId, RemoteVideoFrame Frame);

/// <summary>Address-free quality telemetry exchanged only between current session participants.</summary>
public interface ISessionQualitySignaling
{
    event EventHandler<SessionQualityNotification>? QualityReceived;

    Task SendQualityAsync(
        SessionQualityNotification quality,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional signaling capability used when a server supports short-lived resume tokens.</summary>
public interface IResumableSignalingClient
{
    event EventHandler<SessionPeerResumedNotification>? PeerResumed;

    Task<SessionResumeResult> ResumeSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Safety boundary for input-capable builds. Implementations must atomically disable injection
/// and release every pressed key and pointer button when a connection becomes uncertain.
/// </summary>
public interface IInputSafetyController
{
    void DisableAndReleaseAll();
    void RestoreApprovedScope(SessionPermission approvedPermissions);
}

public sealed class NoOpInputSafetyController : IInputSafetyController
{
    public static NoOpInputSafetyController Instance { get; } = new();

    private NoOpInputSafetyController() { }

    public void DisableAndReleaseAll() { }

    public void RestoreApprovedScope(SessionPermission approvedPermissions) { }
}

public enum RemoteInputEventKind
{
    PointerMove = 0,
    PointerButton = 1,
    PointerWheel = 2,
    Key = 3,
}

public enum RemotePointerButton
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

/// <summary>
/// Address-free input command. Pointer coordinates are normalized to the display the sharer
/// explicitly selected, so no local monitor geometry crosses the encrypted session.
/// </summary>
public sealed record RemoteInputEvent
{
    public required RemoteInputEventKind Kind { get; init; }
    public double NormalizedX { get; init; }
    public double NormalizedY { get; init; }
    public RemotePointerButton Button { get; init; }
    public bool IsPressed { get; init; }
    public int WheelDelta { get; init; }
    public bool IsHorizontalWheel { get; init; }
    public ushort VirtualKey { get; init; }
    public bool IsExtendedKey { get; init; }

    public bool IsValid() => Kind switch
    {
        RemoteInputEventKind.PointerMove => HasValidPosition(),
        RemoteInputEventKind.PointerButton =>
            HasValidPosition() && Button is >= RemotePointerButton.Left and <= RemotePointerButton.X2,
        RemoteInputEventKind.PointerWheel =>
            HasValidPosition() && WheelDelta is >= -1200 and <= 1200 and not 0,
        RemoteInputEventKind.Key => VirtualKey is > 0 and <= 254,
        _ => false,
    };

    private bool HasValidPosition() =>
        double.IsFinite(NormalizedX) && double.IsFinite(NormalizedY)
        && NormalizedX is >= 0 and <= 1
        && NormalizedY is >= 0 and <= 1;
}

/// <summary>
/// Platform input boundary. Implementations must enforce the immutable permission mask again,
/// reject input while disabled, and release held state on every interruption or session end.
/// </summary>
public interface IRemoteInputSink : IInputSafetyController
{
    void SetCaptureTarget(CaptureTargetInfo? target);
    void ReleaseAll();
    bool TryInject(RemoteInputEvent input);
}

/// <summary>Produces a diagnostics document that contains no candidate addresses.</summary>
public static class ConnectionDiagnosticsExporter
{
    public static string ExportSanitized(
        MediaStatistics statistics,
        IReadOnlyList<SessionTimelineEntry>? timeline = null) =>
        JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.UtcNow,
            connectionPath = statistics.ConnectionPath.ToString(),
            relayServerId = statistics.RelayServerId,
            relayRegion = statistics.RelayRegion,
            rttMs = statistics.RttMs,
            packetLossPercent = statistics.PacketLossPercent,
            jitterMs = statistics.JitterMs,
            availableOutgoingBitrateKbps = statistics.AvailableOutgoingBitrateKbps,
            currentBitrateKbps = statistics.CurrentBitrateKbps,
            captureFps = statistics.CaptureFps,
            encodeFps = statistics.EncodeFps,
            decodeFps = statistics.DecodeFps,
            renderFps = statistics.RenderFps,
            sourceWidth = statistics.SourceWidth,
            sourceHeight = statistics.SourceHeight,
            requestedWidth = statistics.RequestedWidth,
            requestedHeight = statistics.RequestedHeight,
            encodedWidth = statistics.EncodedWidth,
            encodedHeight = statistics.EncodedHeight,
            decodedWidth = statistics.DecodedWidth,
            decodedHeight = statistics.DecodedHeight,
            renderedWidth = statistics.RenderedWidth,
            renderedHeight = statistics.RenderedHeight,
            encoderName = statistics.EncoderName,
            encoderHardwareAccelerated = statistics.EncoderHardwareAccelerated,
            decoderName = statistics.DecoderName,
            decoderHardwareAccelerated = statistics.DecoderHardwareAccelerated,
            captureToEncodeP50Ms = statistics.CaptureToEncodeLatencyP50Ms,
            captureToEncodeP95Ms = statistics.CaptureToEncodeLatencyMs,
            captureToEncodeP99Ms = statistics.CaptureToEncodeLatencyP99Ms,
            decodeToRenderP50Ms = statistics.DecodeToRenderLatencyP50Ms,
            decodeToRenderP95Ms = statistics.DecodeToRenderLatencyMs,
            decodeToRenderP99Ms = statistics.DecodeToRenderLatencyP99Ms,
            captureToPresentP50Ms = statistics.CaptureToPresentLatencyP50Ms,
            captureToPresentP95Ms = statistics.CaptureToPresentLatencyP95Ms,
            captureToPresentP99Ms = statistics.CaptureToPresentLatencyP99Ms,
            frameAgeClockUncertaintyMs = statistics.FrameAgeClockUncertaintyMs,
            inputToInjectionP50Ms = statistics.InputToInjectionLatencyP50Ms,
            inputToInjectionP95Ms = statistics.InputToInjectionLatencyP95Ms,
            inputToInjectionP99Ms = statistics.InputToInjectionLatencyP99Ms,
            inputClockUncertaintyMs = statistics.InputClockUncertaintyMs,
            inputDataLaneNegotiated = statistics.InputDataLaneNegotiated,
            inputDataLaneReady = statistics.InputDataLaneReady,
            inputDataRecordsSent = statistics.InputDataRecordsSent,
            bulkDataLaneNegotiated = statistics.BulkDataLaneNegotiated,
            bulkDataLaneReady = statistics.BulkDataLaneReady,
            nativeBulkTransportNegotiated = statistics.NativeBulkTransportNegotiated,
            nativeBulkTransportReady = statistics.NativeBulkTransportReady,
            nativeBulkBudgetKbps = statistics.NativeBulkBudgetKbps,
            nativeBulkGoodputKbps = statistics.NativeBulkGoodputKbps,
            nativeBulkFeedbackSamples = statistics.NativeBulkFeedbackSamples,
            sctpAssociationBufferedBytes = statistics.SctpAssociationBufferedBytes,
            interactiveSctpBufferedBytes = statistics.InteractiveSctpBufferedBytes,
            inputSctpBufferedBytes = statistics.InputSctpBufferedBytes,
            bulkSctpBufferedBytes = statistics.BulkSctpBufferedBytes,
            bulkDataFragmentBytes = statistics.BulkDataFragmentBytes,
            bulkQueueBudgetBytes = statistics.BulkQueueBudgetBytes,
            bulkDataRecordsSent = statistics.BulkDataRecordsSent,
            bulkDataFragmentsSent = statistics.BulkDataFragmentsSent,
            connectionHealth = statistics.ConnectionHealth.ToString(),
            activeQualityProfile = statistics.ActiveQualityProfile?.ToString(),
            adaptiveQualityLevel = statistics.AdaptiveQualityLevel,
            qualityChangeReason = statistics.QualityChangeReason,
            targetFps = statistics.TargetFps,
            targetBitrateKbps = statistics.TargetBitrateKbps,
            encoderQueueDepth = statistics.EncoderQueueDepth,
            transferPriorityMode = statistics.TransferPriorityMode.ToString(),
            framesDropped = statistics.FramesDropped,
            reconnectState = statistics.ReconnectState.ToString(),
            reconnectAttempts = statistics.ReconnectAttempts,
            lastInterruptionReason = statistics.LastInterruptionReason,
            sessionTimeline = (timeline ?? [])
                .Take(128)
                .Select(entry => new
                {
                    entry.OccurredAtUtc,
                    entry.Code,
                    entry.Description,
                }),
        }, new JsonSerializerOptions { WriteIndented = true });
}
