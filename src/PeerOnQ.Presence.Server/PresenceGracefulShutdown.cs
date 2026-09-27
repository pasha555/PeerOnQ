using Microsoft.AspNetCore.SignalR;

namespace PeerOnQ.Presence.Server;

public sealed class PresenceGracefulShutdown(
    IHubContext<PresenceHub> hubs,
    PresenceConnectionTracker connections) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var localConnections = connections.SnapshotConnectionIds();
        return localConnections.Count == 0
            ? Task.CompletedTask
            : hubs.Clients.Clients(localConnections)
                .SendAsync("ServerRestarting", new { reason = "deployment" }, cancellationToken);
    }
}
