namespace PeerOnQ.Observability.Tests;

public sealed class Phase3AdminMetricsBridgeTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void LocalPhase3Metrics_StayOnAnInternalSharedDockerNetwork()
    {
        var phase3Compose = Read("src/PeerOnQ.Realtime.Deployment/docker-compose.local.yml");
        var phase6Compose = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml");
        var phase6Bridge = Read("src/PeerOnQ.Infrastructure.Deployment/docker-compose.local-phase3-observability.yml");
        var prometheus = Read("src/PeerOnQ.Infrastructure.Deployment/observability/prometheus.yml");
        var emptyTargets = Read("src/PeerOnQ.Infrastructure.Deployment/observability/local-phase3-targets.empty.json");
        var developmentTargets = Read("src/PeerOnQ.Infrastructure.Deployment/observability/local-phase3-targets.development.json");

        Assert.Contains("AllowedHosts: ${PEERONQ_LOCAL_SIGNAL_HOST:-127.0.0.1};peeronq-phase3-signaling;signaling", phase3Compose, StringComparison.Ordinal);
        Assert.Contains("aliases:\n          - peeronq-phase3-signaling", Normalize(phase3Compose), StringComparison.Ordinal);
        Assert.Contains("name: peeronq-local-observability", phase3Compose, StringComparison.Ordinal);
        Assert.Contains("network_mode: service:signaling", ExtractService(phase3Compose, "signaling-metrics"), StringComparison.Ordinal);
        Assert.Contains("signaling-metrics.conf", ExtractService(phase3Compose, "signaling-metrics"), StringComparison.Ordinal);
        Assert.DoesNotContain("local-observability:", ExtractService(phase6Compose, "prometheus"), StringComparison.Ordinal);
        Assert.Contains("local-phase3-targets.empty.json", ExtractService(phase6Compose, "prometheus"), StringComparison.Ordinal);
        Assert.Contains("local-observability:", ExtractService(phase6Bridge, "prometheus"), StringComparison.Ordinal);
        Assert.Contains("name: peeronq-local-observability", phase6Bridge, StringComparison.Ordinal);
        Assert.Contains("local-phase3-targets.development.json", phase6Bridge, StringComparison.Ordinal);
        Assert.Contains("job_name: local-phase3-signaling", prometheus, StringComparison.Ordinal);
        Assert.Contains("files: [/etc/prometheus/targets/local-phase3.json]", prometheus, StringComparison.Ordinal);
        Assert.Equal("[]", emptyTargets.Trim());
        Assert.Contains("peeronq-phase3-signaling:8081", developmentTargets, StringComparison.Ordinal);
    }

    [Fact]
    public void BothWindowsControllers_IdempotentlyRequireTheInternalNetwork()
    {
        var phase3 = Read("scripts/windows/peeronq-phase3-local.ps1");
        var phase6 = Read("scripts/windows/peeronq-phase6-dev.ps1");
        var helper = Read("scripts/windows/PeerOnQ.LocalDockerNetwork.ps1");

        foreach (var controller in new[] { phase3, phase6 })
        {
            Assert.Contains("PeerOnQ.LocalDockerNetwork.ps1", controller, StringComparison.Ordinal);
            Assert.Contains("Initialize-PeerOnQLocalInternalDockerNetwork", controller, StringComparison.Ordinal);
        }
        Assert.Contains("network create --driver bridge --internal", helper, StringComparison.Ordinal);
        Assert.Contains("network inspect --format '{{.Driver}}|{{.Scope}}|{{.Internal}}'", helper, StringComparison.Ordinal);
        Assert.Contains("bridge|local|true", helper, StringComparison.Ordinal);
        Assert.Contains("[switch]$RotateCertificate", phase3, StringComparison.Ordinal);
        Assert.Contains("certificate-backups", phase3, StringComparison.Ordinal);
        Assert.Contains("rebuild every pinned LAN client", phase3, StringComparison.Ordinal);
        Assert.Contains("docker-compose.local-phase3-observability.yml", phase6, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase3SignalingLogs_UseTheExistingAsyncRedactedCollectorPipeline()
    {
        var phase3Compose = Read("src/PeerOnQ.Realtime.Deployment/docker-compose.local.yml");
        var collector = Read("src/PeerOnQ.Infrastructure.Deployment/observability/otel-collector.yml");

        var signaling = ExtractService(phase3Compose, "signaling");
        Assert.Contains("driver: fluentd", signaling, StringComparison.Ordinal);
        Assert.Contains("fluentd-address: 172.29.61.10:24224", signaling, StringComparison.Ordinal);
        Assert.Contains("fluentd-async: \"true\"", signaling, StringComparison.Ordinal);
        Assert.Contains("tag: peeronq.signaling.phase3", signaling, StringComparison.Ordinal);
        Assert.Contains("logs/signaling:", collector, StringComparison.Ordinal);
        Assert.Contains("transform/realtime-redaction", collector, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string ExtractService(string compose, string serviceName)
    {
        var normalized = Normalize(compose);
        var marker = $"\n  {serviceName}:\n";
        var markerStart = normalized.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerStart >= 0, $"Compose service '{serviceName}' was not found.");
        var start = markerStart + 1;
        var end = normalized.IndexOf("\n  ", start + marker.Length, StringComparison.Ordinal);
        while (end >= 0 && end + 3 < normalized.Length && char.IsWhiteSpace(normalized[end + 3]))
            end = normalized.IndexOf("\n  ", end + 3, StringComparison.Ordinal);
        return end < 0 ? normalized[start..] : normalized[start..end];
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PeerOnQ.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
