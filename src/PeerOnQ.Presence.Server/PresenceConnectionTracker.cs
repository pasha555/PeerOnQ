using System.Collections.Concurrent;

namespace PeerOnQ.Presence.Server;

public sealed class PresenceConnectionTracker
{
    private static readonly TimeSpan MinimumHeartbeatInterval = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new(StringComparer.Ordinal);

    public long Count => _connections.Count;

    // Registration is not a heartbeat: clients (0.9.81 and older) send their first heartbeat
    // right after Register, and rejecting it tore the connection down. Every later heartbeat on
    // the connection stays at least MinimumHeartbeatInterval apart.
    public void Register(string connectionId, Guid installationId, DateTimeOffset now) =>
        _connections[connectionId] = new ConnectionState(installationId, now, LastHeartbeatUtc: null);

    public bool AllowHeartbeat(string connectionId, DateTimeOffset now)
    {
        while (_connections.TryGetValue(connectionId, out var current))
        {
            if (current.LastHeartbeatUtc is { } last && now - last < MinimumHeartbeatInterval) return false;
            if (_connections.TryUpdate(connectionId, current with { LastHeartbeatUtc = now }, current)) return true;
        }
        return false;
    }

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IReadOnlyList<string> SnapshotConnectionIds() => _connections.Keys.ToArray();

    private sealed record ConnectionState(Guid InstallationId, DateTimeOffset RegisteredAtUtc, DateTimeOffset? LastHeartbeatUtc);
}
