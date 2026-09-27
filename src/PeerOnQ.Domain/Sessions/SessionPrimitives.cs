using PeerOnQ.Domain.Identity;

namespace PeerOnQ.Domain.Sessions;

public readonly record struct SessionId(Guid Value)
{
    public static SessionId New() => new(Guid.NewGuid());

    public static bool TryParse(string? text, out SessionId id)
    {
        if (Guid.TryParse(text, out var guid))
        {
            id = new SessionId(guid);
            return true;
        }

        id = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

public enum SessionMode
{
    ViewOnly = 0,
    FullControl = 1,
    FileTransferOnly = 2,
    Custom = 3,
}

[Flags]
public enum SessionPermission
{
    None = 0,
    ViewScreen = 1 << 0,
    ControlInput = 1 << 1,
    FileTransfer = 1 << 2,
    ClipboardText = 1 << 3,
    ClipboardImage = 1 << 4,
}

public enum SessionAccessKind
{
    Attended = 0,
    Unattended = 1,
    SupportInvitation = 2,
}

/// <summary>
/// Canonical permission sets. A profile is only a starting point; the accepted immutable
/// permission mask is carried by signaling and checked again by every data-plane operation.
/// Full control includes file transfer but keeps clipboard permissions separate.
/// </summary>
public static class SessionPermissionPolicy
{
    public const SessionPermission KnownPermissions =
        SessionPermission.ViewScreen |
        SessionPermission.ControlInput |
        SessionPermission.FileTransfer |
        SessionPermission.ClipboardText |
        SessionPermission.ClipboardImage;

    public static SessionPermission ForMode(SessionMode mode) => mode switch
    {
        SessionMode.ViewOnly => SessionPermission.ViewScreen,
        SessionMode.FullControl => SessionPermission.ViewScreen | SessionPermission.ControlInput | SessionPermission.FileTransfer,
        SessionMode.FileTransferOnly => SessionPermission.FileTransfer,
        SessionMode.Custom => SessionPermission.None,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static bool IsValid(SessionMode mode, SessionPermission permissions)
    {
        if (permissions == SessionPermission.None || (permissions & ~KnownPermissions) != 0)
            return false;
        if (permissions.HasFlag(SessionPermission.ControlInput)
            && !permissions.HasFlag(SessionPermission.ViewScreen))
            return false;

        if (mode == SessionMode.Custom) return true;
        if (mode == SessionMode.FullControl)
        {
            var legacyFullControl = SessionPermission.ViewScreen | SessionPermission.ControlInput;
            return permissions == legacyFullControl || permissions == ForMode(mode);
        }

        return permissions == ForMode(mode);
    }
}

public enum SessionRole
{
    /// <summary>This device asked to see another screen.</summary>
    Viewer = 0,

    /// <summary>This device shares its screen.</summary>
    Sharer = 1,
}

public enum PermissionDecision
{
    Accept = 0,
    Decline = 1,
    Block = 2,

    /// <summary>No answer within the timeout. Treated exactly like Decline.</summary>
    Timeout = 3,
}

/// <summary>
/// The answer returned by an attended permission prompt. A successful response can narrow the
/// incoming request, but it can never add a permission that was not requested.
/// </summary>
public sealed record PermissionPromptResult(
    PermissionDecision Decision,
    SessionMode? GrantedMode = null,
    SessionPermission? GrantedPermissions = null)
{
    public static PermissionPromptResult ForRequestedScope(
        PermissionDecision decision,
        PermissionRequest request) =>
        decision == PermissionDecision.Accept
            ? new PermissionPromptResult(decision, request.RequestedMode, request.RequestedPermissions)
            : new PermissionPromptResult(decision);
}

public enum SessionEndReason
{
    Unknown = 0,
    EndedByViewer = 1,
    EndedBySharer = 2,
    PermissionDeclined = 3,
    PermissionBlocked = 4,
    PermissionTimeout = 5,
    RequestTimeout = 6,
    NegotiationTimeout = 7,
    ConnectTimeout = 8,
    SignalingLost = 9,
    MediaLost = 10,
    DeviceOffline = 11,
    ApplicationShutdown = 12,
    ProtocolError = 13,
    ReconnectFailed = 14,
    AuthenticationMismatch = 15,
    ResumeConfirmationRequired = 16,
}

/// <summary>An incoming request, exactly as shown in the permission dialog.</summary>
public sealed record PermissionRequest
{
    public required SessionId SessionId { get; init; }
    public required PeerOnQId RequesterId { get; init; }
    public required string RequesterDisplayName { get; init; }
    public required SessionMode RequestedMode { get; init; }
    public SessionPermission RequestedPermissions { get; init; } = SessionPermission.ViewScreen;
    public SessionAccessKind AccessKind { get; init; } = SessionAccessKind.Attended;
    public string? RequesterKeyFingerprint { get; init; }
    public string? SupportNote { get; init; }
    /// <summary>
    /// An attended full-control request may explicitly delegate the final choice between
    /// view-only and full control to this device's user.
    /// </summary>
    public bool RemoteScopeSelectionRequired { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required TimeSpan Timeout { get; init; }

    /// <summary>Masked ID for the dialog: the user sees who is asking, not a copyable address.</summary>
    public string MaskedRequesterId => RequesterId.MaskedDisplay;

    public DateTimeOffset ExpiresAt => RequestedAt + Timeout;
}
