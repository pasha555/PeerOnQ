using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Domain.Tests;

public sealed class Phase1SessionScopeTests
{
    [Fact]
    public void Phase4_allows_explicit_custom_and_unattended_scopes_but_rejects_unimplemented_permissions()
    {
        Assert.True(Phase1SessionScope.IsAllowed(
            SessionMode.ViewOnly,
            SessionPermission.ViewScreen,
            SessionAccessKind.Attended));
        Assert.True(Phase1SessionScope.IsAllowed(
            SessionMode.FullControl,
            SessionPermission.ViewScreen | SessionPermission.ControlInput,
            SessionAccessKind.Attended));
        Assert.True(Phase1SessionScope.IsAllowed(
            SessionMode.FullControl,
            Phase1SessionScope.FullControlPermissions,
            SessionAccessKind.Attended));
        Assert.True(Phase1SessionScope.IsAllowed(
            SessionMode.FileTransferOnly,
            SessionPermission.FileTransfer,
            SessionAccessKind.Attended));
        Assert.False(Phase1SessionScope.IsAllowed(
            SessionMode.FullControl,
            SessionPermission.ViewScreen,
            SessionAccessKind.Attended));
        Assert.True(Phase1SessionScope.IsAllowed(
            SessionMode.Custom,
            SessionPermission.ViewScreen | SessionPermission.ControlInput |
            SessionPermission.FileTransfer | SessionPermission.ClipboardText,
            SessionAccessKind.Attended));
        Assert.True(Phase1SessionScope.IsAllowed(
            SessionMode.ViewOnly,
            SessionPermission.ViewScreen,
            SessionAccessKind.Unattended));
        Assert.False(Phase1SessionScope.IsAllowed(
            SessionMode.FileTransferOnly,
            SessionPermission.FileTransfer | SessionPermission.ViewScreen,
            SessionAccessKind.Attended));
        Assert.False(Phase1SessionScope.IsAllowed(
            SessionMode.Custom,
            SessionPermission.ViewScreen | SessionPermission.ClipboardImage,
            SessionAccessKind.Attended));
    }
}
