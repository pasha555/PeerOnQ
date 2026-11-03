using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Nat;
using SIPSorcery.Net;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class RouterPortMappingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("93.184.216.34");
    // The fake gateways answer on thread-pool loops; 600 ms let a loaded CI runner miss the SSDP
    // reply, so no AddPortMapping was ever sent. Tests that expect a reply return as soon as it arrives.
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task Nat_pmp_maps_renews_and_deletes_a_leased_port()
    {
        await using var gateway = new FakeNatPmpGateway(PublicAddress);
        var mapper = new RouterPortMapper(_ => IPAddress.Loopback, gateway.Port, UnusedUdpPort(), false, ReplyTimeout);

        var mapping = await mapper.MapUdpAsync(IPAddress.Loopback, 50_000, CancellationToken.None);

        Assert.NotNull(mapping);
        Assert.Equal(RouterPortMappingMethod.NatPmp, mapping.Method);
        Assert.Equal(PublicAddress, mapping.ExternalAddress);
        Assert.Equal(50_000, mapping.ExternalPort);
        Assert.True(await mapper.RenewAsync(mapping, CancellationToken.None));
        await mapper.UnmapAsync(mapping, CancellationToken.None);
        Assert.Equal(
            new[] { (50_000, 50_000, 3600u), (50_000, 50_000, 3600u), (50_000, 0, 0u) },
            gateway.MapRequests.ToArray());
    }

    [Fact]
    public async Task Behind_a_second_nat_nothing_is_mapped()
    {
        // The 2026-10-27 test PC: its router's own WAN address is 192.168.100.x (double NAT).
        await using var gateway = new FakeNatPmpGateway(IPAddress.Parse("192.168.100.7"));
        var mapper = new RouterPortMapper(_ => IPAddress.Loopback, gateway.Port, UnusedUdpPort(), false, ReplyTimeout);

        Assert.Null(await mapper.MapUdpAsync(IPAddress.Loopback, 50_000, CancellationToken.None));
        Assert.Empty(gateway.MapRequests);
        Assert.Contains("192.168.100.7 is not public", mapper.LastFailure(IPAddress.Loopback), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upnp_moves_to_another_port_on_a_conflict_and_deletes_the_mapping_on_release()
    {
        await using var router = new FakeUpnpGateway(PublicAddress, addPortMappingErrors: [718]);
        var mapper = router.CreateMapper();

        var mapping = await mapper.MapUdpAsync(IPAddress.Loopback, 50_001, CancellationToken.None);

        Assert.NotNull(mapping);
        Assert.Equal(RouterPortMappingMethod.Upnp, mapping.Method);
        Assert.Equal(PublicAddress, mapping.ExternalAddress);
        Assert.NotEqual(50_001, mapping.ExternalPort);
        var adds = router.Requests.Where(request => request.Action == "AddPortMapping").ToList();
        Assert.Equal(2, adds.Count);
        Assert.Contains("<NewExternalPort>50001</NewExternalPort>", adds[0].Body, StringComparison.Ordinal);
        Assert.All(adds, add =>
        {
            Assert.Contains("<NewProtocol>UDP</NewProtocol>", add.Body, StringComparison.Ordinal);
            Assert.Contains("<NewInternalPort>50001</NewInternalPort>", add.Body, StringComparison.Ordinal);
            Assert.Contains("<NewInternalClient>127.0.0.1</NewInternalClient>", add.Body, StringComparison.Ordinal);
            Assert.Contains("<NewLeaseDuration>3600</NewLeaseDuration>", add.Body, StringComparison.Ordinal);
        });

        await mapper.UnmapAsync(mapping, CancellationToken.None);
        var delete = Assert.Single(router.Requests, request => request.Action == "DeletePortMapping");
        Assert.Contains($"<NewExternalPort>{mapping.ExternalPort}</NewExternalPort>", delete.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_router_offering_only_permanent_mappings_is_not_used()
    {
        await using var router = new FakeUpnpGateway(PublicAddress, addPortMappingErrors: [725]);
        var mapper = router.CreateMapper();

        Assert.Null(await mapper.MapUdpAsync(IPAddress.Loopback, 50_002, CancellationToken.None));
        Assert.Single(router.Requests, request => request.Action == "AddPortMapping");
        Assert.Contains("only permanent", mapper.LastFailure(IPAddress.Loopback), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upnp_behind_a_second_nat_adds_no_mapping()
    {
        await using var router = new FakeUpnpGateway(IPAddress.Parse("100.72.1.9"));
        var mapper = router.CreateMapper();

        Assert.Null(await mapper.MapUdpAsync(IPAddress.Loopback, 50_003, CancellationToken.None));
        Assert.DoesNotContain(router.Requests, request => request.Action == "AddPortMapping");
        Assert.Contains("CGNAT", mapper.LastFailure(IPAddress.Loopback), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gateway_without_either_protocol_is_reported_as_such()
    {
        var mapper = new RouterPortMapper(_ => IPAddress.Loopback, UnusedUdpPort(), UnusedUdpPort(), false, ReplyTimeout);

        Assert.Null(await mapper.MapUdpAsync(IPAddress.Loopback, 50_006, CancellationToken.None));
        Assert.Contains("neither NAT-PMP nor UPnP", mapper.LastFailure(IPAddress.Loopback), StringComparison.Ordinal);
        Assert.Null(await new RouterPortMapper(_ => null, 1, 1, false, ReplyTimeout)
            .MapUdpAsync(IPAddress.Loopback, 50_006, CancellationToken.None));
    }

    /// <summary>Asks this machine's real router; it adds no mapping unless the router has a public address.</summary>
    [RouterFact]
    public async Task The_real_default_gateway_maps_or_explains_why_not()
    {
        var local = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                              && !PeerOnQ.Media.Pipeline.IceCandidatePolicy.IsVirtualAdapter(adapter.Name, adapter.Description))
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address))
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork
                                       && RouterPortMapper.DefaultGatewayOf(address) is not null);
        Assert.NotNull(local);
        using var socket = new UdpClient(new IPEndPoint(local, 0));
        var port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        var mapper = new RouterPortMapper();
        var started = System.Diagnostics.Stopwatch.StartNew();

        var mapping = await mapper.MapUdpAsync(local, port, CancellationToken.None);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(8), $"Mapping took {started.Elapsed}.");
        output.WriteLine($"gateway={RouterPortMapper.DefaultGatewayOf(local)} after {started.Elapsed.TotalMilliseconds:0} ms: "
            + (mapping is null ? $"no mapping: {mapper.LastFailure(local)}" : $"{mapping.Method} external port {mapping.ExternalPort}"));
        if (mapping is null)
        {
            Assert.NotNull(mapper.LastFailure(local));
            return;
        }
        Assert.True(RouterPortMapper.IsPublic(mapping.ExternalAddress));
        await mapper.UnmapAsync(mapping, CancellationToken.None);
    }

    [Fact]
    public async Task A_location_that_is_not_the_gateway_is_never_contacted()
    {
        await using var router = new FakeUpnpGateway(PublicAddress, locationHost: "127.0.0.2");

        Assert.Null(await router.CreateMapper().MapUdpAsync(IPAddress.Loopback, 50_004, CancellationToken.None));
        Assert.Equal(0, router.HttpConnections);
    }

    [Fact]
    public async Task A_description_with_a_dtd_is_rejected()
    {
        await using var router = new FakeUpnpGateway(PublicAddress, descriptionPrefix: "<!DOCTYPE root [<!ENTITY x \"y\">]>");

        Assert.Null(await router.CreateMapper().MapUdpAsync(IPAddress.Loopback, 50_005, CancellationToken.None));
        Assert.DoesNotContain(router.Requests, request => request.Action == "AddPortMapping");
    }

    [Theory]
    [InlineData("candidate:1 1 udp 2113937151 10.0.0.10 64922 typ host generation 0", true)]
    [InlineData("candidate:1 1 udp 2113937151 192.168.1.20 5000 typ host", true)]
    [InlineData("candidate:1 1 udp 2113937151 127.0.0.1 5000 typ host", false)]
    [InlineData("candidate:1 1 udp 2113937151 169.254.3.4 5000 typ host", false)]
    [InlineData("candidate:1 1 udp 2113937151 93.184.216.34 5000 typ host", false)]
    [InlineData("candidate:1 1 udp 1694498815 37.114.146.9 3056 typ srflx raddr 10.0.0.10 rport 60619", false)]
    [InlineData("candidate:1 1 tcp 2113937151 10.0.0.10 5000 typ host", false)]
    [InlineData("candidate:1 1 udp 2113937151 fd00::5 5000 typ host", false)]
    public void Only_udp_host_candidates_on_a_nated_ipv4_interface_are_mapped(string candidate, bool mapped) =>
        Assert.Equal(mapped, WebRtcMediaSession.TryParseNatedHost(candidate, out _, out _));

    [Fact]
    public void The_mapped_candidate_is_a_server_reflexive_candidate_the_peer_parses()
    {
        var mapping = new RouterPortMapping(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.10"), 64922,
            PublicAddress, 64922, RouterPortMappingMethod.Upnp);

        var parsed = RTCIceCandidate.Parse(SessionPortMappings.Candidate(mapping));

        Assert.Equal(RTCIceCandidateType.srflx, parsed.type);
        Assert.Equal(PublicAddress.ToString(), parsed.address);
        Assert.Equal(64922, parsed.port);
        Assert.Equal("10.0.0.10", parsed.relatedAddress);
        Assert.Equal(SessionPortMappings.MappedCandidatePriority, parsed.priority);
    }

    [Fact]
    public async Task Session_mappings_are_released_including_one_that_completes_after_the_close()
    {
        var mapper = new FakeMapper();
        var advertised = new ConcurrentBag<int>();
        var mappings = new SessionPortMappings(mapper);
        mappings.Request(IPAddress.Parse("10.0.0.10"), 5000, mapping => advertised.Add(mapping.LocalPort));
        mappings.Request(IPAddress.Parse("10.0.0.10"), 5000, mapping => advertised.Add(mapping.LocalPort));
        await WaitUntilAsync(() => advertised.Count == 1);

        mapper.Hold.Reset();
        mappings.Request(IPAddress.Parse("10.0.0.10"), 5001, mapping => advertised.Add(mapping.LocalPort));
        await WaitUntilAsync(() => mapper.Mapped.Count == 2);
        mappings.Dispose();
        mapper.Hold.Set();

        await WaitUntilAsync(() => mapper.Unmapped.Count == 2);
        Assert.Equal(new[] { 5000, 5001 }, mapper.Unmapped.OrderBy(port => port));
        Assert.Equal(new[] { 5000 }, advertised);
        Assert.Equal(1, mapper.MapCalls(5000));
    }

    [Fact]
    public async Task A_connected_session_advertises_its_mapped_ports_and_releases_them_on_close()
    {
        var sessionId = SessionId.New();
        var permissions = SessionPermission.ViewScreen | SessionPermission.ControlInput;
        await using var capture = new SyntheticCaptureSource();
        var mapper = new FakeMapper();
        var sharer = WebRtcMediaSession.CreateSharer(sessionId, capture, MediaProfile.Conservative, permissions: permissions);
        await using var viewer = WebRtcMediaSession.CreateViewer(sessionId, permissions: permissions);
        sharer.EnableRouterPortMapping(mapper);
        var mappedCandidates = new ConcurrentBag<string>();
        sharer.LocalIceCandidate += async (_, candidate) =>
        {
            if (candidate.Candidate.Contains("pnqmap", StringComparison.Ordinal)) mappedCandidates.Add(candidate.Candidate);
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        };
        viewer.LocalIceCandidate += async (_, candidate) =>
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);

        var offer = await sharer.CreateOfferAsync();
        var answer = await viewer.CreateAnswerAsync(offer);
        await sharer.ApplyRemoteAnswerAsync(answer);
        await WaitUntilAsync(() => sharer.State == MediaConnectionState.Connected);
        await WaitUntilAsync(() => !mappedCandidates.IsEmpty,
            "No private IPv4 host candidate was gathered on this machine to map.");

        Assert.All(mappedCandidates, candidate =>
        {
            Assert.Equal(RTCIceCandidateType.srflx, RTCIceCandidate.Parse(candidate).type);
            Assert.Equal(PublicAddress.ToString(), RTCIceCandidate.Parse(candidate).address);
        });
        // Each ICE socket (primary and the separate input lane) is mapped once.
        Assert.True(mapper.Mapped.Count >= 2, $"{mapper.Mapped.Count} ports mapped.");
        Assert.Equal(mapper.Mapped.Count, mapper.Mapped.Distinct().Count());

        await sharer.DisposeAsync();
        await WaitUntilAsync(() => mapper.Unmapped.Count == mapper.Mapped.Count);
    }

    private static int UnusedUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string? message = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, message ?? "The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private sealed class FakeMapper : IRouterPortMapper
    {
        private readonly ConcurrentDictionary<int, int> _calls = new();
        public ManualResetEventSlim Hold { get; } = new(true);
        public ConcurrentQueue<int> Mapped { get; } = new();
        public ConcurrentQueue<int> Unmapped { get; } = new();

        public int MapCalls(int port) => _calls.GetValueOrDefault(port);

        public Task<RouterPortMapping?> MapUdpAsync(IPAddress localAddress, int localPort, CancellationToken cancellationToken)
        {
            _calls.AddOrUpdate(localPort, 1, (_, count) => count + 1);
            Mapped.Enqueue(localPort);
            // Simulates a router that answers after the session already closed.
            Assert.True(Hold.Wait(TimeSpan.FromSeconds(10)));
            return Task.FromResult<RouterPortMapping?>(new RouterPortMapping(IPAddress.Parse("10.0.0.1"), localAddress,
                localPort, PublicAddress, localPort, RouterPortMappingMethod.Upnp));
        }

        public Task<bool> RenewAsync(RouterPortMapping mapping, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task UnmapAsync(RouterPortMapping mapping, CancellationToken cancellationToken)
        {
            Unmapped.Enqueue(mapping.LocalPort);
            return Task.CompletedTask;
        }

        public string? LastFailure(IPAddress localAddress) => null;
    }

    private sealed class FakeNatPmpGateway : IAsyncDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeNatPmpGateway(IPAddress external) => _loop = Task.Run(() => ServeAsync(external));

        public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        public ConcurrentQueue<(int Internal, int SuggestedExternal, uint Lifetime)> MapRequests { get; } = new();

        private async Task ServeAsync(IPAddress external)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var received = await _socket.ReceiveAsync(_stop.Token);
                    var request = received.Buffer;
                    byte[] reply;
                    if (request is [0, 0])
                    {
                        reply = new byte[12];
                        reply[1] = 128;
                        external.GetAddressBytes().CopyTo(reply, 8);
                    }
                    else if (request.Length == 12 && request[1] == 1)
                    {
                        var internalPort = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4));
                        var suggested = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6));
                        var lifetime = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(8));
                        MapRequests.Enqueue((internalPort, suggested, lifetime));
                        reply = new byte[16];
                        reply[1] = 129;
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), internalPort);
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), lifetime == 0 ? (ushort)0 : suggested);
                        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), lifetime);
                    }
                    else
                    {
                        continue;
                    }

                    await _socket.SendAsync(reply, received.RemoteEndPoint);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _socket.Dispose();
            await _loop;
        }
    }

    /// <summary>An SSDP responder and a minimal HTTP/SOAP IGD on loopback, the "gateway" 127.0.0.1.</summary>
    private sealed class FakeUpnpGateway : IAsyncDisposable
    {
        private readonly UdpClient _ssdp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly TcpListener _http = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _ssdpLoop;
        private readonly Task _httpLoop;
        private readonly IPAddress _external;
        private readonly Queue<int> _addErrors;
        private readonly string _locationHost;
        private readonly string _descriptionPrefix;
        private int _httpConnections;

        public FakeUpnpGateway(IPAddress external, int[]? addPortMappingErrors = null, string locationHost = "127.0.0.1",
            string descriptionPrefix = "")
        {
            _external = external;
            _addErrors = new Queue<int>(addPortMappingErrors ?? []);
            _locationHost = locationHost;
            _descriptionPrefix = descriptionPrefix;
            _http.Start();
            _ssdpLoop = Task.Run(ServeSsdpAsync);
            _httpLoop = Task.Run(ServeHttpAsync);
        }

        public ConcurrentQueue<(string Action, string Body)> Requests { get; } = new();
        public int HttpConnections => Volatile.Read(ref _httpConnections);
        private int HttpPort => ((IPEndPoint)_http.LocalEndpoint).Port;

        public RouterPortMapper CreateMapper() =>
            new(_ => IPAddress.Loopback, UnusedUdpPort(), ((IPEndPoint)_ssdp.Client.LocalEndPoint!).Port, false, ReplyTimeout);

        private async Task ServeSsdpAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var received = await _ssdp.ReceiveAsync(_stop.Token);
                    if (!Encoding.ASCII.GetString(received.Buffer).StartsWith("M-SEARCH", StringComparison.Ordinal)) continue;
                    var reply = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n"
                        + $"LOCATION: http://{_locationHost}:{HttpPort}/desc.xml\r\n\r\n");
                    await _ssdp.SendAsync(reply, received.RemoteEndPoint);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }

        private async Task ServeHttpAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _http.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _httpConnections);
                    await using var stream = client.GetStream();
                    var (head, body) = await ReadRequestAsync(stream);
                    var (status, content) = Respond(head, body);
                    var bytes = Encoding.UTF8.GetBytes(content);
                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status}\r\nContent-Type: text/xml\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(bytes);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
                // Stopped.
            }
        }

        private (string Status, string Content) Respond(string head, string body)
        {
            if (head.StartsWith("GET /desc.xml", StringComparison.Ordinal))
            {
                return ("200 OK", _descriptionPrefix
                    + "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><deviceList><device><deviceList><device>"
                    + "<serviceList><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>"
                    + "<controlURL>/ctl</controlURL></service></serviceList>"
                    + "</device></deviceList></device></deviceList></device></root>");
            }

            var action = head.Split("\r\n")
                .FirstOrDefault(line => line.StartsWith("SOAPAction:", StringComparison.OrdinalIgnoreCase))?
                .Split('#').Last().Trim('"', ' ') ?? string.Empty;
            Requests.Enqueue((action, body));
            if (action == "GetExternalIPAddress")
                return ("200 OK", Envelope($"<u:GetExternalIPAddressResponse><NewExternalIPAddress>{_external}</NewExternalIPAddress></u:GetExternalIPAddressResponse>"));
            if (action == "AddPortMapping" && _addErrors.TryDequeue(out var error))
                return ("500 Internal Server Error", Envelope($"<s:Fault><detail><UPnPError><errorCode>{error}</errorCode></UPnPError></detail></s:Fault>"));
            return ("200 OK", Envelope($"<u:{action}Response/>"));

            static string Envelope(string inner) =>
                "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
                + "xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"><s:Body>" + inner + "</s:Body></s:Envelope>";
        }

        private static async Task<(string Head, string Body)> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new List<byte>();
            var chunk = new byte[4096];
            int headEnd;
            while ((headEnd = IndexOfHeadEnd(buffer)) < 0)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) break;
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }

            var head = Encoding.ASCII.GetString(buffer.ToArray(), 0, Math.Max(0, headEnd));
            var lengthLine = head.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim());
            while (buffer.Count - (headEnd + 4) < length)
            {
                var read = await stream.ReadAsync(chunk);
                if (read == 0) break;
                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
            }

            return (head, Encoding.UTF8.GetString(buffer.ToArray(), headEnd + 4, length));
        }

        private static int IndexOfHeadEnd(List<byte> buffer)
        {
            for (var index = 0; index + 3 < buffer.Count; index++)
                if (buffer[index] == '\r' && buffer[index + 1] == '\n' && buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
                    return index;
            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _ssdp.Dispose();
            _http.Stop();
            await Task.WhenAll(_ssdpLoop, _httpLoop);
        }
    }
}

public sealed class RouterFactAttribute : FactAttribute
{
    public RouterFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PEERONQ_ROUTER_TEST"), "1", StringComparison.Ordinal))
            Skip = "Set PEERONQ_ROUTER_TEST=1 to ask this machine's real router (no mapping without a public address).";
    }
}
