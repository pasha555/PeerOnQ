using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PeerOnQ.Admin.Api;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Admin.Api.Tests;

public sealed class AdminOverviewLiveMetricsTests
{
    [Fact]
    public void Overlay_UsesLivePhase3CountsWithoutReducingPersistedCounts()
    {
        var persisted = Overview(onlineDevices: 2, activeSessions: 3);

        var result = AdminOverviewLiveMetricsOverlay.Apply(
            persisted,
            new AdminOverviewLiveMetrics(OnlineDevices: 5, ActiveSessions: 1));
        var unavailable = AdminOverviewLiveMetricsOverlay.Apply(
            persisted,
            new AdminOverviewLiveMetrics(OnlineDevices: null, ActiveSessions: null));

        Assert.Equal(5, result.OnlineDevices);
        Assert.Equal(5, result.ActiveDevicesToday);
        Assert.Equal(5, result.ActiveDevicesThisMonth);
        Assert.Equal(3, result.ActiveSessions);
        Assert.Equal(persisted, unavailable);
    }

    [Fact]
    public async Task Service_QueriesOnlyTheFixedPhase3SeriesAndCachesTheResult()
    {
        var handler = new PrometheusHandler();
        var service = new AdminInfrastructureMetricsService(
            new StubHttpClientFactory(new HttpClient(handler)),
            Options.Create(new AdminInfrastructureMetricsOptions
            {
                PrometheusEndpoint = new Uri("http://prometheus/"),
                AllowedHosts = ["prometheus"],
                TimeoutSeconds = 3,
                CacheSeconds = 10,
            }),
            TimeProvider.System);

        var first = await service.GetLiveOverviewAsync(CancellationToken.None);
        var cached = await service.GetLiveOverviewAsync(CancellationToken.None);

        Assert.Equal(7, first.OnlineDevices);
        Assert.Equal(4, first.ActiveSessions);
        Assert.Equal(first, cached);
        Assert.Equal(7, handler.Requests.Count);
        var signalingQueries = handler.Requests
            .Where(query => query.Contains("peeronq_signaling_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, signalingQueries.Length);
        Assert.All(signalingQueries, query =>
            Assert.Contains("local-phase3-signaling", query, StringComparison.Ordinal));
        Assert.Contains(signalingQueries, query => query.Contains(
            "sum(peeronq_signaling_devices_online",
            StringComparison.Ordinal));
        Assert.Contains(signalingQueries, query => query.Contains(
            "sum(max by (job) (peeronq_signaling_sessions_active",
            StringComparison.Ordinal));
    }

    private static AdminOverviewV1 Overview(long onlineDevices, long activeSessions) => new(
        TotalDownloads: 0,
        CompletedDownloads: 0,
        UniqueDownloadEstimate: 0,
        TotalInstallations: 0,
        ActiveInstallations: 0,
        OnlineDevices: onlineDevices,
        ActiveDevicesToday: 0,
        ActiveDevicesThisMonth: 0,
        ActiveSessions: activeSessions,
        FailedConnectionAttempts: 0,
        TurnSessions: 0,
        CrashRate: null,
        SessionSuccessRate: null,
        UpdateFailures: 0,
        CurrentStableVersion: null,
        GeneratedAtUtc: DateTimeOffset.Parse("2026-08-24T12:00:00Z"));

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class PrometheusHandler : HttpMessageHandler
    {
        public ConcurrentBag<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Requests.Add(query);
            var value = query.Contains("devices_online", StringComparison.Ordinal) ? "7" : "4";
            var body = "{\"status\":\"success\",\"data\":{\"result\":[{\"value\":[0,\""
                + value
                + "\"]}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
