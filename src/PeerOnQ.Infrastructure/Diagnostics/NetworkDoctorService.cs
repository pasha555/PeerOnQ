using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Infrastructure.Diagnostics;

public enum NetworkProbeStatus
{
    Available = 0,
    Degraded = 1,
    Unavailable = 2,
    NotConfigured = 3,
}

public sealed record NetworkProbeResult(
    string Code,
    string Label,
    NetworkProbeStatus Status,
    string Summary,
    double? DurationMs = null,
    string? TechnicalCode = null);

public sealed record NetworkDoctorReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Region,
    IReadOnlyList<NetworkProbeResult> Probes)
{
    public string ToUserSummary() => string.Join(
        Environment.NewLine,
        Probes.Select(probe => $"{probe.Label,-18} {StatusText(probe.Status)}{LatencyText(probe.DurationMs)}"));

    public string ToSanitizedTechnicalJson() => JsonSerializer.Serialize(new
    {
        startedAtUtc = StartedAtUtc,
        completedAtUtc = CompletedAtUtc,
        region = Region,
        probes = Probes.Select(probe => new
        {
            probe.Code,
            status = probe.Status.ToString(),
            probe.Summary,
            probe.DurationMs,
            probe.TechnicalCode,
        }),
    }, new JsonSerializerOptions { WriteIndented = true });

    private static string StatusText(NetworkProbeStatus status) => status switch
    {
        NetworkProbeStatus.Available => "Available",
        NetworkProbeStatus.Degraded => "Limited",
        NetworkProbeStatus.Unavailable => "Unavailable",
        NetworkProbeStatus.NotConfigured => "Not configured",
        _ => "Unknown",
    };

    private static string LatencyText(double? value) => value is > 0 ? $" ({value:F0} ms)" : string.Empty;
}

public sealed record NetworkDoctorOptions
{
    public required Uri SignalingUri { get; init; }
    public Uri? ApiBaseUri { get; init; }
    public string Region { get; init; } = "not-configured";
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

public interface INetworkDoctorTransport : IDisposable
{
    bool IsNetworkAvailable();
    bool CanBindUdp();
    Task ProbeDnsAsync(string host, CancellationToken cancellationToken);
    Task<double> ProbeTcpAsync(string host, int port, CancellationToken cancellationToken);
    Task ProbeHttpAsync(Uri endpoint, CancellationToken cancellationToken);
}

/// <summary>
/// Runs bounded, deterministic connectivity checks without exposing resolved addresses or raw
/// exception messages. Session-scoped STUN/TURN credentials are never requested outside an
/// approved session, so those checks are reported honestly as unavailable to the standalone test.
/// </summary>
public sealed class NetworkDoctorService(
    NetworkDoctorOptions options,
    Func<SignalingConnectionState> signalingState,
    Func<TimeSpan> serverClockOffset,
    INetworkDoctorTransport? transport = null,
    TimeProvider? timeProvider = null) : IDisposable
{
    private readonly INetworkDoctorTransport _transport = transport ?? new SystemNetworkDoctorTransport();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<NetworkDoctorReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var started = _time.GetUtcNow();
        var networkAvailable = SafeBoolean(_transport.IsNetworkAvailable);
        var udpAvailable = SafeBoolean(_transport.CanBindUdp);
        var host = options.SignalingUri.Host;
        var port = options.SignalingUri.IsDefaultPort
            ? options.SignalingUri.Scheme == "wss" ? 443 : 80
            : options.SignalingUri.Port;

        var dnsTask = RunBoundedAsync(
            token => _transport.ProbeDnsAsync(host, token),
            options.ProbeTimeout,
            cancellationToken);
        var tcpTask = RunBoundedAsync(
            token => _transport.ProbeTcpAsync(host, port, token),
            options.ProbeTimeout,
            cancellationToken);
        var apiTask = options.ApiBaseUri is null
            ? Task.FromResult(ProbeOutcome.Unconfigured())
            : RunBoundedAsync(
                token => _transport.ProbeHttpAsync(options.ApiBaseUri, token),
                options.ProbeTimeout,
                cancellationToken);

        await Task.WhenAll(dnsTask, tcpTask, apiTask);
        var dns = await dnsTask;
        var tcp = await tcpTask;
        var api = await apiTask;
        var state = signalingState();
        var clockOffset = serverClockOffset();
        var internetAvailable = networkAvailable && dns.Success && tcp.Success;

        var probes = new List<NetworkProbeResult>
        {
            Result("internet", "Internet", internetAvailable, "Local interface, DNS and signaling TCP reachability"),
            Outcome("dns", "DNS", dns, "Signaling hostname resolved"),
            Outcome("cloud-api", "PeerOnQ Cloud", api, options.ApiBaseUri is null
                ? "No fixed cloud API is configured for this build"
                : "Cloud endpoint answered without inspecting response content"),
            new(
                "signaling",
                "Signaling",
                state == SignalingConnectionState.Registered
                    ? NetworkProbeStatus.Available
                    : tcp.Success ? NetworkProbeStatus.Degraded : NetworkProbeStatus.Unavailable,
                state == SignalingConnectionState.Registered
                    ? "Authenticated protocol registration is active"
                    : "No authenticated registration is currently active",
                tcp.DurationMs,
                $"POQ-SIGNALING-{state.ToString().ToUpperInvariant()}"),
            Result("udp", "UDP", udpAvailable, "A local UDP socket can be created"),
            Result("direct-path", "Direct Path", udpAvailable, "Local UDP prerequisite for ICE direct candidates"),
            NotConfigured("stun", "STUN", "Requires session-scoped ICE configuration"),
            NotConfigured("turn-udp", "Relay UDP", "Requires an approved session and short-lived TURN credentials"),
            NotConfigured("turn-tcp", "Relay TCP", "Requires an approved session and short-lived TURN credentials"),
            NotConfigured("turn-tls", "Relay TLS", "Requires an approved session and short-lived TURN credentials"),
            Latency(tcp),
            NotConfigured("packet-loss", "Packet Loss", "No active media sample is available"),
            new(
                "region",
                "Selected Region",
                string.Equals(options.Region, "not-configured", StringComparison.Ordinal)
                    ? NetworkProbeStatus.NotConfigured
                    : NetworkProbeStatus.Available,
                options.Region,
                TechnicalCode: "POQ-REGION"),
            new(
                "protocol",
                "Protocol",
                state == SignalingConnectionState.Registered
                    ? NetworkProbeStatus.Available
                    : NetworkProbeStatus.Degraded,
                state == SignalingConnectionState.Registered
                    ? "Signaling version and capabilities were accepted"
                    : "Protocol compatibility is confirmed only after registration",
                TechnicalCode: "POQ-PROTOCOL"),
            ClockSkew(clockOffset, state),
        };

        return new NetworkDoctorReport(started, _time.GetUtcNow(), options.Region, probes);
    }

    public void Dispose() => _transport.Dispose();

    private static NetworkProbeResult Result(string code, string label, bool success, string summary) => new(
        code,
        label,
        success ? NetworkProbeStatus.Available : NetworkProbeStatus.Unavailable,
        summary,
        TechnicalCode: $"POQ-NET-{code.ToUpperInvariant()}");

    private static NetworkProbeResult Outcome(
        string code,
        string label,
        ProbeOutcome outcome,
        string summary) => new(
        code,
        label,
        outcome.NotConfigured ? NetworkProbeStatus.NotConfigured
            : outcome.Success ? NetworkProbeStatus.Available : NetworkProbeStatus.Unavailable,
        summary,
        outcome.DurationMs,
        outcome.TechnicalCode ?? $"POQ-NET-{code.ToUpperInvariant()}");

    private static NetworkProbeResult NotConfigured(string code, string label, string summary) => new(
        code,
        label,
        NetworkProbeStatus.NotConfigured,
        summary,
        TechnicalCode: $"POQ-NET-{code.ToUpperInvariant()}-SESSION-REQUIRED");

    private static NetworkProbeResult Latency(ProbeOutcome tcp)
    {
        if (!tcp.Success)
            return new("latency", "Latency", NetworkProbeStatus.Unavailable, "TCP latency could not be measured", TechnicalCode: tcp.TechnicalCode);

        var status = tcp.DurationMs switch
        {
            <= 100 => NetworkProbeStatus.Available,
            <= 250 => NetworkProbeStatus.Degraded,
            _ => NetworkProbeStatus.Unavailable,
        };
        return new("latency", "Latency", status, "Approximate signaling TCP connection time", tcp.DurationMs, "POQ-NET-LATENCY");
    }

    private static NetworkProbeResult ClockSkew(TimeSpan offset, SignalingConnectionState state)
    {
        if (state != SignalingConnectionState.Registered)
            return NotConfigured("clock-skew", "Clock Skew", "Requires a current authenticated server time sample");

        var absolute = offset.Duration();
        var status = absolute <= TimeSpan.FromSeconds(2)
            ? NetworkProbeStatus.Available
            : absolute <= TimeSpan.FromSeconds(30)
                ? NetworkProbeStatus.Degraded
                : NetworkProbeStatus.Unavailable;
        return new(
            "clock-skew",
            "Clock Skew",
            status,
            "Difference from the last authenticated signaling time sample",
            absolute.TotalMilliseconds,
            "POQ-NET-CLOCK-SKEW");
    }

    private static bool SafeBoolean(Func<bool> probe)
    {
        try { return probe(); }
        catch { return false; }
    }

    private static async Task<ProbeOutcome> RunBoundedAsync(
        Func<CancellationToken, Task> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await probe(deadline.Token);
            return ProbeOutcome.Available(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeOutcome.Unavailable("POQ-NET-TIMEOUT");
        }
        catch (Exception exception)
        {
            return ProbeOutcome.Unavailable($"POQ-NET-{exception.GetType().Name.ToUpperInvariant()}");
        }
    }

    private sealed record ProbeOutcome(bool Success, bool NotConfigured, double? DurationMs, string? TechnicalCode)
    {
        public static ProbeOutcome Available(double durationMs) => new(true, false, durationMs, null);
        public static ProbeOutcome Unavailable(string code) => new(false, false, null, code);
        public static ProbeOutcome Unconfigured() => new(false, true, null, "POQ-NET-NOT-CONFIGURED");
    }
}

internal sealed class SystemNetworkDoctorTransport : INetworkDoctorTransport
{
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    });

    public bool IsNetworkAvailable() => NetworkInterface.GetIsNetworkAvailable();

    public bool CanBindUdp()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return socket.IsBound;
    }

    public async Task ProbeDnsAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
    }

    public async Task<double> ProbeTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public async Task ProbeHttpAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public void Dispose() => _http.Dispose();
}
