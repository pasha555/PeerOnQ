using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// Sub-millisecond-accurate waits for one dedicated thread. The default Windows timer can sleep
/// 15.6 ms for Thread.Sleep(1), which would turn packet pacing back into bursts; a
/// high-resolution waitable timer (Windows 10 1803+) waits about half a millisecond without
/// raising the global timer resolution. Other platforms sleep, and the final fraction spins.
/// </summary>
internal sealed class HighResolutionWaiter : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly TimeSpan SpinThreshold = TimeSpan.FromMilliseconds(0.6);

    private nint _timer;

    public HighResolutionWaiter()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            _timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
    }

    public bool IsHighResolution => _timer != 0;

    public void Wait(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        var started = Stopwatch.GetTimestamp();
        var coarse = duration - SpinThreshold;
        if (coarse > TimeSpan.Zero)
        {
            if (_timer != 0)
            {
                // Negative due time is relative, in 100 ns units.
                var dueTime = -Math.Max(1, coarse.Ticks);
                if (SetWaitableTimerEx(_timer, ref dueTime, 0, 0, 0, 0, 0))
                    WaitForSingleObject(_timer, Infinite);
                else
                    Thread.Sleep(coarse);
            }
            else
            {
                Thread.Sleep(coarse);
            }
        }

        var spinner = new SpinWait();
        while (Stopwatch.GetElapsedTime(started) < duration)
            spinner.SpinOnce(sleep1Threshold: -1);
    }

    public void Dispose()
    {
        var timer = Interlocked.Exchange(ref _timer, 0);
        if (timer != 0) CloseHandle(timer);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(
        nint timer, ref long dueTime, int period, nint completionRoutine, nint argument, nint wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
