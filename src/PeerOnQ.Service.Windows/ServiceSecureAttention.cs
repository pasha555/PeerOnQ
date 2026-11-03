using System.Runtime.InteropServices;

namespace PeerOnQ.Service.Windows;

/// <summary>
/// Raises the Windows secure-attention sequence (Ctrl+Alt+Del) from the LocalSystem broker through
/// the documented SendSAS API. SendSAS with <c>asUser = false</c> is the service path: it shows the
/// secure screen on the active console session and grants nothing — the person still signs in with
/// their own password or PIN. It works only because the broker runs as LocalSystem and the
/// installer enables the SoftwareSASGeneration policy for services.
/// </summary>
internal static class ServiceSecureAttention
{
    // Debounce: repeated clicks within this window reuse the one screen already being shown rather
    // than hammering Winlogon. A debounced repeat still reports success to the caller.
    private static readonly long CooldownTicks = TimeSpan.FromMilliseconds(750).Ticks;
    private static long _lastTicks;

    internal static bool Generate()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastTicks) < CooldownTicks) return true;
        Interlocked.Exchange(ref _lastTicks, now);
        try
        {
            SendSAS(asUser: false);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);
}
