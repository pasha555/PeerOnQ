using PeerOnQ.Shared.Contracts;

namespace PeerOnQ.Service.Windows;

public sealed record SessionPeer(uint ProcessId, uint SessionId, string UserSid, string ExecutablePath, bool Active);

/// <summary>Tracks only live interactive hosts; grants no permissions to the host or caller.</summary>
public sealed class SessionLeaseRegistry(string expectedExecutable)
{
    private readonly object _sync = new();
    private readonly Dictionary<uint, (SessionPeer Peer, Guid Lease, DateTimeOffset Expires)> _leases = [];

    public bool IsAllowed(SessionPeer peer) => peer.ProcessId != 0 && peer.SessionId != 0 && peer.Active &&
        !string.IsNullOrWhiteSpace(peer.UserSid) && peer.UserSid is not ("S-1-5-18" or "S-1-5-19" or "S-1-5-20") &&
        string.Equals(peer.ExecutablePath, expectedExecutable, StringComparison.OrdinalIgnoreCase);

    public Guid? Register(SessionPeer peer, DateTimeOffset now)
    {
        if (!IsAllowed(peer)) return null;
        lock (_sync)
        {
            if (_leases.TryGetValue(peer.SessionId, out var current) && current.Expires > now) return null;
            var lease = Guid.NewGuid();
            _leases[peer.SessionId] = (peer, lease, now.AddSeconds(WindowsSessionBrokerProtocol.LeaseSeconds));
            return lease;
        }
    }

    public bool Heartbeat(SessionPeer peer, Guid lease, DateTimeOffset now)
    {
        if (!IsAllowed(peer)) return false;
        lock (_sync)
        {
            if (!_leases.TryGetValue(peer.SessionId, out var current) || current.Lease != lease ||
                current.Peer != peer || current.Expires <= now) return false;
            _leases[peer.SessionId] = (peer, lease, now.AddSeconds(WindowsSessionBrokerProtocol.LeaseSeconds));
            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="peer"/> is an allowed interactive host whose session currently
    /// holds a live lease. A fresh, unregistered connection uses this to send a one-shot command
    /// (secure attention) without taking a second lease the active host already holds.
    /// </summary>
    public bool HasActiveLease(SessionPeer peer, DateTimeOffset now)
    {
        if (!IsAllowed(peer)) return false;
        lock (_sync)
        {
            return _leases.TryGetValue(peer.SessionId, out var current)
                   && current.Peer == peer && current.Expires > now;
        }
    }

    public void Remove(uint sessionId, Guid lease)
    {
        lock (_sync)
        {
            if (_leases.TryGetValue(sessionId, out var current) && current.Lease == lease) _leases.Remove(sessionId);
        }
    }
}
