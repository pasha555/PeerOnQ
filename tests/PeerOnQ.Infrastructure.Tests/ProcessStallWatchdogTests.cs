using Microsoft.Extensions.Logging;
using PeerOnQ.Infrastructure.Diagnostics;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

public sealed class ProcessStallWatchdogTests
{
    [Fact]
    public void A_blocked_queue_is_reported_once_and_its_recovery_is_logged()
    {
        var log = new RecordingLogger();
        using var watchdog = new ProcessStallWatchdog(log);
        Action? pending = null;
        watchdog.AddPostedProbe("UI thread", work => { pending = work; return true; }, TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.Parse("2026-10-24T10:00:00Z");

        watchdog.Check(now);                       // posts a no-op that never runs (blocked UI thread)
        watchdog.Check(now.AddSeconds(5));
        Assert.Empty(log.Warnings);
        watchdog.Check(now.AddSeconds(11));
        watchdog.Check(now.AddSeconds(16));
        Assert.Single(log.Warnings);
        Assert.Contains("UI thread", log.Warnings[0], StringComparison.Ordinal);

        pending!();                                // the UI thread runs again
        watchdog.Check(now.AddSeconds(20));
        Assert.Contains(log.Information, line => line.Contains("responds again", StringComparison.Ordinal));
    }

    [Fact]
    public void A_loop_whose_heartbeat_stops_is_reported_and_an_idle_loop_is_not()
    {
        var log = new RecordingLogger();
        using var watchdog = new ProcessStallWatchdog(log);
        var now = DateTimeOffset.Parse("2026-10-24T10:00:00Z");
        DateTimeOffset? cloudHeartbeat = now;
        watchdog.AddHeartbeatProbe("Cloud loop", () => cloudHeartbeat, TimeSpan.FromSeconds(90));
        watchdog.AddHeartbeatProbe("Disabled loop", () => null, TimeSpan.FromSeconds(1));

        watchdog.Check(now.AddSeconds(60));
        Assert.Empty(log.Warnings);
        watchdog.Check(now.AddSeconds(120));
        Assert.Single(log.Warnings);

        cloudHeartbeat = now.AddSeconds(125);
        watchdog.Check(now.AddSeconds(130));
        Assert.Contains(log.Information, line => line.Contains("Cloud loop responds again", StringComparison.Ordinal));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public List<string> Information { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (logLevel == LogLevel.Warning) Warnings.Add(line);
            else if (logLevel == LogLevel.Information) Information.Add(line);
        }
    }
}
