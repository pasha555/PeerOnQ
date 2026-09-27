using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Infrastructure.Redis;

namespace PeerOnQ.Cloud.Infrastructure.Workers;

public sealed class PresenceExpirationWorker(
    IServiceScopeFactory scopeFactory,
    IPresenceLeaseStore presence,
    IDistributedOperationLeaseManager leases,
    CloudWorkerOptions options,
    ILogger<PresenceExpirationWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), _time);
        do
        {
            try
            {
                await using var ownership = await leases.TryAcquireAsync(
                    "presence-expiration",
                    options.WorkerLeaseDuration,
                    stoppingToken);
                if (ownership is null) continue;
                using var ownedRun = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    ownership.LeaseLost);
                var now = _time.GetUtcNow();
                var expired = await presence.CollectExpiredAsync(now, 500, ownedRun.Token);
                if (expired.Count > 0)
                {
                    using var scope = scopeFactory.CreateScope();
                    var history = scope.ServiceProvider.GetRequiredService<IPresenceHistoryWriter>();
                    foreach (var lease in expired)
                        await history.RecordEndedLeaseAsync(lease, now, ownedRun.Token);
                    logger.LogInformation("Finalized {LeaseCount} expired presence leases", expired.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Presence lease expiration failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
