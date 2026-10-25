using System.Diagnostics;

namespace PeerOnQ.Infrastructure.Diagnostics;

public sealed record ProcessPerformanceSample(
    DateTimeOffset TimestampUtc,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    int ThreadCount);

public sealed class ProcessPerformanceSampler
{
    private readonly Process _process;
    private readonly Lock _gate = new();
    private DateTimeOffset _lastWallClock;
    private TimeSpan _lastCpu;

    public ProcessPerformanceSampler(Process? process = null)
    {
        _process = process ?? Process.GetCurrentProcess();
        _lastWallClock = DateTimeOffset.UtcNow;
        _lastCpu = _process.TotalProcessorTime;
    }

    public ProcessPerformanceSample Sample()
    {
        lock (_gate)
        {
            _process.Refresh();
            var now = DateTimeOffset.UtcNow;
            var cpu = _process.TotalProcessorTime;
            var wallSeconds = Math.Max(0.001, (now - _lastWallClock).TotalSeconds);
            var cpuPercent = Math.Clamp(
                (cpu - _lastCpu).TotalSeconds / wallSeconds / Math.Max(1, Environment.ProcessorCount) * 100,
                0,
                100);
            _lastWallClock = now;
            _lastCpu = cpu;

            return new ProcessPerformanceSample(
                now,
                cpuPercent,
                _process.WorkingSet64,
                _process.PrivateMemorySize64,
                GC.GetGCMemoryInfo().HeapSizeBytes,
                _process.Threads.Count);
        }
    }
}
