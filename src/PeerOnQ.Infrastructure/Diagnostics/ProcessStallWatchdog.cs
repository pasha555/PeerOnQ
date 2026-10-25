using Microsoft.Extensions.Logging;

namespace PeerOnQ.Infrastructure.Diagnostics;

/// <summary>
/// Logs when the UI thread, the thread pool or a long-running loop stops making progress, and
/// when it recovers. Field 0.9.83 clients froze after a session closed: no logs, no cloud loop,
/// no signaling refresh, and no crash report. This runs on its own thread, so a starved thread
/// pool or a blocked UI thread is still reported.
/// </summary>
public sealed class ProcessStallWatchdog : IDisposable
{
    private readonly ILogger _log;
    private readonly TimeSpan _interval;
    private readonly List<Probe> _probes = [];
    private readonly ManualResetEventSlim _stop = new();
    private readonly object _gate = new();
    private Thread? _thread;

    public ProcessStallWatchdog(ILogger log, TimeSpan? interval = null)
    {
        _log = log;
        _interval = interval ?? TimeSpan.FromSeconds(5);
    }

    private sealed class Probe(string name, TimeSpan stallAfter)
    {
        public string Name { get; } = name;
        public TimeSpan StallAfter { get; } = stallAfter;
        public Func<Action, bool>? Post { get; init; }
        public Func<DateTimeOffset?>? LastActivity { get; init; }
        public long PostedAt;
        public int Pending;
        public DateTimeOffset? StalledSince;
    }

    /// <summary>A queue that must run a posted no-op within <paramref name="stallAfter"/> (UI, thread pool).</summary>
    public void AddPostedProbe(string name, Func<Action, bool> post, TimeSpan stallAfter)
    {
        lock (_gate) _probes.Add(new Probe(name, stallAfter) { Post = post });
    }

    /// <summary>A loop whose last activity must stay within <paramref name="stallAfter"/>; null means idle.</summary>
    public void AddHeartbeatProbe(string name, Func<DateTimeOffset?> lastActivity, TimeSpan stallAfter)
    {
        lock (_gate) _probes.Add(new Probe(name, stallAfter) { LastActivity = lastActivity });
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "PeerOnQ stall watchdog", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    /// <summary>One check pass (the thread calls this every interval; tests call it directly).</summary>
    public void Check(DateTimeOffset now)
    {
        Probe[] probes;
        lock (_gate) probes = [.. _probes];
        foreach (var probe in probes)
        {
            TimeSpan silent;
            if (probe.Post is { } post)
            {
                if (Volatile.Read(ref probe.Pending) == 0)
                {
                    // The previous no-op ran: the queue is alive again; post the next one.
                    Recovered(probe, now);
                    Interlocked.Exchange(ref probe.PostedAt, now.UtcTicks);
                    Interlocked.Exchange(ref probe.Pending, 1);
                    if (!post(() => Interlocked.Exchange(ref probe.Pending, 0)))
                        Interlocked.Exchange(ref probe.Pending, 0);
                    continue;
                }
                silent = now - new DateTimeOffset(Interlocked.Read(ref probe.PostedAt), TimeSpan.Zero);
            }
            else if (probe.LastActivity?.Invoke() is { } last)
            {
                silent = now - last;
            }
            else
            {
                continue;
            }

            if (silent >= probe.StallAfter && probe.StalledSince is null)
            {
                probe.StalledSince = now - silent;
                ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
                _log.LogWarning(
                    "{Probe} has not responded for {Seconds:0} s (thread pool: {Threads} threads, {Available} available, {Pending} queued work items)",
                    probe.Name, silent.TotalSeconds, ThreadPool.ThreadCount, availableWorkers, ThreadPool.PendingWorkItemCount);
            }
            else if (silent < probe.StallAfter)
            {
                Recovered(probe, now);
            }
        }
    }

    private void Recovered(Probe probe, DateTimeOffset now)
    {
        if (probe.StalledSince is not { } since) return;
        probe.StalledSince = null;
        _log.LogInformation("{Probe} responds again after {Seconds:0} s", probe.Name, (now - since).TotalSeconds);
    }

    private void Run()
    {
        while (!_stop.Wait(_interval))
        {
            try
            {
                Check(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Stall watchdog pass failed");
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
