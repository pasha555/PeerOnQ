using System.Runtime.Versioning;

namespace PeerOnQ.Platform.Windows.Input;

/// <summary>
/// Relays a secure-attention request (Ctrl+Alt+Del) to the LocalSystem session broker, which is the
/// only component that can raise the secure screen and that holds the SoftwareSASGeneration policy.
/// It never bypasses sign-in: the person still enters their own password or PIN on the secure
/// screen. The per-user host cannot reach the secure desktop itself, so without the broker service
/// installed and enabled the request has no effect.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSecureAttention
{
    /// <summary>
    /// Dispatches the request to the broker off the caller's thread, so the input lock is never
    /// held for the pipe round-trip. Returns true to mean "dispatched"; the broker logs the outcome.
    /// </summary>
    internal static bool TryGenerate()
    {
        _ = Task.Run(static () =>
        {
            try { WindowsSessionBrokerClient.TryRequestSecureAttention(); }
            catch { /* Best effort; the broker, when present, raises the screen or declines. */ }
        });
        return true;
    }
}
