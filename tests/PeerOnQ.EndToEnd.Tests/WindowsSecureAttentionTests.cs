using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Platform.Windows;
using PeerOnQ.Platform.Windows.Capture;
using PeerOnQ.Platform.Windows.Input;
using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>
/// The Windows side of the secure-attention request. The generator seam is never fired as the real
/// Ctrl+Alt+Del here; these assert only the gating and routing around it.
/// </summary>
public sealed class WindowsSecureAttentionTests
{
    private static RemoteInputEvent SecureAttention => new() { Kind = RemoteInputEventKind.SecureAttention };

    [Fact]
    public void A_secure_attention_request_without_an_authorized_host_lease_is_declined() =>
        // A per-user process cannot raise the sign-in screen; it relays to the LocalSystem broker,
        // which declines here (this test process is not an interactive PeerOnQ host with a lease).
        Assert.False(WindowsSessionBrokerClient.TryRequestSecureAttention());

    [Fact]
    public void A_disabled_controller_rejects_secure_attention_without_invoking_the_generator()
    {
        var generated = 0;
        var controller = new WindowsInputController(logger: null, () => { generated++; return true; });

        // No capture target and no Full Control: control is disabled.
        Assert.False(controller.TryInject(SecureAttention));
        Assert.Equal(0, generated);
    }

    [Fact]
    public void An_enabled_controller_routes_secure_attention_to_the_generator_and_accepts_it()
    {
        var target = DisplayEnumerator.ListCaptureTargets().FirstOrDefault(t => !DisplayEnumerator.IsAllDisplays(t.Id));
        if (target is null) return; // No resolvable display on this machine; nothing to enable.

        var generated = 0;
        var controller = new WindowsInputController(logger: null, () => { generated++; return true; });
        controller.SetCaptureTarget(target);
        controller.RestoreApprovedScope(SessionPermission.ControlInput);

        // SecureAttention returns before SendInput, so no real input reaches the machine.
        Assert.True(controller.TryInject(SecureAttention));
        Assert.Equal(1, generated);

        // A generator that reports it could not run is still accepted, so the input channel is not
        // torn down on a device without the secure-desktop service.
        var unavailable = new WindowsInputController(logger: null, () => false);
        unavailable.SetCaptureTarget(target);
        unavailable.RestoreApprovedScope(SessionPermission.ControlInput);
        Assert.True(unavailable.TryInject(SecureAttention));
    }
}
