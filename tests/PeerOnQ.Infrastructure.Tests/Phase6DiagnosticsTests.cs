using System.IO.Compression;
using System.Text.Json;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Infrastructure.Diagnostics;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

public sealed class Phase6DiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "peeronq-phase6-diag", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Consent_is_required_and_bundle_is_allowlist_sanitized()
    {
        var logs = Path.Combine(_root, "logs");
        var diagnostics = Path.Combine(_root, "diagnostics");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "peeronq-test.log"),
            "device=407-532-373-464 ip=192.168.1.25 ipv6=2001:db8::8a2e:370:7334 " +
            "password=hunter2 token=abc123 jwt=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJzdWIiOiIxMjM0NTY3ODkwMTIzNDU2Nzg5MCJ9.signature_value_with_length C:\\Users\\Alice\\secret.txt");
        var service = new DiagnosticBundleService(diagnostics, logs);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Request(consent: false)));
        var result = await service.CreateAsync(Request(consent: true));

        Assert.True(File.Exists(result.FilePath));
        using var archive = ZipFile.OpenRead(result.FilePath);
        Assert.Equal(["logs/log-1.jsonl", "manifest.json"], archive.Entries.Select(item => item.FullName).Order().ToArray());
        using var reader = new StreamReader(archive.GetEntry("logs/log-1.jsonl")!.Open());
        var content = await reader.ReadToEndAsync();
        Assert.DoesNotContain("407-532-373-464", content, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.25", content, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", content, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", content, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eyJhbGci", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("407-***-***-464", content, StringComparison.Ordinal);

        using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        var manifest = await manifestReader.ReadToEndAsync();
        Assert.Contains("session.request.created", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("407-532-373-464", manifest, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(manifest);
        var webRtc = document.RootElement.GetProperty("webRtcStatistics");
        Assert.Equal(72, webRtc.GetProperty("captureToPresentP95Ms").GetDouble());
        Assert.Equal(31, webRtc.GetProperty("inputToInjectionP95Ms").GetDouble());
        Assert.Equal(1, webRtc.GetProperty("inputDataLaneReady").GetDouble());
        Assert.Equal(160_000, webRtc.GetProperty("nativeBulkGoodputKbps").GetDouble());
        Assert.False(webRtc.TryGetProperty("privateAddress", out _));
    }

    [Theory]
    [InlineData("LNK-407-532-373-464", "407-***-***-464")]
    [InlineData("407-532-373-464", "407-***-***-464")]
    public void Logger_masker_handles_legacy_and_visible_ids(string input, string expected)
    {
        Assert.Equal(expected, PeerOnQIdMaskingEnricher.Mask(input));
    }

    [Fact]
    public void Structured_client_log_has_utc_service_version_fields_and_masks_device_id()
    {
        var logs = Path.Combine(_root, "structured-logs");
        var logger = PeerOnQLogging.CreateSerilogLogger(logs, "0.6.0", "Staging", "eu-test-1");
        try
        {
            logger.Information("Device {DeviceId} connected", "407-532-373-464");
        }
        finally
        {
            (logger as IDisposable)?.Dispose();
        }

        var line = File.ReadAllText(Assert.Single(Directory.EnumerateFiles(logs, "peeronq-*.log")));
        Assert.Contains("TimestampUtc", line, StringComparison.Ordinal);
        Assert.Contains("PeerOnQ.Desktop", line, StringComparison.Ordinal);
        Assert.Contains("0.6.0", line, StringComparison.Ordinal);
        Assert.Contains("eu-test-1", line, StringComparison.Ordinal);
        Assert.Contains("407-***-***-464", line, StringComparison.Ordinal);
        Assert.DoesNotContain("407-532-373-464", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_doctor_is_bounded_address_free_and_marks_session_scoped_probes_honestly()
    {
        using var transport = new SuccessfulNetworkDoctorTransport();
        using var doctor = new NetworkDoctorService(
            new NetworkDoctorOptions
            {
                SignalingUri = new Uri("wss://signal.example.test/ws"),
                ApiBaseUri = new Uri("https://api.example.test/"),
                Region = "eu-test-1",
                ProbeTimeout = TimeSpan.FromSeconds(1),
            },
            () => SignalingConnectionState.Registered,
            () => TimeSpan.FromMilliseconds(250),
            transport);

        var report = await doctor.RunAsync();

        Assert.Equal(NetworkProbeStatus.Available, report.Probes.Single(item => item.Code == "internet").Status);
        Assert.Equal(NetworkProbeStatus.Available, report.Probes.Single(item => item.Code == "protocol").Status);
        Assert.Equal(NetworkProbeStatus.NotConfigured, report.Probes.Single(item => item.Code == "turn-udp").Status);
        Assert.Equal(NetworkProbeStatus.NotConfigured, report.Probes.Single(item => item.Code == "packet-loss").Status);
        Assert.DoesNotContain("signal.example.test", report.ToUserSummary(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("203.0.113", report.ToSanitizedTechnicalJson(), StringComparison.OrdinalIgnoreCase);
    }

    private static DiagnosticBundleRequest Request(bool consent) => new()
    {
        UserConsented = consent,
        AppVersion = "0.6.0",
        UpdateStatus = "current",
        DatabaseSchemaVersion = 3,
        ConnectionFailureCodes = ["turn_unavailable"],
        WebRtcStatistics = new Dictionary<string, double>
        {
            ["rttMs"] = 42,
            ["captureToPresentP95Ms"] = 72,
            ["inputToInjectionP95Ms"] = 31,
            ["inputDataLaneReady"] = 1,
            ["nativeBulkGoodputKbps"] = 160_000,
            ["privateAddress"] = 1,
        },
        HealthChecks = new Dictionary<string, string> { ["database"] = "healthy" },
        SessionTimeline =
        [
            new SessionTimelineEntry(
                DateTimeOffset.UtcNow,
                "session.request.created",
                "Session request created 407-532-373-464"),
        ],
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SuccessfulNetworkDoctorTransport : INetworkDoctorTransport
    {
        public bool IsNetworkAvailable() => true;
        public bool CanBindUdp() => true;
        public Task ProbeDnsAsync(string host, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<double> ProbeTcpAsync(string host, int port, CancellationToken cancellationToken) => Task.FromResult(18d);
        public Task ProbeHttpAsync(Uri endpoint, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}
