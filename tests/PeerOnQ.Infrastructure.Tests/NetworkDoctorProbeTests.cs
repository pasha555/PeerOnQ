using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Infrastructure.Diagnostics;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

/// <summary>
/// Network Doctor (0.9.88): real STUN Binding and unauthenticated TURN Allocate probes against
/// loopback fake servers, DNS excluded from every latency, and honest wording for what a
/// standalone run cannot measure.
/// </summary>
public sealed class NetworkDoctorProbeTests
{
    private static readonly byte[] TransactionId = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray();

    [Fact]
    public void Binding_and_allocate_requests_use_rfc5389_framing()
    {
        var binding = StunMessage.CreateBindingRequest(TransactionId);
        Assert.Equal(20, binding.Length);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(binding));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(binding.AsSpan(2)));
        Assert.Equal(StunMessage.MagicCookie, BinaryPrimitives.ReadUInt32BigEndian(binding.AsSpan(4)));
        Assert.Equal(TransactionId, binding[8..20]);

        var allocate = StunMessage.CreateAllocateRequest(TransactionId);
        Assert.Equal(28, allocate.Length);
        Assert.Equal(0x0003, BinaryPrimitives.ReadUInt16BigEndian(allocate));
        Assert.Equal(8, BinaryPrimitives.ReadUInt16BigEndian(allocate.AsSpan(2)));
        Assert.Equal(StunMessage.RequestedTransportAttribute, BinaryPrimitives.ReadUInt16BigEndian(allocate.AsSpan(20)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16BigEndian(allocate.AsSpan(22)));
        Assert.Equal(new byte[] { 17, 0, 0, 0 }, allocate[24..28]);

        Assert.Equal(0x0101, StunMessage.MessageType(StunMessage.BindingMethod, StunMessage.MessageClass.SuccessResponse));
        Assert.Equal(0x0113, StunMessage.MessageType(StunMessage.AllocateMethod, StunMessage.MessageClass.ErrorResponse));
    }

    [Fact]
    public void Response_parser_accepts_only_matching_well_formed_answers()
    {
        var success = BindingSuccess(TransactionId);
        Assert.True(StunMessage.TryParseResponse(success, TransactionId, StunMessage.BindingMethod, out var parsed));
        Assert.Equal(StunResponseKind.Success, parsed.Kind);
        Assert.True(parsed.HasMappedAddress);

        var challenge = Unauthorized(TransactionId, StunMessage.AllocateMethod);
        Assert.True(StunMessage.TryParseResponse(challenge, TransactionId, StunMessage.AllocateMethod, out var error));
        Assert.Equal(StunResponseKind.Error, error.Kind);
        Assert.Equal(401, error.ErrorCode);
        Assert.True(error.HasRealm);
        Assert.True(error.HasNonce);

        // Another transaction, another method, a wrong cookie, truncation and garbage are rejected.
        Assert.False(StunMessage.TryParseResponse(success, new byte[12], StunMessage.BindingMethod, out _));
        Assert.False(StunMessage.TryParseResponse(success, TransactionId, StunMessage.AllocateMethod, out _));
        var wrongCookie = success.ToArray();
        wrongCookie[4] ^= 0xFF;
        Assert.False(StunMessage.TryParseResponse(wrongCookie, TransactionId, StunMessage.BindingMethod, out _));
        Assert.False(StunMessage.TryParseResponse(success[..^4], TransactionId, StunMessage.BindingMethod, out _));
        Assert.False(StunMessage.TryParseResponse(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n"),
            TransactionId, StunMessage.BindingMethod, out _));
        var errorWithoutCode = StunMessage.Create(
            StunMessage.MessageType(StunMessage.AllocateMethod, StunMessage.MessageClass.ErrorResponse), TransactionId, []);
        Assert.False(StunMessage.TryParseResponse(errorWithoutCode, TransactionId, StunMessage.AllocateMethod, out _));
        var requestEcho = StunMessage.CreateBindingRequest(TransactionId);
        Assert.False(StunMessage.TryParseResponse(requestEcho, TransactionId, StunMessage.BindingMethod, out _));
    }

    [Fact]
    public async Task Udp_binding_and_allocate_report_success_and_the_authentication_challenge()
    {
        using var server = new FakeStunUdpServer(FakeBehavior.Correct);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var binding = await StunTurnProbe.BindAsync(server.EndPoint, deadline.Token);
        Assert.Equal(StunProbeResultKind.BindingSucceeded, binding.Kind);
        Assert.InRange(binding.RoundTripMs, 0, 2_000);

        var allocate = await StunTurnProbe.AllocateAsync(
            server.EndPoint, RelayProbeTransport.Udp, "turn.test", null, deadline.Token);
        Assert.Equal(StunProbeResultKind.AuthenticationRequired, allocate.Kind);
        Assert.Equal(401, allocate.ErrorCode);
    }

    [Fact]
    public async Task Udp_garbage_is_an_invalid_answer_and_silence_is_a_timeout()
    {
        using (var garbage = new FakeStunUdpServer(FakeBehavior.Garbage))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(900)))
        {
            var result = await StunTurnProbe.BindAsync(garbage.EndPoint, deadline.Token);
            Assert.Equal(StunProbeResultKind.InvalidResponse, result.Kind);
        }

        using var silent = new FakeStunUdpServer(FakeBehavior.Silent);
        using var shortDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StunTurnProbe.BindAsync(silent.EndPoint, shortDeadline.Token));
    }

    [Fact]
    public async Task Tcp_allocate_reports_the_challenge_and_rejects_garbage()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var server = new FakeStunStreamServer(FakeBehavior.Correct, certificate: null))
        {
            var result = await StunTurnProbe.AllocateAsync(
                server.EndPoint, RelayProbeTransport.Tcp, "turn.test", null, deadline.Token);
            Assert.Equal(StunProbeResultKind.AuthenticationRequired, result.Kind);
        }

        await using var garbage = new FakeStunStreamServer(FakeBehavior.Garbage, certificate: null);
        var invalid = await StunTurnProbe.AllocateAsync(
            garbage.EndPoint, RelayProbeTransport.Tcp, "turn.test", null, deadline.Token);
        Assert.Equal(StunProbeResultKind.InvalidResponse, invalid.Kind);
    }

    [Fact]
    public async Task Tls_allocate_validates_the_certificate_before_the_challenge()
    {
        using var certificate = CreateServerCertificate("turn.test");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var server = new FakeStunStreamServer(FakeBehavior.Correct, certificate))
        {
            var pinned = await StunTurnProbe.AllocateAsync(
                server.EndPoint, RelayProbeTransport.Tls, "turn.test", Pinned(certificate), deadline.Token);
            Assert.Equal(StunProbeResultKind.AuthenticationRequired, pinned.Kind);
        }

        // Normal validation refuses an untrusted self-signed relay certificate.
        await using var untrusted = new FakeStunStreamServer(FakeBehavior.Correct, certificate);
        await Assert.ThrowsAsync<AuthenticationException>(() => StunTurnProbe.AllocateAsync(
            untrusted.EndPoint, RelayProbeTransport.Tls, "turn.test", null, deadline.Token));
    }

    [Fact]
    public async Task Doctor_probes_stun_and_relays_for_real_and_excludes_dns_from_latency()
    {
        using var certificate = CreateServerCertificate("turn.test");
        using var udp = new FakeStunUdpServer(FakeBehavior.Correct);
        await using var tcp = new FakeStunStreamServer(FakeBehavior.Correct, certificate: null);
        await using var tls = new FakeStunStreamServer(FakeBehavior.Correct, certificate);
        var signaling = new TcpListener(IPAddress.Loopback, 0);
        signaling.Start();
        try
        {
            var signalingPort = ((IPEndPoint)signaling.LocalEndpoint).Port;
            const int resolverDelayMs = 500;
            using var transport = new SystemNetworkDoctorTransport(
                async (_, token) =>
                {
                    await Task.Delay(resolverDelayMs, token);
                    return [IPAddress.Loopback];
                },
                Pinned(certificate));
            using var doctor = new NetworkDoctorService(
                new NetworkDoctorOptions
                {
                    SignalingUri = new Uri($"wss://signal.test:{signalingPort}/ws"),
                    Region = "test",
                    ProbeTimeout = TimeSpan.FromSeconds(4),
                    RelayHost = "turn.test",
                    RelayUdpPort = udp.EndPoint.Port,
                    RelayTcpPort = tcp.EndPoint.Port,
                    RelayTlsPort = tls.EndPoint.Port,
                },
                () => SignalingConnectionState.Registered,
                () => TimeSpan.Zero,
                transport);

            var report = await doctor.RunAsync();

            var dns = Probe(report, "dns");
            var latency = Probe(report, "latency");
            Assert.Equal(NetworkProbeStatus.Available, dns.Status);
            Assert.True(dns.DurationMs >= resolverDelayMs - 50, $"DNS took {dns.DurationMs} ms");
            Assert.Equal(NetworkProbeStatus.Available, latency.Status);
            Assert.True(latency.DurationMs < 250, $"Latency {latency.DurationMs} ms must not include the {resolverDelayMs} ms resolver delay");
            Assert.Equal(latency.DurationMs, Probe(report, "signaling").DurationMs);

            Assert.Equal(NetworkProbeStatus.Available, Probe(report, "stun").Status);
            foreach (var code in new[] { "turn-udp", "turn-tcp", "turn-tls" })
            {
                var relay = Probe(report, code);
                Assert.Equal(NetworkProbeStatus.Available, relay.Status);
                Assert.Equal("Reachable (allocation needs session credentials)", relay.DisplayStatus);
                Assert.True(relay.DurationMs < 250, $"{code} RTT {relay.DurationMs} ms includes the resolver delay");
            }

            var summary = report.ToUserSummary();
            Assert.Contains("Relay TLS          Reachable (allocation needs session credentials)", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("127.0.0.1", report.ToSanitizedTechnicalJson(), StringComparison.Ordinal);
        }
        finally
        {
            signaling.Stop();
        }
    }

    [Fact]
    public async Task Doctor_reports_unreachable_relays_honestly()
    {
        using var silent = new FakeStunUdpServer(FakeBehavior.Silent);
        var closedPort = GetClosedTcpPort();
        using var transport = new SystemNetworkDoctorTransport((_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        using var doctor = new NetworkDoctorService(
            new NetworkDoctorOptions
            {
                SignalingUri = new Uri($"wss://signal.test:{closedPort}/ws"),
                ProbeTimeout = TimeSpan.FromMilliseconds(1_500),
                RelayHost = "turn.test",
                RelayUdpPort = silent.EndPoint.Port,
                RelayTcpPort = closedPort,
                RelayTlsPort = closedPort,
            },
            () => SignalingConnectionState.Disconnected,
            () => TimeSpan.Zero,
            transport);

        var report = await doctor.RunAsync();

        Assert.Equal(NetworkProbeStatus.Unavailable, Probe(report, "stun").Status);
        Assert.Equal("POQ-NET-TIMEOUT", Probe(report, "stun").TechnicalCode);
        Assert.Equal(NetworkProbeStatus.Unavailable, Probe(report, "turn-tcp").Status);
        Assert.Equal(NetworkProbeStatus.Unavailable, Probe(report, "turn-tls").Status);
        // A failed connect is the only reason latency is Unavailable.
        Assert.Equal(NetworkProbeStatus.Unavailable, Probe(report, "latency").Status);
        Assert.Null(Probe(report, "latency").DurationMs);
    }

    [Theory]
    [InlineData(40, NetworkProbeStatus.Available, null)]
    [InlineData(150, NetworkProbeStatus.Degraded, "Limited")]
    [InlineData(400, NetworkProbeStatus.Degraded, "High")]
    public void Measured_latency_is_never_unavailable(double milliseconds, NetworkProbeStatus expected, string? display)
    {
        var result = NetworkDoctorService.Latency(NetworkDoctorService.ProbeOutcome.Available(milliseconds));

        Assert.Equal(expected, result.Status);
        Assert.Equal(display, result.DisplayStatus);
        Assert.Equal(milliseconds, result.DurationMs);
        var failed = NetworkDoctorService.Latency(NetworkDoctorService.ProbeOutcome.Unavailable("POQ-NET-TIMEOUT"));
        Assert.Equal(NetworkProbeStatus.Unavailable, failed.Status);
    }

    [Fact]
    public async Task Unprobed_rows_say_why_instead_of_not_configured()
    {
        using var transport = new FixedTransport();
        using var doctor = new NetworkDoctorService(
            new NetworkDoctorOptions { SignalingUri = new Uri("wss://signal.example.test/ws") },
            () => SignalingConnectionState.Disconnected,
            () => TimeSpan.Zero,
            transport);

        var report = await doctor.RunAsync();

        var loss = Probe(report, "packet-loss");
        Assert.Equal(NetworkProbeStatus.NotTested, loss.Status);
        Assert.Equal("Not measured (no active session)", loss.DisplayStatus);
        Assert.Equal("Not tested (needs signaling registration)", Probe(report, "clock-skew").DisplayStatus);
        // Without a known relay host the relay rows remain "Not configured", with the reason.
        Assert.Equal(NetworkProbeStatus.NotConfigured, Probe(report, "stun").Status);
        Assert.Contains("No STUN/TURN server is known", Probe(report, "stun").Summary, StringComparison.Ordinal);
        Assert.Contains("Packet Loss        Not measured (no active session)", report.ToUserSummary(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Packet_loss_comes_from_the_active_session()
    {
        using var transport = new FixedTransport();
        using var doctor = new NetworkDoctorService(
            new NetworkDoctorOptions { SignalingUri = new Uri("wss://signal.example.test/ws") },
            () => SignalingConnectionState.Registered,
            () => TimeSpan.Zero,
            transport,
            activeSessionPacketLossPercent: () => 3.5);

        var loss = Probe(await doctor.RunAsync(), "packet-loss");

        Assert.Equal(NetworkProbeStatus.Degraded, loss.Status);
        Assert.Equal("3.5% (active session)", loss.DisplayStatus);
    }

    [Theory]
    [InlineData("wss://signal.peeronq.example/ws", "turn.peeronq.example")]
    [InlineData("wss://SIGNAL.example.com:8443/", "turn.example.com")]
    [InlineData("wss://10.1.10.1/ws", null)]
    [InlineData("wss://localhost:5001/ws", null)]
    [InlineData("wss://signal./ws", null)]
    public void Relay_host_follows_the_deployment_naming_convention(string signaling, string? expected) =>
        Assert.Equal(expected, NetworkDoctorOptions.DeriveRelayHost(new Uri(signaling)));

    private static NetworkProbeResult Probe(NetworkDoctorReport report, string code) =>
        report.Probes.Single(probe => probe.Code == code);

    private static RemoteCertificateValidationCallback Pinned(X509Certificate2 expected) =>
        (_, certificate, _, _) => certificate is not null
                                  && string.Equals(certificate.GetCertHashString(), expected.GetCertHashString(),
                                      StringComparison.OrdinalIgnoreCase);

    private static int GetClosedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] BindingSuccess(byte[] transactionId)
    {
        var mapped = new byte[8];
        mapped[1] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(mapped.AsSpan(2), (ushort)(50_000 ^ (StunMessage.MagicCookie >> 16)));
        BinaryPrimitives.WriteUInt32BigEndian(mapped.AsSpan(4), 0x7F000001 ^ StunMessage.MagicCookie);
        return StunMessage.Create(
            StunMessage.MessageType(StunMessage.BindingMethod, StunMessage.MessageClass.SuccessResponse),
            transactionId,
            [(StunMessage.XorMappedAddressAttribute, mapped)]);
    }

    private static byte[] Unauthorized(byte[] transactionId, ushort method)
    {
        var reason = Encoding.ASCII.GetBytes("Unauthorized");
        var error = new byte[4 + reason.Length];
        error[2] = 4;
        error[3] = 1;
        reason.CopyTo(error, 4);
        return StunMessage.Create(
            StunMessage.MessageType(method, StunMessage.MessageClass.ErrorResponse),
            transactionId,
            [
                (StunMessage.ErrorCodeAttribute, error),
                (StunMessage.RealmAttribute, Encoding.ASCII.GetBytes("turn.test")),
                (StunMessage.NonceAttribute, Encoding.ASCII.GetBytes("abcdef0123456789")),
            ]);
    }

    /// <summary>Answers like coturn: Binding success, Allocate 401 with REALM and NONCE.</summary>
    private static byte[]? Answer(ReadOnlySpan<byte> request, FakeBehavior behavior)
    {
        if (behavior == FakeBehavior.Silent || request.Length < 20) return null;
        if (behavior == FakeBehavior.Garbage) return Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n");
        var transactionId = request.Slice(8, 12).ToArray();
        var type = BinaryPrimitives.ReadUInt16BigEndian(request);
        return type switch
        {
            0x0001 => BindingSuccess(transactionId),
            0x0003 => Unauthorized(transactionId, StunMessage.AllocateMethod),
            _ => null,
        };
    }

    private static X509Certificate2 CreateServerCertificate(string dnsName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private enum FakeBehavior
    {
        Correct,
        Garbage,
        Silent,
    }

    private sealed class FakeStunUdpServer : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource _stop = new();

        public FakeStunUdpServer(FakeBehavior behavior)
        {
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            _ = RunAsync(behavior);
        }

        public IPEndPoint EndPoint { get; }

        private async Task RunAsync(FakeBehavior behavior)
        {
            var buffer = new byte[1500];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var received = await _socket.ReceiveFromAsync(
                        buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                    if (Answer(buffer.AsSpan(0, received.ReceivedBytes), behavior) is { } answer)
                        await _socket.SendToAsync(answer, SocketFlags.None, received.RemoteEndPoint, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Test server shutdown.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class FakeStunStreamServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeStunStreamServer(FakeBehavior behavior, X509Certificate2? certificate)
        {
            _listener.Start();
            EndPoint = (IPEndPoint)_listener.LocalEndpoint;
            _loop = RunAsync(behavior, certificate);
        }

        public IPEndPoint EndPoint { get; }

        private async Task RunAsync(FakeBehavior behavior, X509Certificate2? certificate)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = ServeAsync(client, behavior, certificate);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Test server shutdown.
            }
        }

        private async Task ServeAsync(TcpClient client, FakeBehavior behavior, X509Certificate2? certificate)
        {
            using (client)
            {
                try
                {
                    Stream stream = client.GetStream();
                    if (certificate is not null)
                    {
                        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                        await tls.AuthenticateAsServerAsync(certificate);
                        stream = tls;
                    }

                    await using (stream)
                    {
                        var header = new byte[20];
                        await stream.ReadExactlyAsync(header, _stop.Token);
                        var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2))];
                        await stream.ReadExactlyAsync(body, _stop.Token);
                        if (Answer([.. header, .. body], behavior) is { } answer)
                            await stream.WriteAsync(answer, _stop.Token);
                        // Keep the connection until the client has read the answer.
                        await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException
                                               or AuthenticationException or ObjectDisposedException)
                {
                    // The client closed, refused the certificate, or the test ended.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _loop;
            _stop.Dispose();
        }
    }

    private sealed class FixedTransport : INetworkDoctorTransport
    {
        public bool IsNetworkAvailable() => true;
        public bool CanBindUdp() => true;
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("203.0.113.7")]);
        public Task<double> ProbeTcpAsync(IPAddress address, int port, CancellationToken cancellationToken) =>
            Task.FromResult(12d);
        public Task ProbeHttpAsync(Uri endpoint, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StunProbeOutcome> ProbeStunAsync(IPEndPoint server, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No relay host is configured in these tests.");
        public Task<StunProbeOutcome> ProbeRelayAsync(
            IPEndPoint server, RelayProbeTransport transport, string serverName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No relay host is configured in these tests.");
        public void Dispose() { }
    }
}
