using System.Collections.Concurrent;

namespace PeerOnQ.Presence.Server;

public sealed class PresenceConnectionTracker
{
    private static readonly TimeSpan MinimumHeartbeatInterval = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new(StringComparer.Ordinal);

    public long Count => _connections.Count;

    public void Register(string connectionId, Guid installationId, DateTimeOffset now) =>
        _connections[connectionId] = new ConnectionState(installationId, now);

    public bool AllowHeartbeat(string connectionId, DateTimeOffset now)
    {
        while (_connections.TryGetValue(connectionId, out var current))
        {
            if (now - current.LastHeartbeatUtc < MinimumHeartbeatInterval) return false;
            if (_connections.TryUpdate(connectionId, current with { LastHeartbeatUtc = now }, current)) return true;
        }
        return false;
    }

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public IReadOnlyList<string> SnapshotConnectionIds() => _connections.Keys.ToArray();

    private sealed record ConnectionState(Guid InstallationId, DateTimeOffset LastHeartbeatUtc);
}
