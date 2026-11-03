using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Nat;

namespace PeerOnQ.Media;

/// <summary>
/// Router port mapping for the ICE sockets, and the candidate mix behind the selected path. The
/// 0.9.90 relayed sessions paired this side's host candidate with the peer's TURN relay: the peer's
/// own NAT admitted no direct check. A mapped port is reachable from any NAT, so such a peer can
/// still connect directly when this side's router forwards it.
/// </summary>
public sealed partial class WebRtcMediaSession
{
    private SessionPortMappings? _portMappings;
    private int _mappingUnavailableLogged;
    private int _localHost, _localServerReflexive, _localRelay, _localMapped;
    private int _remoteHost, _remoteServerReflexive, _remoteRelay, _remoteOther;

    /// <summary>Lets this session ask its default gateway to forward its ICE ports; the app opts in.</summary>
    internal void EnableRouterPortMapping(IRouterPortMapper mapper) => _portMappings ??= new SessionPortMappings(mapper);

    /// <summary>
    /// For a host candidate on a NAT'ed IPv4 interface, maps its port and advertises the mapped
    /// address on the same lane through <paramref name="advertise"/>.
    /// </summary>
    private void MapRouterPort(string candidate, Action<string> advertise)
    {
        if (_portMappings is not { } mappings || !TryParseNatedHost(candidate, out var address, out var port)) return;
        mappings.Request(address, port, mapping =>
        {
            Interlocked.Increment(ref _localMapped);
            LogRouterPortMapped(_log, SessionId, mapping.Method.ToString(), Describe(mapping.ExternalAddress.ToString()),
                mapping.ExternalPort, mapping.LocalPort);
            advertise(SessionPortMappings.Candidate(mapping));
        }, reason =>
        {
            // One line per session: every lane meets the same router.
            if (Interlocked.Exchange(ref _mappingUnavailableLogged, 1) == 0)
                LogRouterPortMappingUnavailable(_log, SessionId, reason);
        });
    }

    /// <summary>"candidate:f 1 udp p ADDRESS PORT typ host ..." on a private, non-loopback IPv4 address.</summary>
    internal static bool TryParseNatedHost(string candidate, out IPAddress address, out int port)
    {
        address = IPAddress.None;
        port = 0;
        var fields = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 8
               && string.Equals(fields[2], "udp", StringComparison.OrdinalIgnoreCase)
               && string.Equals(fields[6], "typ", StringComparison.OrdinalIgnoreCase)
               && string.Equals(fields[7], "host", StringComparison.OrdinalIgnoreCase)
               && IPAddress.TryParse(fields[4], out address!)
               && address.AddressFamily == AddressFamily.InterNetwork
               && !IPAddress.IsLoopback(address)
               && !Nat.RouterPortMapper.IsPublic(address)
               && address.GetAddressBytes() is not [169, 254, ..]
               && int.TryParse(fields[5], out port)
               && port is > 0 and <= ushort.MaxValue;
    }

    private void CountLocalCandidate(string candidate)
    {
        switch (CandidateType(candidate))
        {
            case "host": Interlocked.Increment(ref _localHost); break;
            case "srflx" or "prflx": Interlocked.Increment(ref _localServerReflexive); break;
            default: Interlocked.Increment(ref _localRelay); break;
        }
    }

    private void CountRemoteCandidate(string candidate)
    {
        switch (CandidateType(candidate))
        {
            case "host": Interlocked.Increment(ref _remoteHost); break;
            case "srflx": Interlocked.Increment(ref _remoteServerReflexive); break;
            case "relay": Interlocked.Increment(ref _remoteRelay); break;
            default: Interlocked.Increment(ref _remoteOther); break;
        }
    }

    private static string CandidateType(string candidate)
    {
        var fields = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typ = Array.FindIndex(fields, field => string.Equals(field, "typ", StringComparison.OrdinalIgnoreCase));
        return typ >= 0 && typ + 1 < fields.Length ? fields[typ + 1].ToLowerInvariant() : "unknown";
    }

    /// <summary>
    /// Without a direct remote candidate (host/srflx) or with only relays on both sides the path is
    /// relayed by necessity; with both present a relay means every direct check failed.
    /// </summary>
    private void LogCandidateMix() =>
        LogIceCandidateMix(_log, SessionId,
            Volatile.Read(ref _localHost), Volatile.Read(ref _localServerReflexive),
            Volatile.Read(ref _localRelay), Volatile.Read(ref _localMapped),
            Volatile.Read(ref _remoteHost), Volatile.Read(ref _remoteServerReflexive),
            Volatile.Read(ref _remoteRelay), Volatile.Read(ref _remoteOther));

    private void ReleaseRouterPortMappings() => _portMappings?.Dispose();

    [LoggerMessage(1425, LogLevel.Information, "Media {SessionId} router ({Method}) forwards {ExternalAddress}:{ExternalPort} to ICE port {LocalPort}; advertised as a server-reflexive candidate")]
    private static partial void LogRouterPortMapped(
        ILogger logger, SessionId sessionId, string method, string externalAddress, int externalPort, int localPort);

    [LoggerMessage(1427, LogLevel.Information, "Media {SessionId} has no router port mapping for its ICE sockets: {Reason}")]
    private static partial void LogRouterPortMappingUnavailable(ILogger logger, SessionId sessionId, string reason);

    [LoggerMessage(1426, LogLevel.Information, "Media {SessionId} ICE candidates: local host={LocalHost} srflx={LocalSrflx} relay={LocalRelay} mapped={LocalMapped}; remote host={RemoteHost} srflx={RemoteSrflx} relay={RemoteRelay} other={RemoteOther}")]
    private static partial void LogIceCandidateMix(
        ILogger logger, SessionId sessionId, int localHost, int localSrflx, int localRelay, int localMapped,
        int remoteHost, int remoteSrflx, int remoteRelay, int remoteOther);
}
