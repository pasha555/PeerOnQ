using System.Runtime.InteropServices;

namespace PeerOnQ.Platform.Windows;

/// <summary>
/// Opts the process out of Windows power throttling (EcoQoS). A sharer's PeerOnQ window is usually
/// in the background, and Windows 11 throttles such a process and ignores its timer-resolution
/// requests: the AMD hardware H.264 MFT then returned each frame after about 31 ms (two 15.6 ms
/// timer ticks), capping a session at 32 fps. Opted out, the same encoder took 6.3 ms at 1080p,
/// 8.6 ms at 1440p and 15 ms at 4K (ScreenContentProbeTests on an RX 5600 XT).
/// </summary>
public static class ProcessPowerThrottling
{
    private const int ProcessPowerThrottlingClass = 4;
    private const uint StateVersion = 1;
    private const uint ExecutionSpeed = 0x1;
    private const uint IgnoreTimerResolution = 0x4;

    /// <summary>True when Windows accepted the opt-out.</summary>
    public static bool OptOut() =>
        // IgnoreTimerResolution exists from Windows 11; Windows 10 rejects the unknown bit.
        TrySet(ExecutionSpeed | IgnoreTimerResolution) || TrySet(ExecutionSpeed);

    private static bool TrySet(uint controlMask)
    {
        // A control bit with a clear state bit means "never throttle this behavior".
        var state = new PowerThrottlingState { Version = StateVersion, ControlMask = controlMask, StateMask = 0 };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, ref state,
            (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        nint process, int informationClass, ref PowerThrottlingState information, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();
}
