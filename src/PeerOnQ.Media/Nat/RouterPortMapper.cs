using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PeerOnQ.Media.Nat;

internal enum RouterPortMappingMethod
{
    NatPmp,
    Upnp,
}

/// <summary>A public UDP port the local default gateway forwards to one local ICE socket.</summary>
internal sealed record RouterPortMapping(
    IPAddress Gateway,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress ExternalAddress,
    int ExternalPort,
    RouterPortMappingMethod Method,
    Uri? UpnpControlUrl = null,
    string? UpnpServiceType = null);

internal interface IRouterPortMapper
{
    Task<RouterPortMapping?> MapUdpAsync(IPAddress localAddress, int localPort, CancellationToken cancellationToken);

    Task<bool> RenewAsync(RouterPortMapping mapping, CancellationToken cancellationToken);

    Task UnmapAsync(RouterPortMapping mapping, CancellationToken cancellationToken);

    /// <summary>Why the last mapping for <paramref name="localAddress"/> failed, for the session log.</summary>
    string? LastFailure(IPAddress localAddress);
}

/// <summary>
/// Asks the local default gateway to forward a public UDP port to an ICE socket, as remote-desktop
/// products do so a peer behind a symmetric (mobile/CGNAT) NAT can still reach this side directly.
/// NAT-PMP (RFC 6886) is one datagram; UPnP IGD falls back to SSDP discovery and SOAP. Only the
/// interface's own default gateway is contacted, mappings are leased (a router that only offers
/// permanent ones is not used), and nothing is mapped while the router's own external address is
/// private: behind a second NAT or CGNAT the mapping would not make the socket reachable.
/// </summary>
internal sealed class RouterPortMapper : IRouterPortMapper
{
    internal static readonly TimeSpan Lease = TimeSpan.FromHours(1);
    private static readonly TimeSpan DiscoveryLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailedDiscoveryLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(2);
    private const int MaximumXmlBytes = 64 * 1024;
    private const int UpnpConflictInMappingEntry = 718;
    private const string MappingDescription = "PeerOnQ";
    private static readonly IPAddress SsdpMulticast = IPAddress.Parse("239.255.255.250");

    // WANIPConnection:2 first: routers that offer it implement the lease semantics most carefully.
    private static readonly string[] IgdServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    public static RouterPortMapper Shared { get; } = new();

    private readonly Func<IPAddress, IPAddress?> _gatewayOf;
    private readonly int _natPmpPort;
    private readonly int _ssdpPort;
    private readonly bool _multicastDiscovery;
    private readonly TimeSpan _replyTimeout;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<(IPAddress Gateway, IPAddress Local), (DateTimeOffset At, Task<UpnpService?> Service)> _upnp = new();
    private readonly ConcurrentDictionary<IPAddress, DateTimeOffset> _natPmpUnavailable = new();
    private readonly ConcurrentDictionary<IPAddress, string> _lastFailure = new();

    internal sealed record UpnpService(Uri ControlUrl, string ServiceType);

    /// <summary>The most specific reason one mapping attempt found; the first protocol to explain wins.</summary>
    private sealed class Attempt
    {
        public string? Failure { get; set; }
    }

    public RouterPortMapper()
        : this(DefaultGatewayOf, natPmpPort: 5351, ssdpPort: 1900, multicastDiscovery: true, TimeSpan.FromMilliseconds(1500))
    {
    }

    internal RouterPortMapper(
        Func<IPAddress, IPAddress?> gatewayOf, int natPmpPort, int ssdpPort, bool multicastDiscovery, TimeSpan replyTimeout)
    {
        _gatewayOf = gatewayOf;
        _natPmpPort = natPmpPort;
        _ssdpPort = ssdpPort;
        _multicastDiscovery = multicastDiscovery;
        _replyTimeout = replyTimeout;
        // The gateway is on the local link: never send these requests through a configured proxy.
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = HttpTimeout,
        })
        {
            Timeout = HttpTimeout,
        };
    }

    public async Task<RouterPortMapping?> MapUdpAsync(IPAddress localAddress, int localPort, CancellationToken cancellationToken)
    {
        if (localAddress.AddressFamily != AddressFamily.InterNetwork || localPort is <= 0 or > ushort.MaxValue) return null;
        if (_gatewayOf(localAddress) is not { } gateway)
        {
            _lastFailure[localAddress] = "the adapter has no IPv4 default gateway";
            return null;
        }

        var attempt = new Attempt();
        // UPnP discovery runs alongside NAT-PMP's reply window instead of after it (2.7 s on the
        // 2026-10-27 test router); both results are cached for later lanes and sessions.
        var discovery = DiscoverAsync(gateway, localAddress, cancellationToken);
        var mapping = await MapWithNatPmpAsync(gateway, localAddress, localPort, attempt, cancellationToken).ConfigureAwait(false)
                      ?? await MapWithUpnpAsync(gateway, localAddress, localPort, discovery, attempt, cancellationToken).ConfigureAwait(false);
        if (mapping is null)
            _lastFailure[localAddress] = attempt.Failure ?? "the router answers neither NAT-PMP nor UPnP IGD (both may be disabled)";
        else
            _lastFailure.TryRemove(localAddress, out _);
        return mapping;
    }

    public string? LastFailure(IPAddress localAddress) =>
        _lastFailure.TryGetValue(localAddress, out var failure) ? failure : null;

    /// <summary>
    /// Finds the gateways' NAT-PMP and UPnP services before a session gathers candidates, so a
    /// mapping then costs one request and is advertised ahead of the TURN relay's checks. Cached
    /// results make repeated calls cheap; nothing is mapped here.
    /// </summary>
    internal void Prewarm() =>
        _ = Task.Run(async () =>
        {
            foreach (var (localAddress, gateway) in PhysicalGateways())
            {
                try
                {
                    var discovery = DiscoverAsync(gateway, localAddress, CancellationToken.None);
                    if (!_natPmpUnavailable.TryGetValue(gateway, out var failedAt)
                        || DateTimeOffset.UtcNow - failedAt >= FailedDiscoveryLifetime)
                    {
                        if (await NatPmpRequestAsync(gateway, localAddress, [0, 0], 12, CancellationToken.None)
                                .ConfigureAwait(false) is null)
                            _natPmpUnavailable[gateway] = DateTimeOffset.UtcNow;
                    }

                    await discovery.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A later mapping attempt asks again.
                }
            }
        });

    internal RouterPortMapper Prewarmed()
    {
        Prewarm();
        return this;
    }

    /// <summary>Private IPv4 addresses of the up, non-virtual adapters with their default gateway.</summary>
    private static List<(IPAddress Local, IPAddress Gateway)> PhysicalGateways()
    {
        var gateways = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || Pipeline.IceCandidatePolicy.IsVirtualAdapter(adapter.Name, adapter.Description))
                    continue;
                var properties = adapter.GetIPProperties();
                var gateway = properties.GatewayAddresses.Select(candidate => candidate.Address)
                    .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork
                                                 && !candidate.Equals(IPAddress.Any));
                if (gateway is null) continue;
                gateways.AddRange(properties.UnicastAddresses
                    .Select(unicast => unicast.Address)
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                                      && !IsPublic(address) && !IPAddress.IsLoopback(address))
                    .Select(address => (address, gateway)));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // No adapter table: mapping attempts discover on demand.
        }

        return gateways;
    }

    private static string PrivateExternal(IPAddress external) =>
        $"the router's own external address {external} is not public: another NAT or CGNAT sits upstream";

    public async Task<bool> RenewAsync(RouterPortMapping mapping, CancellationToken cancellationToken) =>
        mapping.Method == RouterPortMappingMethod.NatPmp
            ? await NatPmpMapAsync(mapping.Gateway, mapping.LocalAddress, mapping.LocalPort, mapping.ExternalPort,
                (uint)Lease.TotalSeconds, cancellationToken).ConfigureAwait(false) == mapping.ExternalPort
            : await AddUpnpMappingAsync(new UpnpService(mapping.UpnpControlUrl!, mapping.UpnpServiceType!),
                mapping.LocalAddress, mapping.LocalPort, mapping.ExternalPort, cancellationToken).ConfigureAwait(false) == 0;

    public async Task UnmapAsync(RouterPortMapping mapping, CancellationToken cancellationToken)
    {
        if (mapping.Method == RouterPortMappingMethod.NatPmp)
        {
            // RFC 6886 section 3.4: lifetime 0 and suggested external port 0 delete the mapping.
            await NatPmpMapAsync(mapping.Gateway, mapping.LocalAddress, mapping.LocalPort, 0, 0, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await SoapAsync(new UpnpService(mapping.UpnpControlUrl!, mapping.UpnpServiceType!), "DeletePortMapping",
            [
                ("NewRemoteHost", string.Empty),
                ("NewExternalPort", mapping.ExternalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewProtocol", "UDP"),
            ], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The IPv4 default gateway of the adapter that owns <paramref name="localAddress"/>.</summary>
    internal static IPAddress? DefaultGatewayOf(IPAddress localAddress)
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                var properties = adapter.GetIPProperties();
                if (!properties.UnicastAddresses.Any(unicast => unicast.Address.Equals(localAddress))) continue;
                return properties.GatewayAddresses
                    .Select(gateway => gateway.Address)
                    .FirstOrDefault(gateway => gateway.AddressFamily == AddressFamily.InterNetwork
                                               && !gateway.Equals(IPAddress.Any));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // Without the adapter table there is no gateway to ask.
        }

        return null;
    }

    /// <summary>True for a globally routable IPv4 address; a mapping to anything else is unreachable.</summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 or >= 224
                 || (b[0] == 100 && b[1] is >= 64 and <= 127)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 172 && b[1] is >= 16 and <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                 || (b[0] == 198 && b[1] is 18 or 19)
                 || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                 || (b[0] == 203 && b[1] == 0 && b[2] == 113));
    }

    private async Task<RouterPortMapping?> MapWithNatPmpAsync(
        IPAddress gateway, IPAddress localAddress, int localPort, Attempt attempt, CancellationToken cancellationToken)
    {
        if (_natPmpUnavailable.TryGetValue(gateway, out var failedAt)
            && DateTimeOffset.UtcNow - failedAt < FailedDiscoveryLifetime)
            return null;

        var reply = await NatPmpRequestAsync(gateway, localAddress, [0, 0], 12, cancellationToken).ConfigureAwait(false);
        if (reply is null || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)) != 0)
        {
            _natPmpUnavailable[gateway] = DateTimeOffset.UtcNow;
            return null;
        }

        var external = new IPAddress(reply.AsSpan(8, 4));
        if (!IsPublic(external))
        {
            attempt.Failure ??= PrivateExternal(external);
            return null;
        }

        var externalPort = await NatPmpMapAsync(gateway, localAddress, localPort, localPort, (uint)Lease.TotalSeconds,
            cancellationToken).ConfigureAwait(false);
        if (externalPort is > 0)
            return new RouterPortMapping(gateway, localAddress, localPort, external, externalPort.Value, RouterPortMappingMethod.NatPmp);
        attempt.Failure ??= "the router refused the NAT-PMP mapping";
        return null;
    }

    /// <summary>Opcode 1 (UDP): returns the granted external port, or null without a valid grant.</summary>
    private async Task<int?> NatPmpMapAsync(IPAddress gateway, IPAddress localAddress, int localPort,
        int suggestedExternalPort, uint lifetimeSeconds, CancellationToken cancellationToken)
    {
        var request = new byte[12];
        request[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)localPort);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)suggestedExternalPort);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), lifetimeSeconds);
        var reply = await NatPmpRequestAsync(gateway, localAddress, request, 16, cancellationToken).ConfigureAwait(false);
        if (reply is null
            || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)) != 0
            || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8)) != localPort)
            return null;
        var externalPort = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10));
        var lifetime = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(12));
        return lifetimeSeconds == 0 || (externalPort != 0 && lifetime != 0) ? externalPort : null;
    }

    private async Task<byte[]?> NatPmpRequestAsync(
        IPAddress gateway, IPAddress localAddress, byte[] request, int replyLength, CancellationToken cancellationToken)
    {
        var endpoint = new IPEndPoint(gateway, _natPmpPort);
        using var socket = new UdpClient(new IPEndPoint(localAddress, 0));
        // Two tries inside the reply budget: an interactive session cannot wait RFC 6886's 64 s.
        var wait = _replyTimeout / 3;
        for (var attempt = 0; attempt < 2; attempt++, wait *= 2)
        {
            try
            {
                await socket.SendAsync(request, endpoint, cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(wait);
                while (true)
                {
                    var received = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    var reply = received.Buffer;
                    // Only the gateway answers NAT-PMP, with the request opcode + 128.
                    if (received.RemoteEndPoint.Equals(endpoint) && reply.Length >= replyLength
                        && reply[0] == 0 && reply[1] == request[1] + 128)
                        return reply;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // No reply in this attempt.
            }
            catch (SocketException)
            {
                // ICMP port unreachable: the gateway has no NAT-PMP service.
                return null;
            }
        }

        return null;
    }

    private async Task<RouterPortMapping?> MapWithUpnpAsync(IPAddress gateway, IPAddress localAddress, int localPort,
        Task<UpnpService?> discovery, Attempt attempt, CancellationToken cancellationToken)
    {
        if (await discovery.ConfigureAwait(false) is not { } service) return null;
        var (status, reply) = await SoapAsync(service, "GetExternalIPAddress", [], cancellationToken).ConfigureAwait(false);
        var externalText = reply?.Descendants().FirstOrDefault(element => element.Name.LocalName == "NewExternalIPAddress")?.Value;
        if (status != 0 || !IPAddress.TryParse(externalText, out var external))
        {
            attempt.Failure ??= $"the router's UPnP service did not report its external address (status {status})";
            return null;
        }

        if (!IsPublic(external))
        {
            attempt.Failure ??= PrivateExternal(external);
            return null;
        }

        // The same external port first, then a few random ones if another device holds it.
        foreach (var externalPort in new[] { localPort, Random.Shared.Next(49152, 65536), Random.Shared.Next(49152, 65536) })
        {
            var error = await AddUpnpMappingAsync(service, localAddress, localPort, externalPort, cancellationToken)
                .ConfigureAwait(false);
            if (error == 0)
            {
                return new RouterPortMapping(gateway, localAddress, localPort, external, externalPort,
                    RouterPortMappingMethod.Upnp, service.ControlUrl, service.ServiceType);
            }

            // 725 OnlyPermanentLeasesSupported and every other error: never leave a permanent hole.
            if (error != UpnpConflictInMappingEntry)
            {
                attempt.Failure ??= error == 725
                    ? "the router offers only permanent UPnP mappings, which PeerOnQ never creates"
                    : $"the router refused the UPnP mapping (error {error})";
                return null;
            }
        }

        attempt.Failure ??= "every tried external port is taken on the router";
        return null;
    }

    private async Task<int> AddUpnpMappingAsync(
        UpnpService service, IPAddress localAddress, int localPort, int externalPort, CancellationToken cancellationToken)
    {
        var (status, _) = await SoapAsync(service, "AddPortMapping",
            [
                ("NewRemoteHost", string.Empty),
                ("NewExternalPort", externalPort.ToString(CultureInfo.InvariantCulture)),
                ("NewProtocol", "UDP"),
                ("NewInternalPort", localPort.ToString(CultureInfo.InvariantCulture)),
                ("NewInternalClient", localAddress.ToString()),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", MappingDescription),
                ("NewLeaseDuration", ((int)Lease.TotalSeconds).ToString(CultureInfo.InvariantCulture)),
            ], cancellationToken).ConfigureAwait(false);
        return status;
    }

    private Task<UpnpService?> DiscoverAsync(IPAddress gateway, IPAddress localAddress, CancellationToken cancellationToken)
    {
        var key = (gateway, localAddress);
        var now = DateTimeOffset.UtcNow;
        if (_upnp.TryGetValue(key, out var cached))
        {
            var found = cached.Service.IsCompletedSuccessfully && cached.Service.Result is not null;
            var lifetime = !cached.Service.IsCompleted ? DiscoveryLifetime
                : found ? DiscoveryLifetime : FailedDiscoveryLifetime;
            if (now - cached.At < lifetime) return cached.Service.WaitAsync(cancellationToken);
        }

        // Shared by every lane of every session on this gateway; bounded by its own timeouts.
        var discovery = DiscoverUncachedAsync(gateway, localAddress);
        _upnp[key] = (now, discovery);
        return discovery.WaitAsync(cancellationToken);
    }

    private async Task<UpnpService?> DiscoverUncachedAsync(IPAddress gateway, IPAddress localAddress)
    {
        try
        {
            foreach (var location in await SearchAsync(gateway, localAddress).ConfigureAwait(false))
            {
                if (await DescribeAsync(gateway, location).ConfigureAwait(false) is { } service) return service;
            }
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or XmlException or OperationCanceledException or IOException)
        {
            // No UPnP gateway on this link.
        }

        return null;
    }

    /// <summary>SSDP M-SEARCH sent from the gateway's interface; only the gateway's own replies count.</summary>
    private async Task<IReadOnlyList<Uri>> SearchAsync(IPAddress gateway, IPAddress localAddress)
    {
        using var socket = new UdpClient(new IPEndPoint(localAddress, 0));
        // Hyper-V/WSL adapters otherwise carry the multicast and the router never sees it.
        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
        foreach (var target in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:2", "urn:schemas-upnp-org:device:InternetGatewayDevice:1" })
        {
            var search = Encoding.ASCII.GetBytes(
                $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: {target}\r\n\r\n");
            await socket.SendAsync(search, new IPEndPoint(gateway, _ssdpPort)).ConfigureAwait(false);
            if (_multicastDiscovery) await socket.SendAsync(search, new IPEndPoint(SsdpMulticast, _ssdpPort)).ConfigureAwait(false);
        }

        var locations = new List<Uri>();
        using var timeout = new CancellationTokenSource(_replyTimeout);
        try
        {
            while (locations.Count == 0)
            {
                var received = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (!received.RemoteEndPoint.Address.Equals(gateway) || received.Buffer.Length > 4096) continue;
                foreach (var line in Encoding.ASCII.GetString(received.Buffer).Split("\r\n"))
                {
                    if (!line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (OnGateway(line["LOCATION:".Length..].Trim(), gateway) is { } location) locations.Add(location);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Search window over.
        }
        catch (SocketException)
        {
            // ICMP port unreachable from the gateway: no SSDP responder.
        }

        return locations;
    }

    private async Task<UpnpService?> DescribeAsync(IPAddress gateway, Uri location)
    {
        // HttpClient.Timeout ends at the headers; the body read needs its own bound.
        using var timeout = new CancellationTokenSource(HttpTimeout);
        using var response = await _http.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var description = ParseXml(await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false));
        if (description is null) return null;
        var baseText = description.Descendants().FirstOrDefault(element => element.Name.LocalName == "URLBase")?.Value.Trim();
        var baseUri = !string.IsNullOrEmpty(baseText) && OnGateway(baseText, gateway) is { } declared ? declared : location;
        var services = description.Descendants().Where(element => element.Name.LocalName == "service").ToList();
        foreach (var type in IgdServiceTypes)
        {
            foreach (var service in services)
            {
                var serviceType = Child(service, "serviceType");
                var control = Child(service, "controlURL");
                if (!string.Equals(serviceType, type, StringComparison.Ordinal) || string.IsNullOrEmpty(control)) continue;
                if (Uri.TryCreate(baseUri, control, out var controlUrl) && OnGateway(controlUrl.AbsoluteUri, gateway) is { } onGateway)
                    return new UpnpService(onGateway, type);
            }
        }

        return null;

        static string? Child(XElement parent, string name) =>
            parent.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
    }

    /// <summary>Plain HTTP URLs on the gateway itself; anything else on the LAN is never contacted.</summary>
    private static Uri? OnGateway(string text, IPAddress gateway) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && IPAddress.TryParse(uri.Host, out var host)
        && host.Equals(gateway)
            ? uri
            : null;

    /// <summary>Returns 0, the UPnP error code of a SOAP fault, or -1 without a usable answer.</summary>
    private async Task<(int Status, XDocument? Reply)> SoapAsync(
        UpnpService service, string action, IReadOnlyList<(string Name, string Value)> arguments,
        CancellationToken cancellationToken)
    {
        var body = new StringBuilder()
            .Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ")
            .Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>")
            .Append("<u:").Append(action).Append(" xmlns:u=\"").Append(service.ServiceType).Append("\">");
        foreach (var (name, value) in arguments)
            body.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        body.Append("</u:").Append(action).Append("></s:Body></s:Envelope>");

        using var request = new HttpRequestMessage(HttpMethod.Post, service.ControlUrl)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8),
        };
        request.Content.Headers.Remove("Content-Type");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.ServiceType}#{action}\"");
        request.Headers.ConnectionClose = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HttpTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var reply = ParseXml(await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false));
            if (response.IsSuccessStatusCode) return (0, reply);
            var error = reply?.Descendants().FirstOrDefault(element => element.Name.LocalName == "errorCode")?.Value;
            return (int.TryParse(error, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : -1, reply);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (-1, null);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumXmlBytes) throw new IOException("The gateway's XML exceeds its bound.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static XDocument? ParseXml(byte[] xml)
    {
        if (xml.Length == 0) return null;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes,
            });
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
