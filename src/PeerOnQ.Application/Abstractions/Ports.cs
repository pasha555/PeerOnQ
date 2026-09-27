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
    public required ReadOnlyMemory<byte> I420 { get; init; }
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
