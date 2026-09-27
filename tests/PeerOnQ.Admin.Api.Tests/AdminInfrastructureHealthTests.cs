using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PeerOnQ.Admin.Api;
using PeerOnQ.Cloud.Domain;

namespace PeerOnQ.Admin.Api.Tests;

public sealed class AdminInfrastructureHealthTests
{
    [Fact]
    public async Task ServiceHealth_RecordsOnlyFixedPrometheusJobsWithRealScrapeLatency()
    {
        var queries = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query["?query=".Length..]);
            queries.Add(query);
            return Json(query.StartsWith("up{", StringComparison.Ordinal) ? AvailabilityJson : LatencyJson);
        }));
        var service = CreateService(client);

        var observations = await service.GetServiceHealthAsync(CancellationToken.None);

        Assert.Equal(2, queries.Count);
        Assert.All(queries, query => Assert.DoesNotContain("unknown-job", query));
        Assert.All(queries, query => Assert.Contains("local-phase3-signaling", query));
        Assert.Collection(
            observations,
            value => AssertObservation(value, "cloud-api", ServiceHealthState.Healthy, 12.5),
            value => AssertObservation(value, "admin-api", ServiceHealthState.Degraded, 8),
            value => AssertObservation(value, "presence", ServiceHealthState.Unhealthy, 30),
            value => AssertObservation(value, "downloads", ServiceHealthState.Unknown, 0),
            value => AssertObservation(value, "signaling", ServiceHealthState.Degraded, 5),
            value => AssertObservation(value, "coturn", ServiceHealthState.Unknown, 0));
    }

    [Fact]
    public void SnapshotStore_CreatesBoundedDomainSnapshotsWithoutChangingMeasurements()
    {
        var regionId = Guid.NewGuid();
        var observedAt = DateTimeOffset.Parse("2026-08-24T09:00:00Z");
        var observations = new[]
        {
            new AdminServiceHealthObservation("cloud-api", ServiceHealthState.Healthy, 11.25),
            new AdminServiceHealthObservation("presence", ServiceHealthState.Unhealthy, 25),
        };

        var snapshots = AdminInfrastructureSnapshotStore.CreateSnapshots(regionId, observations, observedAt);

        Assert.Collection(
            snapshots,
            value =>
            {
                Assert.Equal(regionId, value.RegionId);
                Assert.Equal("cloud-api", value.Service);
                Assert.Equal(ServiceHealthState.Healthy, value.State);
                Assert.Equal(11.25, value.LatencyMilliseconds);
                Assert.Equal(observedAt, value.ObservedAtUtc);
            },
            value =>
            {
                Assert.Equal(regionId, value.RegionId);
                Assert.Equal("presence", value.Service);
                Assert.Equal(ServiceHealthState.Unhealthy, value.State);
                Assert.Equal(25, value.LatencyMilliseconds);
                Assert.Equal(observedAt, value.ObservedAtUtc);
            });
    }

    [Fact]
    public void SnapshotConfiguration_IsBoundedAndRequiresARegion()
    {
        var validator = new AdminInfrastructureMetricsOptionsValidator(new TestEnvironment());
        var options = Options("local", snapshotIntervalSeconds: 60);

        Assert.True(validator.Validate(null, options).Succeeded);

        options.SnapshotIntervalSeconds = 10;
        Assert.True(validator.Validate(null, options).Failed);

        options.SnapshotIntervalSeconds = 60;
        options.Region = string.Empty;
        Assert.True(validator.Validate(null, options).Failed);

        options.Region = "local";
        options.SnapshotLeaseDuration = TimeSpan.FromSeconds(10);
        Assert.True(validator.Validate(null, options).Failed);
    }

    [Fact]
    public async Task ServiceHealth_RecordsUnknownForEveryFixedServiceWhenPrometheusIsUnavailable()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var service = CreateService(client);

        var observations = await service.GetServiceHealthAsync(CancellationToken.None);

        Assert.Equal(6, observations.Count);
        Assert.All(observations, value => Assert.Equal(ServiceHealthState.Unknown, value.State));
    }

    private static AdminInfrastructureMetricsService CreateService(HttpClient client) => new(
        new StubHttpClientFactory(client),
        Microsoft.Extensions.Options.Options.Create(Options("local", snapshotIntervalSeconds: 60)),
        TimeProvider.System);

    private static AdminInfrastructureMetricsOptions Options(string region, int snapshotIntervalSeconds) => new()
    {
        PrometheusEndpoint = new Uri("http://prometheus:9090/"),
        AllowedHosts = ["prometheus"],
        TimeoutSeconds = 3,
        CacheSeconds = 10,
        SnapshotIntervalSeconds = snapshotIntervalSeconds,
        Region = region,
    };

    private static void AssertObservation(
        AdminServiceHealthObservation observation,
        string service,
        ServiceHealthState state,
        double latencyMilliseconds)
    {
        Assert.Equal(service, observation.Service);
        Assert.Equal(state, observation.State);
        Assert.Equal(latencyMilliseconds, observation.LatencyMilliseconds);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json"),
    };

    private const string AvailabilityJson = """
        {"status":"success","data":{"result":[
          {"metric":{"job":"cloud-api","instance":"cloud-api:8080"},"value":[1,"1"]},
          {"metric":{"job":"admin-api","instance":"admin-a:8080"},"value":[1,"1"]},
          {"metric":{"job":"admin-api","instance":"admin-b:8080"},"value":[1,"0"]},
          {"metric":{"job":"presence","instance":"presence:8080"},"value":[1,"0"]},
          {"metric":{"job":"signaling","instance":"signaling:8080"},"value":[1,"1"]},
          {"metric":{"job":"local-phase3-signaling","instance":"peeronq-phase3-signaling:8081"},"value":[1,"0"]},
          {"metric":{"job":"unknown-job","instance":"unknown:8080"},"value":[1,"1"]}
        ]}}
        """;

    private const string LatencyJson = """
        {"status":"success","data":{"result":[
          {"metric":{"job":"cloud-api","instance":"cloud-api:8080"},"value":[1,"12.5"]},
          {"metric":{"job":"admin-api","instance":"admin-a:8080"},"value":[1,"7"]},
          {"metric":{"job":"admin-api","instance":"admin-b:8080"},"value":[1,"8"]},
          {"metric":{"job":"presence","instance":"presence:8080"},"value":[1,"30"]},
          {"metric":{"job":"signaling","instance":"signaling:8080"},"value":[1,"4"]},
          {"metric":{"job":"local-phase3-signaling","instance":"peeronq-phase3-signaling:8081"},"value":[1,"5"]},
          {"metric":{"job":"unknown-job","instance":"unknown:8080"},"value":[1,"999"]}
        ]}}
        """;

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "PeerOnQ.Admin.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
