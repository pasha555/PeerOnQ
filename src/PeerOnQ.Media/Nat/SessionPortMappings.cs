using System.Globalization;
using System.Net;

namespace PeerOnQ.Media.Nat;

/// <summary>
/// The router mappings one media session holds: at most one per local ICE port, renewed at half
/// their lease while the session lives and deleted when it closes, including a mapping that only
/// completes after the close.
/// </summary>
internal sealed class SessionPortMappings(IRouterPortMapper mapper) : IDisposable
{
    // RFC 8445 priority of a server-reflexive candidate: type preference 100, local preference
    // 65535, component 1. It ranks with STUN's srflx candidates and ahead of every relay.
    internal const uint MappedCandidatePriority = (100u << 24) | (65535u << 8) | 255u;

    private static readonly TimeSpan UnmapTimeout = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private readonly Dictionary<int, RouterPortMapping?> _ports = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Timer? _renewal;
    private bool _closed;

    /// <summary>
    /// Maps <paramref name="localPort"/> once; <paramref name="mapped"/> runs after success and
    /// <paramref name="unavailable"/> with the router's reason otherwise.
    /// </summary>
    public void Request(IPAddress localAddress, int localPort, Action<RouterPortMapping> mapped,
        Action<string>? unavailable = null)
    {
        lock (_gate)
        {
            if (_closed || !_ports.TryAdd(localPort, null)) return;
        }

        _ = Task.Run(async () =>
        {
            RouterPortMapping? mapping;
            try
            {
                mapping = await mapper.MapUdpAsync(localAddress, localPort, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // ICE keeps its host, STUN and TURN candidates.
            }

            if (mapping is null)
            {
                try { unavailable?.Invoke(mapper.LastFailure(localAddress) ?? "no mapping was granted"); }
                catch (Exception) { /* Diagnostics only. */ }
                return;
            }
            bool closed;
            lock (_gate)
            {
                closed = _closed;
                if (!closed)
                {
                    _ports[localPort] = mapping;
                    _renewal ??= new Timer(_ => Renew(), null, RouterPortMapper.Lease / 2, RouterPortMapper.Lease / 2);
                }
            }

            if (closed)
            {
                Unmap(mapping);
                return;
            }

            try { mapped(mapping); }
            catch (Exception) { /* Advertising is best effort; the mapping is released with the session. */ }
        });
    }

    /// <summary>The trickled candidate for a mapped port; its base is the host socket it forwards to.</summary>
    internal static string Candidate(RouterPortMapping mapping) =>
        string.Create(CultureInfo.InvariantCulture,
            $"candidate:pnqmap{mapping.LocalPort} 1 udp {MappedCandidatePriority} {mapping.ExternalAddress} {mapping.ExternalPort} typ srflx raddr {mapping.LocalAddress} rport {mapping.LocalPort} generation 0");

    private void Renew()
    {
        RouterPortMapping[] live;
        lock (_gate)
        {
            if (_closed) return;
            live = [.. _ports.Values.OfType<RouterPortMapping>()];
        }

        foreach (var mapping in live)
        {
            _ = Task.Run(async () =>
            {
                try { await mapper.RenewAsync(mapping, _lifetime.Token).ConfigureAwait(false); }
                catch (Exception) { /* The lease then ends on its own; ICE keeps the path it has. */ }
            });
        }
    }

    private void Unmap(RouterPortMapping mapping) =>
        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(UnmapTimeout);
            try { await mapper.UnmapAsync(mapping, timeout.Token).ConfigureAwait(false); }
            catch (Exception) { /* The router drops it when the lease ends. */ }
        });

    public void Dispose()
    {
        RouterPortMapping[] live;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            live = [.. _ports.Values.OfType<RouterPortMapping>()];
            _renewal?.Dispose();
        }

        _lifetime.Cancel();
        foreach (var mapping in live) Unmap(mapping);
    }
}
