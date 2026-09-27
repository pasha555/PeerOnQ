namespace PeerOnQ.Domain.Sessions;

/// <summary>
/// Product capability boundary retained under its historical name for compatibility.
/// Phase 4 adds explicit custom collaboration and unattended scopes while rejecting every
/// unknown or unimplemented permission.
/// </summary>
public static class Phase1SessionScope
{
    /// <summary>The safe default retained for existing callers.</summary>
    public const SessionMode Mode = SessionMode.ViewOnly;
    public const SessionPermission Permissions = SessionPermission.ViewScreen;
    public const SessionAccessKind AccessKind = SessionAccessKind.Attended;
    public const SessionPermission FullControlPermissions =
        SessionPermission.ViewScreen | SessionPermission.ControlInput | SessionPermission.FileTransfer;
    public const SessionPermission FileTransferPermissions = SessionPermission.FileTransfer;

    public static bool IsAllowed(
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind) =>
        accessKind is SessionAccessKind.Attended or SessionAccessKind.Unattended or SessionAccessKind.SupportInvitation
        && !permissions.HasFlag(SessionPermission.ClipboardImage)
        && SessionPermissionPolicy.IsValid(mode, permissions);

    public static bool HasViewOnlyPermissions(SessionPermission permissions) => permissions == Permissions;

    public static bool HasInteractiveMediaPermissions(SessionPermission permissions) =>
        permissions != SessionPermission.None
        && (permissions & ~SessionPermissionPolicy.KnownPermissions) == 0
        && !permissions.HasFlag(SessionPermission.ClipboardImage)
        && (!permissions.HasFlag(SessionPermission.ControlInput)
            || permissions.HasFlag(SessionPermission.ViewScreen));

    public static void EnsureAllowed(
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind)
    {
        if (!IsAllowed(mode, permissions, accessKind))
            throw new ArgumentException(
                "PeerOnQ rejected an invalid, unknown, or unimplemented session permission scope.");
    }

    public static void EnsureInteractiveMediaPermissions(SessionPermission permissions)
    {
        if (!HasInteractiveMediaPermissions(permissions))
            throw new ArgumentException(
                "The media session requires a supported screen, input, file-transfer, or text-clipboard scope.", nameof(permissions));
    }
}
