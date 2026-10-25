using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Pipeline;
using SIPSorcery.Net;

namespace PeerOnQ.Media;

/// <summary>
/// ICE candidate policy and the selected-pair log. "path=Relayed" alone cannot say why two devices
/// on one LAN went through TURN; the selected local/remote candidate types, addresses and the
/// local adapter can. Public addresses keep only their first two octets.
/// </summary>
public sealed partial class WebRtcMediaSession
{
    private int _filteredLocalCandidates;

    private bool AdvertiseLocalCandidate(string candidate)
    {
        if (IceCandidatePolicy.ShouldAdvertise(candidate)) return true;
        if (Interlocked.Increment(ref _filteredLocalCandidates) == 1)
            LogVirtualCandidateWithheld(_log, SessionId);
        return false;
    }

    private void LogSelectedCandidatePair()
    {
        try
        {
            var nominated = _peer.GetRtpChannel().NominatedEntry;
            if (nominated is null) return;
            var local = nominated.LocalCandidate;
            var remote = nominated.RemoteCandidate;
            LogCandidatePair(_log, SessionId, GetSelectedPath(nominated),
                local.type.ToString(), Describe(local.address), local.port,
                IceCandidatePolicy.AdapterNameOf(local.address) ?? "unknown",
                remote.type.ToString(), Describe(remote.address), remote.port,
                Volatile.Read(ref _filteredLocalCandidates));
        }
        catch (Exception ex)
        {
            LogVideoAssemblyError(_log, ex);
        }
    }

    /// <summary>
    /// Field 0.9.84: TURN TLS connections (port 5349) grew from 2 to 4 across sessions. Each
    /// session has up to four peer connections with their own allocation, so open connections
    /// during a session are expected; any still open 5 s after the last close are logged.
    /// </summary>
    private void ReportTurnConnectionsAfterClose() =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var open = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpConnections()
                    .Count(connection => connection.State == System.Net.NetworkInformation.TcpState.Established
                                         && connection.RemoteEndPoint.Port is 5349 or 3478);
                if (open > 0) LogTurnConnectionsAfterClose(_log, SessionId, open);
            }
            catch (Exception ex)
            {
                LogVideoAssemblyError(_log, ex);
            }
        });

    /// <summary>
    /// Closes the TCP/TLS connections this session's peers opened to TURN servers. The maintained
    /// SIPSorcery 8.0.14 derivative closes its turns: (TLS) connections with the RTP channel, but
    /// a session that left connections open (0.9.86: 2 per session, against user-quota=4) kept
    /// its coturn allocations alive, so this stays as a safety net for every TCP/TLS socket.
    /// coturn deletes the allocation of a TCP/TLS client when that connection closes. UDP
    /// allocations stop being refreshed and expire on their own lifetime; a lifetime-0 Refresh
    /// would need SIPSorcery's internal TURN credentials and nonce.
    /// </summary>
    internal static int ReleaseTurnConnections(params RTCPeerConnection?[] peers)
    {
        var released = 0;
        foreach (var peer in peers)
        {
            IReadOnlyCollection<Socket>? sockets;
            try
            {
                sockets = peer?.GetRtpChannel()?.RtpTcpSocketByUri?.Values.ToArray();
            }
            catch (ObjectDisposedException)
            {
                continue;
            }

            foreach (var socket in sockets ?? [])
            {
                try
                {
                    var connected = socket.Connected;
                    if (connected) socket.Shutdown(SocketShutdown.Both);
                    socket.Close();
                    if (connected) released++;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // Already reset by the server or closed by the ICE channel.
                }
            }
        }

        return released;
    }

    [LoggerMessage(1423, LogLevel.Information, "Media {SessionId} closed; {Count} TCP connections to TURN ports (3478/5349) are still open 5 s later (other sessions included)")]
    private static partial void LogTurnConnectionsAfterClose(ILogger logger, SessionId sessionId, int count);

    [LoggerMessage(1424, LogLevel.Information, "Media {SessionId} closed; released {Released} TURN TCP/TLS connection(s)")]
    private static partial void LogTurnConnectionsReleased(ILogger logger, SessionId sessionId, int released);

    private static string Describe(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed)) return "unresolved";
        if (IsPrivate(parsed)) return parsed.ToString();
        var bytes = parsed.GetAddressBytes();
        return parsed.AddressFamily == AddressFamily.InterNetwork
            ? $"{bytes[0]}.{bytes[1]}.*.*"
            : $"{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}::*";
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    [LoggerMessage(1421, LogLevel.Information, "Media {SessionId} selected {Path}: local {LocalType} {LocalAddress}:{LocalPort} on {Adapter}, remote {RemoteType} {RemoteAddress}:{RemotePort}; {Withheld} virtual-adapter candidates withheld")]
    private static partial void LogCandidatePair(
        ILogger logger, SessionId sessionId, PeerOnQ.Application.Abstractions.ConnectionPath path,
        string localType, string localAddress, int localPort, string adapter,
        string remoteType, string remoteAddress, int remotePort, int withheld);

    [LoggerMessage(1422, LogLevel.Information, "Media {SessionId} withholds host candidates of virtual adapters (Hyper-V/WSL, VMware, VirtualBox, Docker)")]
    private static partial void LogVirtualCandidateWithheld(ILogger logger, SessionId sessionId);
}
