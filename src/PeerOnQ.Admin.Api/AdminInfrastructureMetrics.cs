using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain;

namespace PeerOnQ.Admin.Api;

public sealed class AdminInfrastructureMetricsOptions
{
    public const string SectionName = "PeerOnQ:AdminInfrastructureMetrics";
    public Uri? PrometheusEndpoint { get; set; }
    public string[] AllowedHosts { get; set; } = [];
    public string? BearerToken { get; set; }
    public int TimeoutSeconds { get; set; } = 3;
    public int CacheSeconds { get; set; } = 10;
    public int SnapshotIntervalSeconds { get; set; } = 60;
    public TimeSpan SnapshotLeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public string Region { get; set; } = "local";
}

public sealed class AdminInfrastructureMetricsOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<AdminInfrastructureMetricsOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminInfrastructureMetricsOptions options)
    {
        if (options.PrometheusEndpoint is null) return ValidateOptionsResult.Success;
        var errors = new List<string>();
        var endpoint = options.PrometheusEndpoint;
        if (!endpoint.IsAbsoluteUri
            || (endpoint.Scheme != Uri.UriSchemeHttps
                && !(environment.IsDevelopment() || environment.IsEnvironment("Testing"))))
            errors.Add("PrometheusEndpoint must be absolute HTTPS outside Development and Testing.");
        if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.Query))
            errors.Add("PrometheusEndpoint must not contain credentials, a query, or a fragment.");
        if (options.AllowedHosts.Length == 0
            || !options.AllowedHosts.Contains(endpoint.IdnHost, StringComparer.OrdinalIgnoreCase))
            errors.Add("PrometheusEndpoint host must be explicitly allowlisted.");
        if (options.TimeoutSeconds is < 1 or > 10) errors.Add("TimeoutSeconds must be between 1 and 10.");
        if (options.CacheSeconds is < 5 or > 60) errors.Add("CacheSeconds must be between 5 and 60.");
        if (options.SnapshotIntervalSeconds is < 30 or > 3600)
            errors.Add("SnapshotIntervalSeconds must be between 30 and 3600.");
        if (options.SnapshotLeaseDuration < TimeSpan.FromSeconds(30)
            || options.SnapshotLeaseDuration > TimeSpan.FromMinutes(10))
            errors.Add("SnapshotLeaseDuration must be between 30 seconds and 10 minutes.");
        if (string.IsNullOrWhiteSpace(options.Region) || options.Region.Length > 64)
            errors.Add("Region is required and must not exceed 64 characters.");
        if (options.BearerToken is { Length: > 0 and < 32 }) errors.Add("Prometheus bearer tokens must contain at least 32 characters.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed record AdminInfrastructureMetric(string State, double? Value, string Unit);

internal sealed record AdminServiceHealthObservation(
    string Service,
    ServiceHealthState State,
    double LatencyMilliseconds);

public sealed record AdminInfrastructureMetricsResponse(
    string State,
    DateTimeOffset ObservedAtUtc,
    AdminInfrastructureMetric ApiLatencyP95Milliseconds,
    AdminInfrastructureMetric WebSocketConnections,
    AdminInfrastructureMetric TurnAllocationsPerSecond,
    AdminInfrastructureMetric TurnBandwidthBytesPerSecond,
    AdminInfrastructureMetric DatabasePoolUsage,
    AdminInfrastructureMetric QueueDepth);

public sealed class AdminInfrastructureMetricsService(
    IHttpClientFactory clients,
    IOptions<AdminInfrastructureMetricsOptions> options,
    TimeProvider timeProvider)
{
    private const int MaximumResponseBytes = 128 * 1024;
    private const int MaximumVectorSamples = 64;
    private const string SignalingOnlineDevicesQuery =
        "sum(peeronq_signaling_devices_online{job=~\"signaling|local-phase3-signaling\"})";
    private const string SignalingActiveSessionsQuery =
        "sum(max by (job) (peeronq_signaling_sessions_active{job=~\"signaling|local-phase3-signaling\"}))";
    private const string HealthJobPattern =
        "cloud-api|admin-api|presence|downloads|signaling|local-phase3-signaling|coturn";
    private static readonly string[] MonitoredServices =
        ["cloud-api", "admin-api", "presence", "downloads", "signaling", "coturn"];
    private static readonly HashSet<string> MonitoredServiceSet = new(MonitoredServices, StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AdminInfrastructureMetricsSnapshot? _cached;
    private DateTimeOffset _cacheExpiresAtUtc;

    public async Task<AdminInfrastructureMetricsResponse> GetAsync(CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(cancellationToken)).Infrastructure;

    internal async Task<AdminOverviewLiveMetrics> GetLiveOverviewAsync(CancellationToken cancellationToken) =>
        (await GetSnapshotAsync(cancellationToken)).LiveOverview;

    private async Task<AdminInfrastructureMetricsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_cached is not null && now < _cacheExpiresAtUtc) return _cached;
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (_cached is not null && now < _cacheExpiresAtUtc) return _cached;
            _cached = await RefreshAsync(now, cancellationToken);
            _cacheExpiresAtUtc = now.AddSeconds(options.Value.CacheSeconds);
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal async Task<IReadOnlyList<AdminServiceHealthObservation>> GetServiceHealthAsync(
        CancellationToken cancellationToken)
    {
        if (options.Value.PrometheusEndpoint is null) return [];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        var queries = await Task.WhenAll(
            QueryVectorAsync($"up{{job=~\"{HealthJobPattern}\"}}", timeout.Token),
            QueryVectorAsync($"scrape_duration_seconds{{job=~\"{HealthJobPattern}\"}}*1000", timeout.Token));
        if (queries[0] is not { } availabilitySamples || queries[1] is not { } latencySamples)
            return UnknownServiceHealth();

        var availabilityByService = availabilitySamples
            .GroupBy(sample => sample.Service, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(sample => sample.Value).ToArray(), StringComparer.Ordinal);
        var latencyByService = latencySamples
            .GroupBy(sample => sample.Service, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(sample => sample.Value), StringComparer.Ordinal);
        var observations = new List<AdminServiceHealthObservation>(MonitoredServices.Length);
        foreach (var service in MonitoredServices)
        {
            if (!availabilityByService.TryGetValue(service, out var availability)
                || !latencyByService.TryGetValue(service, out var latency))
            {
                observations.Add(new AdminServiceHealthObservation(service, ServiceHealthState.Unknown, 0));
                continue;
            }
            var availableTargets = availability.Count(value => value > 0);
            var state = availableTargets == availability.Length
                ? ServiceHealthState.Healthy
                : availableTargets == 0
                    ? ServiceHealthState.Unhealthy
                    : ServiceHealthState.Degraded;
            observations.Add(new AdminServiceHealthObservation(service, state, Math.Min(latency, 60_000)));
        }
        return observations;
    }

    private static IReadOnlyList<AdminServiceHealthObservation> UnknownServiceHealth() =>
        MonitoredServices
            .Select(service => new AdminServiceHealthObservation(service, ServiceHealthState.Unknown, 0))
            .ToArray();

    private async Task<AdminInfrastructureMetricsSnapshot> RefreshAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (options.Value.PrometheusEndpoint is null)
            return new(Unavailable(now), new(null, null));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        var queries = await Task.WhenAll(
            QueryAsync("histogram_quantile(0.95,sum(rate(http_server_request_duration_seconds_bucket[5m])) by (le))*1000", "milliseconds", timeout.Token),
            QueryAsync(SignalingOnlineDevicesQuery, "connections", timeout.Token),
            QueryAsync("sum(rate(peeronq_turn_allocations_total[5m]))", "allocations_per_second", timeout.Token),
            QueryAsync("sum(rate(peeronq_turn_bandwidth_bytes_total[5m]))", "bytes_per_second", timeout.Token),
            QueryAsync("max(peeronq_database_pool_usage)", "connections", timeout.Token),
            QueryAsync("sum(peeronq_queue_depth)", "items", timeout.Token),
            QueryAsync(SignalingActiveSessionsQuery, "sessions", timeout.Token));
        var available = queries.Take(6).Count(value => value.State == "available");
        var infrastructure = new AdminInfrastructureMetricsResponse(
            available == 6 ? "available" : available == 0 ? "unavailable" : "partial",
            now,
            queries[0],
            queries[1],
            queries[2],
            queries[3],
            queries[4],
            queries[5]);
        return new(
            infrastructure,
            new AdminOverviewLiveMetrics(ToCount(queries[1]), ToCount(queries[6])));
    }

    private static long? ToCount(AdminInfrastructureMetric metric)
    {
        if (metric.State != "available" || metric.Value is null || metric.Value > long.MaxValue) return null;
        return checked((long)Math.Floor(metric.Value.Value));
    }

    private async Task<AdminInfrastructureMetric> QueryAsync(
        string fixedQuery,
        string unit,
        CancellationToken cancellationToken)
    {
        using var document = await QueryDocumentAsync(fixedQuery, cancellationToken);
        if (document is null || !TryReadValue(document.RootElement, out var value) || !double.IsFinite(value))
            return new AdminInfrastructureMetric("unavailable", null, unit);
        return new AdminInfrastructureMetric("available", Math.Max(0, value), unit);
    }

    private async Task<IReadOnlyList<PrometheusVectorSample>?> QueryVectorAsync(
        string fixedQuery,
        CancellationToken cancellationToken)
    {
        using var document = await QueryDocumentAsync(fixedQuery, cancellationToken);
        return document is null ? null : TryReadVector(document.RootElement);
    }

    private async Task<JsonDocument?> QueryDocumentAsync(string fixedQuery, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = options.Value.PrometheusEndpoint!;
            var relative = $"api/v1/query?query={Uri.EscapeDataString(fixedQuery)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, relative));
            if (!string.IsNullOrWhiteSpace(options.Value.BearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.BearerToken);
            using var response = await clients.CreateClient("admin-prometheus")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return null;
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 16 }, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryReadValue(JsonElement root, out double value)
    {
        value = 0;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "success"
            || !root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() == 0)
            return false;
        var first = result[0];
        if (!first.TryGetProperty("value", out var pair)
            || pair.ValueKind != JsonValueKind.Array
            || pair.GetArrayLength() != 2)
            return false;
        return double.TryParse(pair[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static IReadOnlyList<PrometheusVectorSample>? TryReadVector(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "success"
            || !root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() > MaximumVectorSamples)
            return null;

        var samples = new List<PrometheusVectorSample>(result.GetArrayLength());
        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("metric", out var metric)
                || !metric.TryGetProperty("job", out var job)
                || job.GetString() is not { } rawService
                || NormalizeMonitoredService(rawService) is not { } service
                || !item.TryGetProperty("value", out var pair)
                || pair.ValueKind != JsonValueKind.Array
                || pair.GetArrayLength() != 2
                || !double.TryParse(pair[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value)
                || value < 0)
                continue;
            samples.Add(new PrometheusVectorSample(service, value));
        }
        return samples;
    }

    private static string? NormalizeMonitoredService(string service)
    {
        if (service == "local-phase3-signaling") return "signaling";
        return MonitoredServiceSet.Contains(service) ? service : null;
    }

    private static AdminInfrastructureMetricsResponse Unavailable(DateTimeOffset now)
    {
        static AdminInfrastructureMetric Metric(string unit) => new("unavailable", null, unit);
        return new AdminInfrastructureMetricsResponse(
            "unavailable", now,
            Metric("milliseconds"), Metric("connections"), Metric("allocations_per_second"),
            Metric("bytes_per_second"), Metric("connections"), Metric("items"));
    }

    private sealed record PrometheusVectorSample(string Service, double Value);
    private sealed record AdminInfrastructureMetricsSnapshot(
        AdminInfrastructureMetricsResponse Infrastructure,
        AdminOverviewLiveMetrics LiveOverview);
}
