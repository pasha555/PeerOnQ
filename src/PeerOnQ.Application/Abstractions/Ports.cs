using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Abstractions;

/// <summary>Persistence of the non-secret half of the device identity.</summary>
public interface IDeviceIdentityRepository
{
    Task<DeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DeviceIdentity identity, CancellationToken cancellationToken = default);
}

/// <summary>
/// OS-protected storage for private material. Implementations must never write plaintext
/// secrets to the SQLite database or to logs.
/// </summary>
public interface IDeviceSecretStore
{
    Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default);
    Task SetAsync(string name, byte[] secret, CancellationToken cancellationToken = default);
    Task RemoveAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>Blocked peers stored locally for the sharing device.</summary>
public interface IBlockedDeviceStore
{
    Task<bool> IsBlockedAsync(PeerOnQId id, CancellationToken cancellationToken = default);
    Task BlockAsync(PeerOnQId id, CancellationToken cancellationToken = default);
    Task UnblockAsync(PeerOnQId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PeerOnQId>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Local audit trail of sessions. Never contains media or secrets.</summary>
public interface ISessionAuditLog
{
    Task RecordAsync(SessionAuditEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionAuditEntry>> RecentAsync(int take = 50, CancellationToken cancellationToken = default);
}

public interface ISecurityAuditLog
{
    Task AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecurityAuditEvent>> ReadRecentAsync(int limit, CancellationToken cancellationToken = default);

    Task<AuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
    Task ExportSanitizedJsonLinesAsync(string destinationFile, CancellationToken cancellationToken = default);
    Task ApplyRetentionAsync(TimeSpan retention, CancellationToken cancellationToken = default);
    Task ClearAsync(bool confirmed, CancellationToken cancellationToken = default);
}

public sealed record AuditIntegrityResult(bool IsValid, int VerifiedRecords, string? Failure);

public sealed record SessionAuditEntry
{
    public required SessionId SessionId { get; init; }
    public required SessionRole Role { get; init; }
    public required string PeerMaskedId { get; init; }
    public required string PeerDisplayName { get; init; }
    public required SessionMode Mode { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public SessionEndReason EndReason { get; init; } = SessionEndReason.Unknown;
}

/// <summary>
/// Shows the incoming-session dialog. Returning <see cref="PermissionDecision.Timeout"/> is
/// equivalent to a decline; implementations must never auto-accept.
/// </summary>
public interface IPermissionPrompt
{
    Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets a modern attended prompt narrow a request to an approved scope. Existing prompts
    /// retain their exact-scope behavior through this default implementation.
    /// </summary>
    async Task<PermissionPromptResult> AskForScopeAsync(
        PermissionRequest request,
        CancellationToken cancellationToken = default) =>
        PermissionPromptResult.ForRequestedScope(
            await AskAsync(request, cancellationToken).ConfigureAwait(false),
            request);
}

/// <summary>Screen source, implemented by PeerOnQ.Platform.Windows on top of Windows.Graphics.Capture.</summary>
public interface IScreenCaptureSource : IAsyncDisposable
{
    bool IsCapturing { get; }
    CaptureTargetInfo? Target { get; }

    event EventHandler<CapturedFrame>? FrameArrived;
    event EventHandler<CaptureStoppedReason>? CaptureStopped;

    Task StartAsync(CaptureRequest request, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implemented by capture sources that can change what and how they capture without a
/// restart. Used by the adaptive quality controller and by the viewer's monitor selector.
/// </summary>
public interface IAdaptiveCaptureSource
{
    /// <summary>Displays this device can share, refreshed on every call.</summary>
    IReadOnlyList<CaptureTargetInfo> AvailableTargets { get; }

    /// <summary>1 = requested resolution, 2 = half, 4 = quarter. Applied on the next frame.</summary>
    int DownscaleFactor { get; }

    void SetDownscaleFactor(int factor);

    /// <summary>Maximum capture rate before GPU readback and pixel conversion.</summary>
    int TargetFps { get; }

    void SetTargetFps(int fps);

    /// <summary>Switches the captured display mid-session; the peer keeps the same connection.</summary>
    Task SwitchTargetAsync(CaptureTargetInfo target, CancellationToken cancellationToken = default);
}

public enum CaptureTargetKind
{
    Display = 0,
    Window = 1,
}

public enum CaptureStoppedReason
{
    StoppedByUser = 0,
    TargetClosed = 1,
    DeviceLost = 2,
    Error = 3,
    ApplicationShutdown = 4,
}

public sealed record CaptureTargetInfo(CaptureTargetKind Kind, string Id, string DisplayName, int Width, int Height);

public sealed record CaptureRequest
{
    public required CaptureTargetInfo Target { get; init; }
    public bool IncludeCursor { get; init; } = true;
    public int MaxFramesPerSecond { get; init; } = 30;
    public CaptureResolution Resolution { get; init; } = CaptureResolution.Automatic;
}

public enum CaptureResolution
{
    Automatic = 0,
    P720 = 1,
    P1080 = 2,
    Native = 3,
    P1440 = 4,
    P2160 = 5,
}

/// <summary>
/// Streaming tiers: a 4K-class display (short edge of at least 2160 pixels) streams native 4K
/// when the request allows it, every other display streams a 1080-pixel short edge. Legacy wire
/// values remain readable; old hosts normalize every request to 1080p on their side.
/// </summary>
public static class CaptureResolutionPolicy
{
    /// <summary>Short edge of the standard tier; larger non-4K displays are reduced to it.</summary>
    public const int StandardShortEdge = 1080;

    /// <summary>Short edge of the 4K tier and the largest short edge ever streamed.</summary>
    public const int MaximumShortEdge = 2160;

    /// <summary>
    /// 4K frames must fit the hardware H.264 window (level 5.1 class encoders); a 4K-class source
    /// that would exceed it, such as a portrait or 5K2K display, uses the standard tier instead.
    /// </summary>
    public const int MaximumUltraHdWidth = 4096;
    public const int MaximumUltraHdHeight = 2304;

    public static CaptureResolution Normalize(CaptureResolution resolution) => resolution switch
    {
        CaptureResolution.P720 or CaptureResolution.P1080 => resolution,
        CaptureResolution.P1440 => CaptureResolution.P1080,
        CaptureResolution.Automatic or CaptureResolution.Native or CaptureResolution.P2160 => CaptureResolution.Automatic,
        _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
    };

    /// <summary>True when the request lets a 4K-class display stream at the 4K tier.</summary>
    public static bool AllowsUltraHd(CaptureResolution resolution) =>
        Normalize(resolution) == CaptureResolution.Automatic;

    /// <summary>True when this physical source would stream at the 4K tier at full quality.</summary>
    public static bool IsUltraHdSource(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return false;
        var shortEdge = Math.Min(sourceWidth, sourceHeight);
        if (shortEdge < MaximumShortEdge) return false;
        var scale = (double)MaximumShortEdge / shortEdge;
        return Math.Round(sourceWidth * scale) <= MaximumUltraHdWidth
               && Math.Round(sourceHeight * scale) <= MaximumUltraHdHeight;
    }

    /// <summary>
    /// Target short edge before even-size rounding. Adaptive downscale factors move a 4K source
    /// to the standard tier; they never reduce the standard tier itself or upscale a small source.
    /// </summary>
    public static int ResolveTargetShortEdge(
        int sourceWidth,
        int sourceHeight,
        CaptureResolution resolution,
        int downscaleFactor = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(downscaleFactor);
        var shortEdge = Math.Min(sourceWidth, sourceHeight);
        if (AllowsUltraHd(resolution) && downscaleFactor == 1 && IsUltraHdSource(sourceWidth, sourceHeight))
            return MaximumShortEdge;
        return Math.Min(shortEdge, StandardShortEdge);
    }
}

public enum CapturedPixelFormat
{
    /// <summary>Y plane, then U and V quarter planes.</summary>
    I420 = 0,

    /// <summary>Y plane, then one interleaved UV quarter plane.</summary>
    Nv12 = 1,
}

/// <summary>
/// One captured frame in a WebRTC-compatible layout (I420). Consumers must copy a buffer they
/// retain beyond the callback unless the producer explicitly transfers ownership below.
/// </summary>
public sealed class CapturedFrame
{
    /// <summary>Physical capture-target dimensions before profile scaling.</summary>
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }

    /// <summary>Dimensions selected by the user/profile before adaptive downscaling.</summary>
    public int RequestedWidth { get; init; }
    public int RequestedHeight { get; init; }

    /// <summary>Actual dimensions handed to the encoder after adaptive downscaling.</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>
    /// Tightly packed 4:2:0 pixels in the layout named by <see cref="Format"/>: planar I420 by
    /// default, or NV12 when the producer converted on the GPU for a hardware H.264 encoder.
    /// </summary>
    public required ReadOnlyMemory<byte> I420 { get; init; }

    public CapturedPixelFormat Format { get; init; } = CapturedPixelFormat.I420;
    public required TimeSpan Timestamp { get; init; }
    public required long SequenceNumber { get; init; }

    /// <summary>
    /// True only when the producer created this buffer for this frame and will never mutate or
    /// reuse it. Media can then transfer the buffer into its bounded queue without another copy.
    /// </summary>
    public bool BufferOwnershipCanTransfer { get; init; }

    /// <summary>Stopwatch timestamp assigned when media accepts this frame into its local pipeline.</summary>
    public long PipelineEnteredTimestamp { get; init; }

    /// <summary>UTC capture time in Unix microseconds for negotiated cross-device frame-age telemetry.</summary>
    public long CaptureTimestampUnixMicroseconds { get; init; }
}
