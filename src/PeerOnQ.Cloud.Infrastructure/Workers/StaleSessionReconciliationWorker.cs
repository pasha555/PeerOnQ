using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Infrastructure.Redis;

namespace PeerOnQ.Cloud.Infrastructure.Workers;

public sealed class StaleSessionReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    CloudWorkerOptions options,
    IDistributedOperationLeaseManager leases,
    ILogger<StaleSessionReconciliationWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SessionReconciliationInterval, _time);
        do
        {
            try
            {
                await using var ownership = await leases.TryAcquireAsync(
                    "stale-session-reconciliation",
                    options.WorkerLeaseDuration,
                    stoppingToken);
                if (ownership is null) continue;
                using var ownedRun = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    ownership.LeaseLost);
                using var scope = scopeFactory.CreateScope();
                var now = _time.GetUtcNow();
                var reconciled = await scope.ServiceProvider.GetRequiredService<ISessionTelemetryService>()
                    .ReconcileStaleAsync(
                        now - options.SessionNegotiationTimeout,
                        now - options.SessionConnectedInactivityTimeout,
                        options.SessionReconciliationBatchSize,
                        ownedRun.Token);
                if (reconciled > 0) logger.LogInformation("Reconciled {SessionCount} stale remote sessions", reconciled);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Stale session reconciliation failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
