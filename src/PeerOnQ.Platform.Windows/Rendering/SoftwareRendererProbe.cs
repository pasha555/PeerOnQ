using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace PeerOnQ.Platform.Windows.Rendering;

/// <summary>
/// Names the component after which the WARP software rasterizer (d3d10warp.dll) first became
/// resident. A "hardware" D3D11 device on the Microsoft Basic Render Driver adapter also runs on
/// WARP, so the explicit capture fallback is not the only way it loads: in the 0.9.85 field test
/// one PC loaded it and no log said which component did.
/// </summary>
/// <remarks>
/// 0.9.86 found it on both test PCs "after the main window was shown", with hardware default
/// adapters. A background sampler now watches the first startup seconds and remembers the startup
/// step that last ran before WARP appeared, so the next report places the load between two steps.
/// 0.9.87 still could not say who: a loader notification (<see cref="ModuleLoadTracer"/>) now
/// records the loading thread with its native and managed callers at the moment of the load.
/// PeerOnQ's own devices do not request WARP on a machine with a hardware adapter: the capture
/// device falls back to WARP only after both hardware attempts failed (logged), and the viewer
/// presenter refuses software adapters.
/// </remarks>
public static class SoftwareRendererProbe
{
    internal const string WarpModule = "d3d10warp.dll";
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MinimumLoadTraceWindow = TimeSpan.FromMinutes(2);
    private static int _reported;
    private static int _watching;
    private static string _lastStep = "process start";
    private static string? _firstSeenAfterStep;
    private static long _firstSeenAtMs = -1;
    private static ModuleLoadTracer? _loadTracer;
    private static string? _residentBeforeTraceStep;
    private static int _traceReported;
    private static ILogger? _traceLogger;

    public static bool IsWarpLoaded => GetModuleHandle(WarpModule) != 0;

    /// <summary>Records the startup step that just completed (and checks for WARP right after it).</summary>
    public static void Mark(string step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        NoteIfLoaded();
        Volatile.Write(ref _lastStep, step);
    }

    /// <summary>Samples for WARP every 50 ms on a background thread until it appears or <paramref name="duration"/> ends.</summary>
    public static void StartStartupWatch(TimeSpan duration)
    {
        if (Interlocked.Exchange(ref _watching, 1) != 0) return;
        // The loader notification runs managed code under the loader lock for every DLL load in
        // its window. That is a diagnostic risk the default client must not take (0.9.88 fixes
        // hangs), so it runs only when PEERONQ_TRACE_WARP=1 is set on a test machine.
        if (IsLoadTraceRequested)
            StartLoadTrace(duration > MinimumLoadTraceWindow ? duration : MinimumLoadTraceWindow);
        var thread = new Thread(() =>
        {
            var started = Stopwatch.GetTimestamp();
            while (Volatile.Read(ref _firstSeenAtMs) < 0 && Stopwatch.GetElapsedTime(started) < duration)
            {
                NoteIfLoaded();
                Thread.Sleep(SampleInterval);
            }
        })
        {
            IsBackground = true,
            Name = "PeerOnQ WARP startup probe",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>Logs once per process, the first time WARP is found resident after <paramref name="trigger"/>.</summary>
    public static void ReportIfLoaded(ILogger logger, string trigger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        NoteIfLoaded();
        if (Volatile.Read(ref _reported) != 0 || !IsWarpLoaded) return;
        if (Interlocked.Exchange(ref _reported, 1) != 0) return;
        var (hardwareAdapters, defaultAdapter, defaultIsSoftware) = D3D11CompositionVideoPresenter.DescribeAdapters();
        // Not {Component}: that property name overwrote the logging enricher's Component="desktop".
        logger.LogWarning(
            "The WARP software rasterizer (d3d10warp.dll) is loaded; first seen after {Trigger}. It appeared {FirstSeenMs} ms after start, after the startup step {FirstSeenAfterStep}. Default graphics adapter: {Adapter} (software={Software}), hardware adapters: {HardwareAdapters}. {Justification}",
            trigger,
            Volatile.Read(ref _firstSeenAtMs),
            Volatile.Read(ref _firstSeenAfterStep) ?? "unknown",
            defaultAdapter,
            defaultIsSoftware,
            hardwareAdapters,
            Justify(defaultIsSoftware, hardwareAdapters));
        Volatile.Write(ref _traceLogger, logger);
        ReportLoadTrace(logger, Volatile.Read(ref _loadTracer)?.Trace);
    }

    internal static bool IsLoadTraceRequested =>
        string.Equals(Environment.GetEnvironmentVariable("PEERONQ_TRACE_WARP"), "1", StringComparison.Ordinal);

    /// <summary>Why WARP is resident, as far as PeerOnQ's own devices can tell.</summary>
    internal static string Justify(bool defaultIsSoftware, object? hardwareAdapters)
    {
        if (defaultIsSoftware)
            return "The default adapter is software (Basic Render), so Direct3D clients use WARP; video still uses the hardware adapters when present.";
        var trace = IsLoadTraceRequested
            ? "the load trace below names the loading thread"
            : "set PEERONQ_TRACE_WARP=1 to record the loading thread";
        return $"No PeerOnQ device requested it (capture falls back to WARP only after both hardware attempts fail, which is logged, and the viewer presenter refuses software adapters); the Windows UI framework most likely loaded it, and it does not carry PeerOnQ video. To confirm, {trace}.";
    }

    /// <summary>Registers the loader notification for WARP for <paramref name="window"/> (or until it fires).</summary>
    private static void StartLoadTrace(TimeSpan window)
    {
        try
        {
            if (IsWarpLoaded)
            {
                // Loaded before tracing could start (WinUI bootstrap or earlier); say so instead of guessing.
                Volatile.Write(ref _residentBeforeTraceStep, Volatile.Read(ref _lastStep));
                return;
            }
            var tracer = ModuleLoadTracer.Start(WarpModule, trace =>
            {
                NoteIfLoaded();
                if (Volatile.Read(ref _traceLogger) is { } logger) ReportLoadTrace(logger, trace);
            });
            if (!tracer.IsRegistered) return;
            Volatile.Write(ref _loadTracer, tracer);
            _ = Task.Delay(window).ContinueWith(_ => tracer.Dispose(), TaskScheduler.Default);
        }
        catch (Exception)
        {
            // The sampler and the step marks still work without the loader notification.
        }
    }

    /// <summary>Logs once who loaded WARP (thread, native callers, managed stack).</summary>
    internal static void ReportLoadTrace(ILogger logger, ModuleLoadTrace? trace)
    {
        if (trace is null)
        {
            if (Volatile.Read(ref _residentBeforeTraceStep) is not { } step
                || Interlocked.Exchange(ref _traceReported, 1) != 0) return;
            logger.LogWarning(
                "The WARP software rasterizer was already loaded when its load trace started (after the startup step {Step}); its requester is unknown",
                step);
            return;
        }
        if (Interlocked.Exchange(ref _traceReported, 1) != 0) return;
        logger.LogWarning(
            "The WARP software rasterizer was loaded by thread {ManagedThreadId} ({ThreadName}, OS thread {OsThreadId}); native callers: {NativeCallers}; managed stack: {ManagedStack}",
            trace.ManagedThreadId,
            trace.ThreadName,
            trace.OsThreadId,
            trace.NativeCallers,
            trace.ManagedStack);
    }

    private static void NoteIfLoaded()
    {
        if (Volatile.Read(ref _firstSeenAtMs) >= 0 || !IsWarpLoaded) return;
        var step = Volatile.Read(ref _lastStep);
        var elapsedMs = (long)Stopwatch.GetElapsedTime(StartTimestamp).TotalMilliseconds;
        if (Interlocked.CompareExchange(ref _firstSeenAtMs, elapsedMs, -1) == -1)
            Volatile.Write(ref _firstSeenAfterStep, step);
    }

    internal static void ResetForTesting(string lastStep = "process start")
    {
        Volatile.Write(ref _reported, 0);
        Volatile.Write(ref _firstSeenAtMs, -1);
        Volatile.Write(ref _firstSeenAfterStep, null);
        Volatile.Write(ref _lastStep, lastStep);
        Volatile.Write(ref _traceReported, 0);
        Volatile.Write(ref _residentBeforeTraceStep, null);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(string moduleName);
}
