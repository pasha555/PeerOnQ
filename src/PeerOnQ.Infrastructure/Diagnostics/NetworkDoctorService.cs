using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
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
    /// <summary>The check exists but needs something this standalone run does not have (a session).</summary>
    NotTested = 4,
}

/// <param name="DisplayStatus">Short user-facing status that replaces the generic status word.</param>
public sealed record NetworkProbeResult(
    string Code,
    string Label,
    NetworkProbeStatus Status,
    string Summary,
    double? DurationMs = null,
    string? TechnicalCode = null,
    string? DisplayStatus = null);

public sealed record NetworkDoctorReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Region,
    IReadOnlyList<NetworkProbeResult> Probes)
{
    public string ToUserSummary() => string.Join(
        Environment.NewLine,
        Probes.Select(probe =>
            $"{probe.Label,-18} {probe.DisplayStatus ?? StatusText(probe.Status)}{LatencyText(probe.DurationMs)}"));

    public string ToSanitizedTechnicalJson() => JsonSerializer.Serialize(new
    {
        startedAtUtc = StartedAtUtc,
        completedAtUtc = CompletedAtUtc,
        region = Region,
        probes = Probes.Select(probe => new
        {
            probe.Code,
            status = probe.Status.ToString(),
            probe.DisplayStatus,
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
        NetworkProbeStatus.NotTested => "Not tested (needs a session)",
        _ => "Unknown",
    };

    private static string LatencyText(double? value) =>
        value is > 0 ? string.Create(CultureInfo.InvariantCulture, $" ({value:F0} ms)") : string.Empty;
}

public sealed record NetworkDoctorOptions
{
    public required Uri SignalingUri { get; init; }
    public Uri? ApiBaseUri { get; init; }
    public string Region { get; init; } = "not-configured";

    /// <summary>Deadline of each probe path (name resolution plus the probe itself).</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>STUN/TURN server name; null when this build knows no relay server.</summary>
    public string? RelayHost { get; init; }
    public int RelayUdpPort { get; init; } = 3478;
    public int RelayTcpPort { get; init; } = 3478;
    public int RelayTlsPort { get; init; } = 5349;

    /// <summary>
    /// Production deployments publish signaling as <c>signal.&lt;domain&gt;</c> and coturn as
    /// <c>turn.&lt;domain&gt;</c> (bootstrap-peeronq-production.sh). Any other host is unknown.
    /// </summary>
    public static string? DeriveRelayHost(Uri signalingUri)
    {
        ArgumentNullException.ThrowIfNull(signalingUri);
        const string signalingPrefix = "signal.";
        var host = signalingUri.IdnHost;
        return host.StartsWith(signalingPrefix, StringComparison.OrdinalIgnoreCase)
               && host.Length > signalingPrefix.Length
            ? "turn." + host[signalingPrefix.Length..]
            : null;
    }
}

public interface INetworkDoctorTransport : IDisposable
{
    bool IsNetworkAvailable();
    bool CanBindUdp();

    /// <summary>Name resolution only; timed separately so no other probe includes DNS.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);

    /// <summary>Returns the TCP connect time to an already resolved address, in milliseconds.</summary>
    Task<double> ProbeTcpAsync(IPAddress address, int port, CancellationToken cancellationToken);

    Task ProbeHttpAsync(Uri endpoint, CancellationToken cancellationToken);

    /// <summary>RFC 5389 Binding request over UDP.</summary>
    Task<StunProbeOutcome> ProbeStunAsync(IPEndPoint server, CancellationToken cancellationToken);

    /// <summary>Unauthenticated RFC 5766 Allocate; <paramref name="serverName"/> is the TLS name.</summary>
    Task<StunProbeOutcome> ProbeRelayAsync(
        IPEndPoint server,
        RelayProbeTransport transport,
        string serverName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs bounded, deterministic connectivity checks without exposing resolved addresses or raw
/// exception messages. Names are resolved first and timed as DNS; every latency shown afterwards
/// is measured against the resolved address. STUN and the relay are probed for real, but no
/// TURN credential is requested: signaling issues those only for an accepted session, so a relay
/// that answers an unauthenticated Allocate with its 401 challenge is reported as reachable.
/// </summary>
public sealed class NetworkDoctorService(
    NetworkDoctorOptions options,
    Func<SignalingConnectionState> signalingState,
    Func<TimeSpan> serverClockOffset,
    INetworkDoctorTransport? transport = null,
    TimeProvider? timeProvider = null,
    Func<double?>? activeSessionPacketLossPercent = null) : IDisposable
{
    internal const double GoodLatencyMs = 100;
    internal const double HighLatencyMs = 250;

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

        var signalingTask = ProbeSignalingPathAsync(host, port, cancellationToken);
        var apiTask = options.ApiBaseUri is null
            ? Task.FromResult(ProbeOutcome.Unconfigured())
            : RunBoundedAsync(
                async token =>
                {
                    await _transport.ProbeHttpAsync(options.ApiBaseUri, token);
                    return (double?)null;
                },
                options.ProbeTimeout,
                cancellationToken);
        var relayTask = ProbeRelayPathAsync(cancellationToken);

        await Task.WhenAll(signalingTask, apiTask, relayTask);
        var (dns, tcp) = await signalingTask;
        var api = await apiTask;
        var relay = await relayTask;
        var state = signalingState();
        var clockOffset = serverClockOffset();
        var internetAvailable = networkAvailable && dns.Success && tcp.Success;

        List<NetworkProbeResult> probes =
        [
            Result("internet", "Internet", internetAvailable, "Local interface, DNS and signaling TCP reachability"),
            Outcome("dns", "DNS", dns, "Signaling hostname resolved (resolution time only)"),
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
                    ? "Authenticated protocol registration is active; time is the TCP connect to the resolved address"
                    : "No authenticated registration is currently active",
                tcp.DurationMs,
                $"POQ-SIGNALING-{state.ToString().ToUpperInvariant()}"),
            Result("udp", "UDP", udpAvailable, "A local UDP socket can be created"),
            Result("direct-path", "Direct Path", udpAvailable, "Local UDP prerequisite for ICE direct candidates"),
            .. relay,
            Latency(tcp),
            PacketLoss(),
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
        ];

        return new NetworkDoctorReport(started, _time.GetUtcNow(), options.Region, probes);
    }

    public void Dispose() => _transport.Dispose();

    /// <summary>Resolves the signaling name, then times a TCP connect to the resolved address.</summary>
    private async Task<(ProbeOutcome Dns, ProbeOutcome Tcp)> ProbeSignalingPathAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.ProbeTimeout);
        IReadOnlyList<IPAddress> addresses = [];
        var dns = await RunBoundedAsync(
            async token =>
            {
                addresses = await _transport.ResolveAsync(host, token);
                if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
                return (double?)null;
            },
            Timeout.InfiniteTimeSpan,
            deadline.Token,
            cancellationToken);
        if (!dns.Success) return (dns, ProbeOutcome.Unavailable("POQ-NET-DNS-FAILED"));

        var tcp = ProbeOutcome.Unavailable("POQ-NET-TCP-FAILED");
        foreach (var address in OrderForProbing(addresses).Take(3))
        {
            tcp = await RunBoundedAsync(
                async token => await _transport.ProbeTcpAsync(address, port, token),
                Timeout.InfiniteTimeSpan,
                deadline.Token,
                cancellationToken);
            if (tcp.Success || deadline.IsCancellationRequested) break;
        }
        return (dns, tcp);
    }

    private async Task<IReadOnlyList<NetworkProbeResult>> ProbeRelayPathAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.RelayHost))
        {
            const string summary = "No STUN/TURN server is known to this build";
            return
            [
                RelayNotConfigured("stun", "STUN", summary),
                RelayNotConfigured("turn-udp", "Relay UDP", summary),
                RelayNotConfigured("turn-tcp", "Relay TCP", summary),
                RelayNotConfigured("turn-tls", "Relay TLS", summary),
            ];
        }

        var relayHost = options.RelayHost;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.ProbeTimeout);
        IReadOnlyList<IPAddress> addresses = [];
        var resolution = await RunBoundedAsync(
            async token =>
            {
                addresses = await _transport.ResolveAsync(relayHost, token);
                if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
                return (double?)null;
            },
            Timeout.InfiniteTimeSpan,
            deadline.Token,
            cancellationToken);
        if (!resolution.Success)
        {
            const string summary = "The relay server name did not resolve";
            var code = resolution.TechnicalCode ?? "POQ-NET-RELAY-DNS";
            return
            [
                new("stun", "STUN", NetworkProbeStatus.Unavailable, summary, TechnicalCode: code),
                new("turn-udp", "Relay UDP", NetworkProbeStatus.Unavailable, summary, TechnicalCode: code),
                new("turn-tcp", "Relay TCP", NetworkProbeStatus.Unavailable, summary, TechnicalCode: code),
                new("turn-tls", "Relay TLS", NetworkProbeStatus.Unavailable, summary, TechnicalCode: code),
            ];
        }

        // coturn listens on IPv4 in every shipped deployment; prefer it when both families exist.
        var address = OrderForProbing(addresses).First();
        var stun = RunStunAsync(token =>
            _transport.ProbeStunAsync(new IPEndPoint(address, options.RelayUdpPort), token));
        var udp = RunStunAsync(token => _transport.ProbeRelayAsync(
            new IPEndPoint(address, options.RelayUdpPort), RelayProbeTransport.Udp, relayHost, token));
        var tcp = RunStunAsync(token => _transport.ProbeRelayAsync(
            new IPEndPoint(address, options.RelayTcpPort), RelayProbeTransport.Tcp, relayHost, token));
        var tls = RunStunAsync(token => _transport.ProbeRelayAsync(
            new IPEndPoint(address, options.RelayTlsPort), RelayProbeTransport.Tls, relayHost, token));
        await Task.WhenAll(stun, udp, tcp, tls);
        return
        [
            StunResult(await stun),
            RelayResult("turn-udp", "Relay UDP", "UDP", options.RelayUdpPort, await udp),
            RelayResult("turn-tcp", "Relay TCP", "TCP", options.RelayTcpPort, await tcp),
            RelayResult("turn-tls", "Relay TLS", "TLS", options.RelayTlsPort, await tls),
        ];

        async Task<StunAttempt> RunStunAsync(Func<CancellationToken, Task<StunProbeOutcome>> probe)
        {
            try
            {
                await Task.Yield();
                return new StunAttempt(await probe(deadline.Token), null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new StunAttempt(null, "POQ-NET-TIMEOUT");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new StunAttempt(null, $"POQ-NET-{exception.GetType().Name.ToUpperInvariant()}");
            }
        }
    }

    private sealed record StunAttempt(StunProbeOutcome? Outcome, string? FailureCode);

    private NetworkProbeResult StunResult(StunAttempt attempt)
    {
        if (attempt.Outcome is not { } outcome)
            return new(
                "stun",
                "STUN",
                NetworkProbeStatus.Unavailable,
                attempt.FailureCode == "POQ-NET-TIMEOUT"
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"No STUN answer on UDP {options.RelayUdpPort} within {options.ProbeTimeout.TotalSeconds:0.#} s (UDP may be blocked)")
                    : string.Create(CultureInfo.InvariantCulture, $"STUN request on UDP {options.RelayUdpPort} failed"),
                TechnicalCode: attempt.FailureCode);

        return outcome.Kind switch
        {
            StunProbeResultKind.BindingSucceeded => new(
                "stun", "STUN", NetworkProbeStatus.Available,
                "Binding request answered with a mapped address (address not shown)",
                outcome.RoundTripMs, "POQ-NET-STUN"),
            StunProbeResultKind.ErrorResponse => new(
                "stun", "STUN", NetworkProbeStatus.Degraded,
                string.Create(CultureInfo.InvariantCulture, $"STUN server answered with error {outcome.ErrorCode}"),
                outcome.RoundTripMs, string.Create(CultureInfo.InvariantCulture, $"POQ-NET-STUN-ERROR-{outcome.ErrorCode}"),
                "Error response"),
            _ => new(
                "stun", "STUN", NetworkProbeStatus.Degraded,
                "Something answered on the STUN port, but not with a valid Binding response",
                outcome.RoundTripMs, "POQ-NET-STUN-INVALID", "Unexpected answer"),
        };
    }

    private static NetworkProbeResult RelayResult(
        string code,
        string label,
        string transportName,
        int port,
        StunAttempt attempt)
    {
        if (attempt.Outcome is not { } outcome)
        {
            var summary = attempt.FailureCode switch
            {
                "POQ-NET-TIMEOUT" => $"No relay answer on {transportName} {port} before the deadline",
                "POQ-NET-AUTHENTICATIONEXCEPTION" => $"TLS handshake or certificate check failed on {port}",
                "POQ-NET-SOCKETEXCEPTION" => $"{transportName} {port} refused or unreachable",
                _ => $"Relay request on {transportName} {port} failed",
            };
            return new(code, label, NetworkProbeStatus.Unavailable, summary, TechnicalCode: attempt.FailureCode);
        }

        return outcome.Kind switch
        {
            StunProbeResultKind.AuthenticationRequired => new(
                code, label, NetworkProbeStatus.Available,
                string.Create(CultureInfo.InvariantCulture,
                    $"Relay answered an allocation request on {transportName} {port} with its authentication challenge; a real allocation uses the short-lived credentials signaling issues only for an accepted session"),
                outcome.RoundTripMs, $"POQ-NET-{code.ToUpperInvariant()}-AUTH-REQUIRED",
                "Reachable (allocation needs session credentials)"),
            StunProbeResultKind.AllocationSucceeded => new(
                code, label, NetworkProbeStatus.Available,
                string.Create(CultureInfo.InvariantCulture,
                    $"Relay allocated on {transportName} {port} without credentials; the allocation was released"),
                outcome.RoundTripMs, $"POQ-NET-{code.ToUpperInvariant()}-ALLOCATED"),
            StunProbeResultKind.ErrorResponse => new(
                code, label, NetworkProbeStatus.Degraded,
                string.Create(CultureInfo.InvariantCulture,
                    $"Relay answered on {transportName} {port} with error {outcome.ErrorCode}"),
                outcome.RoundTripMs,
                string.Create(CultureInfo.InvariantCulture, $"POQ-NET-{code.ToUpperInvariant()}-ERROR-{outcome.ErrorCode}"),
                "Error response"),
            _ => new(
                code, label, NetworkProbeStatus.Degraded,
                string.Create(CultureInfo.InvariantCulture,
                    $"Something answered on {transportName} {port}, but not with a valid TURN response"),
                outcome.RoundTripMs, $"POQ-NET-{code.ToUpperInvariant()}-INVALID", "Unexpected answer"),
        };
    }

    private static NetworkProbeResult RelayNotConfigured(string code, string label, string summary) => new(
        code,
        label,
        NetworkProbeStatus.NotConfigured,
        summary,
        TechnicalCode: $"POQ-NET-{code.ToUpperInvariant()}-NOT-CONFIGURED");

    /// <summary>IPv4 first (the relay and most signaling deployments are IPv4), then the rest.</summary>
    private static IEnumerable<IPAddress> OrderForProbing(IReadOnlyList<IPAddress> addresses) =>
        addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);

    private NetworkProbeResult PacketLoss()
    {
        double? loss;
        try { loss = activeSessionPacketLossPercent?.Invoke(); }
        catch { loss = null; }

        if (loss is not { } percent || !double.IsFinite(percent) || percent < 0)
            return new(
                "packet-loss",
                "Packet Loss",
                NetworkProbeStatus.NotTested,
                "Packet loss is measured from a live session's media reports",
                TechnicalCode: "POQ-NET-PACKET-LOSS-NO-SESSION",
                DisplayStatus: "Not measured (no active session)");

        var status = percent < 2 ? NetworkProbeStatus.Available : NetworkProbeStatus.Degraded;
        return new(
            "packet-loss",
            "Packet Loss",
            status,
            "Loss reported by the peer for the active session",
            TechnicalCode: "POQ-NET-PACKET-LOSS",
            DisplayStatus: string.Create(CultureInfo.InvariantCulture, $"{percent:0.#}% (active session)"));
    }

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

    /// <summary>
    /// A measured value is never "Unavailable": a slow link is reported as High with its value.
    /// Unavailable means the connect failed.
    /// </summary>
    internal static NetworkProbeResult Latency(ProbeOutcome tcp)
    {
        if (!tcp.Success || tcp.DurationMs is not { } milliseconds)
            return new("latency", "Latency", NetworkProbeStatus.Unavailable, "TCP latency could not be measured", TechnicalCode: tcp.TechnicalCode);

        var (status, display) = milliseconds switch
        {
            <= GoodLatencyMs => (NetworkProbeStatus.Available, (string?)null),
            <= HighLatencyMs => (NetworkProbeStatus.Degraded, "Limited"),
            _ => (NetworkProbeStatus.Degraded, "High"),
        };
        return new(
            "latency",
            "Latency",
            status,
            "TCP connect time to the resolved signaling address (DNS excluded)",
            milliseconds,
            "POQ-NET-LATENCY",
            display);
    }

    private static NetworkProbeResult ClockSkew(TimeSpan offset, SignalingConnectionState state)
    {
        if (state != SignalingConnectionState.Registered)
            return new(
                "clock-skew",
                "Clock Skew",
                NetworkProbeStatus.NotTested,
                "Requires a current authenticated server time sample",
                TechnicalCode: "POQ-NET-CLOCK-SKEW-REGISTRATION-REQUIRED",
                DisplayStatus: "Not tested (needs signaling registration)");

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

    private static Task<ProbeOutcome> RunBoundedAsync(
        Func<CancellationToken, Task<double?>> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunBoundedAsync(probe, timeout, cancellationToken, cancellationToken);

    /// <summary>
    /// Runs one probe under <paramref name="deadlineToken"/> (plus <paramref name="timeout"/>).
    /// The duration is the probe's own measurement when it returns one (a connect time), otherwise
    /// the elapsed time of this call. Only the caller's own cancellation escapes.
    /// </summary>
    private static async Task<ProbeOutcome> RunBoundedAsync(
        Func<CancellationToken, Task<double?>> probe,
        TimeSpan timeout,
        CancellationToken deadlineToken,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken);
        if (timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(timeout);
        try
        {
            var measured = await probe(deadline.Token);
            return ProbeOutcome.Available(measured ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProbeOutcome.Unavailable("POQ-NET-TIMEOUT");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ProbeOutcome.Unavailable($"POQ-NET-{exception.GetType().Name.ToUpperInvariant()}");
        }
    }

    internal sealed record ProbeOutcome(bool Success, bool NotConfigured, double? DurationMs, string? TechnicalCode)
    {
        public static ProbeOutcome Available(double durationMs) => new(true, false, durationMs, null);
        public static ProbeOutcome Unavailable(string code) => new(false, false, null, code);
        public static ProbeOutcome Unconfigured() => new(false, true, null, "POQ-NET-NOT-CONFIGURED");
    }
}

internal sealed class SystemNetworkDoctorTransport(
    Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
    RemoteCertificateValidationCallback? relayCertificateValidation = null) : INetworkDoctorTransport
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

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        return resolver is not null
            ? await resolver(host, cancellationToken)
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
    }

    public async Task<double> ProbeTcpAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        var started = Stopwatch.GetTimestamp();
        await client.ConnectAsync(address, port, cancellationToken);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    public async Task ProbeHttpAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public Task<StunProbeOutcome> ProbeStunAsync(IPEndPoint server, CancellationToken cancellationToken) =>
        StunTurnProbe.BindAsync(server, cancellationToken);

    public Task<StunProbeOutcome> ProbeRelayAsync(
        IPEndPoint server,
        RelayProbeTransport transport,
        string serverName,
        CancellationToken cancellationToken) =>
        StunTurnProbe.AllocateAsync(server, transport, serverName, relayCertificateValidation, cancellationToken);

    public void Dispose() => _http.Dispose();
}
