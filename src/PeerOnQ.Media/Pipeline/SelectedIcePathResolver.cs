using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Media.Pipeline;

/// <summary>Classifies nominated endpoints without treating an ICE socket's wildcard bind as a LAN address.</summary>
internal sealed class SelectedIcePathResolver(
    Func<IPEndPoint, IPAddress?>? selectRouteAddress = null,
    Func<IPAddress, bool>? isActiveLocalAddress = null,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);
    private readonly Func<IPEndPoint, IPAddress?> _selectRouteAddress = selectRouteAddress ?? SelectRouteAddress;
    private readonly Func<IPAddress, bool> _isActiveLocalAddress = isActiveLocalAddress ?? IsActiveLocalAddress;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private object? _cachedNomination;
    private IPAddress? _cachedLocal;
    private IPEndPoint? _cachedRemote;
    private long _cachedAt;
    private ConnectionPath _cachedPath;

    public ConnectionPath Classify(object nomination, string local, string remote, int remotePort)
    {
        if (!IPAddress.TryParse(local, out var localAddress)
            || !IPAddress.TryParse(remote, out var remoteAddress)
            || IsWildcard(remoteAddress))
            return ConnectionPath.UnknownNegotiating;

        if (!IsWildcard(localAddress))
            return ClassifyAddresses(localAddress, remoteAddress);

        if (remotePort <= IPEndPoint.MinPort || remotePort > IPEndPoint.MaxPort)
            return ConnectionPath.UnknownNegotiating;

        var endpoint = new IPEndPoint(remoteAddress, remotePort);
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (ReferenceEquals(_cachedNomination, nomination)
                && localAddress.Equals(_cachedLocal)
                && endpoint.Equals(_cachedRemote)
                && _time.GetElapsedTime(_cachedAt, now) < CacheLifetime)
                return _cachedPath;

            // One short-lived entry bounds memory and avoids a socket/NIC enumeration on each
            // statistics query. Changed nominations/endpoints miss; NIC state expires after 1s.
            _cachedNomination = nomination;
            _cachedLocal = localAddress;
            _cachedRemote = endpoint;
            _cachedAt = now;
            _cachedPath = ConnectionPath.UnknownNegotiating;
            try
            {
                var selected = _selectRouteAddress(endpoint);
                if (selected is not null && !IsWildcard(selected)
                    && selected.AddressFamily == remoteAddress.AddressFamily
                    && _isActiveLocalAddress(selected))
                    _cachedPath = ClassifyAddresses(selected, remoteAddress);
            }
            catch (Exception error) when (error is SocketException or NetworkInformationException
                or ArgumentException or NotSupportedException or ObjectDisposedException)
            {
                // Route/interface uncertainty never opens the LAN-only native transport gate.
            }
            return _cachedPath;
        }
    }

    private static IPAddress? SelectRouteAddress(IPEndPoint remote)
    {
        using var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        // UDP Connect selects a local route without sending a datagram to the peer.
        socket.Connect(remote);
        return (socket.LocalEndPoint as IPEndPoint)?.Address;
    }

    private static bool IsActiveLocalAddress(IPAddress address) =>
        NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up
            && adapter.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(address)));

    private static ConnectionPath ClassifyAddresses(IPAddress local, IPAddress remote) =>
        IsPrivate(local) && IsPrivate(remote) ? ConnectionPath.DirectLan : ConnectionPath.DirectInternet;

    private static bool IsWildcard(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 127
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        return address.IsIPv6LinkLocal || (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }
}
