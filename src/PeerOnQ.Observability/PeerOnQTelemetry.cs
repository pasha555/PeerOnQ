using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace PeerOnQ.Observability;

public static class PeerOnQTelemetry
{
    public const string ActivitySourceName = "PeerOnQ.Cloud";
    public const string MeterName = "PeerOnQ.Cloud";
    public const string Version = "1.0.0";

    public static readonly ActivitySource Activities = new(ActivitySourceName, Version);
}

public sealed class PeerOnQMetrics : IDisposable
{
    private readonly Meter _meter = new(PeerOnQTelemetry.MeterName, PeerOnQTelemetry.Version);
    private readonly Counter<long> _authenticationFailures;
    private readonly Counter<long> _sessionFailures;
    private readonly Counter<long> _sessionCompletions;
    private readonly Counter<long> _downloadStarted;
    private readonly Counter<long> _downloadCompleted;
    private readonly Counter<long> _installationRegistered;
    private readonly Counter<long> _clientCrashes;
    private readonly Counter<long> _updateFailures;
    private readonly Counter<long> _turnAllocations;
    private readonly Counter<long> _turnBytes;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _apiErrors;
    private readonly Func<long> _onlineDevices;
    private readonly Func<long> _activeSessions;
    private readonly Func<long> _presenceConnections;
    private readonly Func<long> _queueDepth;
    private readonly Func<long> _databasePoolUsage;
    private long _authenticationFailureTotal;
    private long _sessionFailureTotal;
    private long _sessionCompletionTotal;
    private long _downloadStartedTotal;
    private long _downloadCompletedTotal;
    private long _installationRegisteredTotal;
    private long _clientCrashTotal;
    private long _updateFailureTotal;
    private long _turnAllocationTotal;
    private long _turnBytesTotal;
    private long _apiErrorTotal;
    private long _requestCount;
    private long _requestDurationMicroseconds;

    public PeerOnQMetrics(PeerOnQMetricState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _onlineDevices = state.OnlineDevices;
        _activeSessions = state.ActiveSessions;
        _presenceConnections = state.PresenceConnections;
        _queueDepth = state.QueueDepth;
        _databasePoolUsage = state.DatabasePoolUsage;

        _meter.CreateObservableGauge("peeronq_online_devices", ObserveOnlineDevices);
        _meter.CreateObservableGauge("peeronq_active_sessions", ObserveActiveSessions);
        _meter.CreateObservableGauge("peeronq_presence_connections", ObservePresenceConnections);
        _meter.CreateObservableGauge("peeronq_queue_depth", ObserveQueueDepth);
        _meter.CreateObservableGauge("peeronq_database_pool_usage", ObserveDatabasePoolUsage);

        _authenticationFailures = _meter.CreateCounter<long>("peeronq_authentication_failures_total");
        _sessionFailures = _meter.CreateCounter<long>("peeronq_session_failures_total");
        _sessionCompletions = _meter.CreateCounter<long>("peeronq_session_completions_total");
        _downloadStarted = _meter.CreateCounter<long>("peeronq_download_started_total");
        _downloadCompleted = _meter.CreateCounter<long>("peeronq_download_completed_total");
        _installationRegistered = _meter.CreateCounter<long>("peeronq_installation_registered_total");
        _clientCrashes = _meter.CreateCounter<long>("peeronq_client_crashes_total");
        _updateFailures = _meter.CreateCounter<long>("peeronq_update_failures_total");
        _turnAllocations = _meter.CreateCounter<long>("peeronq_turn_allocations_total");
        _turnBytes = _meter.CreateCounter<long>("peeronq_turn_bandwidth_bytes_total");
        _requestDuration = _meter.CreateHistogram<double>("peeronq_api_request_duration_seconds", "s");
        _apiErrors = _meter.CreateCounter<long>("peeronq_api_errors_total");
    }

    public void AuthenticationFailed(string reason)
    {
        Interlocked.Increment(ref _authenticationFailureTotal);
        _authenticationFailures.Add(1, OutcomeTag(reason));
    }

    public void SessionFailed(string stage)
    {
        Interlocked.Increment(ref _sessionFailureTotal);
        _sessionFailures.Add(1, StageTag(stage));
    }

    public void SessionCompleted(bool succeeded)
    {
        Interlocked.Increment(ref _sessionCompletionTotal);
        _sessionCompletions.Add(1, OutcomeTag(succeeded ? "success" : "failure"));
    }

    public void DownloadStarted(string platform, string architecture)
    {
        Interlocked.Increment(ref _downloadStartedTotal);
        _downloadStarted.Add(1, PlatformTag(platform), ArchitectureTag(architecture));
    }

    public void DownloadCompleted(string result)
    {
        Interlocked.Increment(ref _downloadCompletedTotal);
        _downloadCompleted.Add(1, OutcomeTag(result));
    }

    public void InstallationRegistered(string platform, string architecture)
    {
        Interlocked.Increment(ref _installationRegisteredTotal);
        _installationRegistered.Add(1, PlatformTag(platform), ArchitectureTag(architecture));
    }

    public void ClientCrash(string platform)
    {
        Interlocked.Increment(ref _clientCrashTotal);
        _clientCrashes.Add(1, PlatformTag(platform));
    }

    public void UpdateFailed(string stage)
    {
        Interlocked.Increment(ref _updateFailureTotal);
        _updateFailures.Add(1, StageTag(stage));
    }

    public void TurnAllocation(string transport)
    {
        Interlocked.Increment(ref _turnAllocationTotal);
        _turnAllocations.Add(1, new KeyValuePair<string, object?>("transport", NormalizeTransport(transport)));
    }

    public void TurnBandwidth(long bytes, string direction)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _turnBytesTotal, bytes);
        _turnBytes.Add(bytes, new KeyValuePair<string, object?>("direction", direction.Equals("out", StringComparison.OrdinalIgnoreCase) ? "out" : "in"));
    }

    public void RequestCompleted(double elapsedSeconds, string method, string route, int statusCode)
    {
        var tags = new TagList
        {
            { "http.request.method", NormalizeMethod(method) },
            { "http.route", NormalizeRoute(route) },
            { "http.response.status_code", statusCode },
        };

        _requestDuration.Record(Math.Max(0, elapsedSeconds), tags);
        Interlocked.Increment(ref _requestCount);
        Interlocked.Add(ref _requestDurationMicroseconds, (long)Math.Min(long.MaxValue, Math.Max(0, elapsedSeconds * 1_000_000)));
        if (statusCode >= 500)
        {
            Interlocked.Increment(ref _apiErrorTotal);
            _apiErrors.Add(1, tags);
        }
    }

    public string ExportPrometheus()
    {
        var builder = new StringBuilder(1024);
        AppendGauge(builder, "peeronq_online_devices", SafeRead(_onlineDevices));
        AppendGauge(builder, "peeronq_active_sessions", SafeRead(_activeSessions));
        AppendGauge(builder, "peeronq_presence_connections", SafeRead(_presenceConnections));
        AppendGauge(builder, "peeronq_queue_depth", SafeRead(_queueDepth));
        AppendGauge(builder, "peeronq_database_pool_usage", SafeRead(_databasePoolUsage));
        AppendCounter(builder, "peeronq_authentication_failures_total", Volatile.Read(ref _authenticationFailureTotal));
        AppendCounter(builder, "peeronq_session_failures_total", Volatile.Read(ref _sessionFailureTotal));
        AppendCounter(builder, "peeronq_session_completions_total", Volatile.Read(ref _sessionCompletionTotal));
        AppendCounter(builder, "peeronq_download_started_total", Volatile.Read(ref _downloadStartedTotal));
        AppendCounter(builder, "peeronq_download_completed_total", Volatile.Read(ref _downloadCompletedTotal));
        AppendCounter(builder, "peeronq_installation_registered_total", Volatile.Read(ref _installationRegisteredTotal));
        AppendCounter(builder, "peeronq_client_crashes_total", Volatile.Read(ref _clientCrashTotal));
        AppendCounter(builder, "peeronq_update_failures_total", Volatile.Read(ref _updateFailureTotal));
        AppendCounter(builder, "peeronq_turn_allocations_total", Volatile.Read(ref _turnAllocationTotal));
        AppendCounter(builder, "peeronq_turn_bandwidth_bytes_total", Volatile.Read(ref _turnBytesTotal));
        AppendCounter(builder, "peeronq_api_errors_total", Volatile.Read(ref _apiErrorTotal));
        AppendCounter(builder, "peeronq_api_requests_total", Volatile.Read(ref _requestCount));
        AppendCounter(builder, "peeronq_api_request_duration_microseconds_total", Volatile.Read(ref _requestDurationMicroseconds));
        return builder.ToString();
    }

    private static void AppendGauge(StringBuilder builder, string name, long value) =>
        Append(builder, name, "gauge", value);

    private static void AppendCounter(StringBuilder builder, string name, long value) =>
        Append(builder, name, "counter", value);

    private static void Append(StringBuilder builder, string name, string type, long value)
    {
        builder.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        builder.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private Measurement<long> ObserveOnlineDevices() => new(SafeRead(_onlineDevices));
    private Measurement<long> ObserveActiveSessions() => new(SafeRead(_activeSessions));
    private Measurement<long> ObservePresenceConnections() => new(SafeRead(_presenceConnections));
    private Measurement<long> ObserveQueueDepth() => new(SafeRead(_queueDepth));
    private Measurement<long> ObserveDatabasePoolUsage() => new(SafeRead(_databasePoolUsage));

    private static long SafeRead(Func<long> valueFactory)
    {
        try { return Math.Max(0, valueFactory()); }
        catch { return 0; }
    }

    private static KeyValuePair<string, object?> OutcomeTag(string value) =>
        new("outcome", Normalize(value, ["success", "failure", "expired", "replay", "locked", "invalid", "blocked"]));

    private static KeyValuePair<string, object?> StageTag(string value) =>
        new("stage", Normalize(value, ["registration", "authentication", "signaling", "ice", "turn", "media", "update", "unknown"]));

    private static KeyValuePair<string, object?> PlatformTag(string value) =>
        new("platform", Normalize(value, ["windows", "macos", "linux", "android", "ios"]));

    private static KeyValuePair<string, object?> ArchitectureTag(string value) =>
        new("architecture", Normalize(value, ["x64", "arm64", "x86"]));

    private static string NormalizeTransport(string value) =>
        Normalize(value, ["udp", "tcp", "tls"]);

    private static string NormalizeMethod(string value) =>
        Normalize(value, ["GET", "POST", "PUT", "PATCH", "DELETE"]);

    private static string NormalizeRoute(string route) =>
        route.StartsWith('/') && route.Length <= 120 && !route.Any(char.IsDigit) ? route : "unmatched";

    private static string Normalize(string value, string[] allowed) =>
        allowed.FirstOrDefault(item => item.Equals(value, StringComparison.OrdinalIgnoreCase))?.ToLowerInvariant() ?? "unknown";

    public void Dispose() => _meter.Dispose();
}

public sealed class PeerOnQMetricState
{
    public Func<long> OnlineDevices { get; init; } = static () => 0;
    public Func<long> ActiveSessions { get; init; } = static () => 0;
    public Func<long> PresenceConnections { get; init; } = static () => 0;
    public Func<long> QueueDepth { get; init; } = static () => 0;
    public Func<long> DatabasePoolUsage { get; init; } = static () => 0;
}
