using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Cloud.Infrastructure.Redis;

namespace PeerOnQ.Admin.Api;

internal sealed class AdminInfrastructureSnapshotWorker(
    IServiceScopeFactory scopeFactory,
    AdminInfrastructureMetricsService metrics,
    IDistributedOperationLeaseManager leases,
    IOptions<AdminInfrastructureMetricsOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminInfrastructureSnapshotWorker> logger) : BackgroundService
{
    private const string LeaseName = "admin-infrastructure-snapshots";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.PrometheusEndpoint is null) return;

        var interval = TimeSpan.FromSeconds(options.Value.SnapshotIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            IDistributedOperationLease? ownership;
            try
            {
                ownership = await leases.TryAcquireAsync(
                    LeaseName,
                    options.Value.SnapshotLeaseDuration,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Infrastructure snapshot lease acquisition failed");
                await Task.Delay(interval, timeProvider, stoppingToken);
                continue;
            }

            if (ownership is null)
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
                continue;
            }

            await using var ownedLease = ownership;
            using var ownedRun = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                ownedLease.LeaseLost);
            while (!ownedRun.IsCancellationRequested)
            {
                try
                {
                    await RecordOnceAsync(ownedRun.Token);
                }
                catch (OperationCanceledException) when (ownedRun.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Infrastructure health snapshot collection failed");
                }

                try
                {
                    await Task.Delay(interval, timeProvider, ownedRun.Token);
                }
                catch (OperationCanceledException) when (ownedRun.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal async Task<int> RecordOnceAsync(CancellationToken cancellationToken)
    {
        var observations = await metrics.GetServiceHealthAsync(cancellationToken);
        if (observations.Count == 0) return 0;
        var unknownCount = observations.Count(value => value.State == ServiceHealthState.Unknown);
        if (unknownCount > 0)
            logger.LogWarning("Recording {UnknownCount} infrastructure health targets as Unknown", unknownCount);

        await using var scope = scopeFactory.CreateAsyncScope();
        var written = await scope.ServiceProvider.GetRequiredService<AdminInfrastructureSnapshotStore>()
            .RecordAsync(
                options.Value.Region,
                observations,
                timeProvider.GetUtcNow(),
                cancellationToken);
        logger.LogDebug("Recorded {SnapshotCount} bounded infrastructure health snapshots", written);
        return written;
    }
}

internal sealed class AdminInfrastructureSnapshotStore(CloudDbContext db)
{
    internal async Task<int> RecordAsync(
        string regionCode,
        IReadOnlyList<AdminServiceHealthObservation> observations,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (observations.Count == 0) return 0;
        if (observations.Count > 6)
            throw new ArgumentOutOfRangeException(nameof(observations), "At most six fixed service snapshots may be recorded per run.");

        var normalizedRegion = regionCode.Trim().ToLowerInvariant();
        var region = await db.InfrastructureRegions
            .SingleOrDefaultAsync(value => value.Code == normalizedRegion, cancellationToken);
        if (region is null)
        {
            region = new InfrastructureRegion(Guid.NewGuid(), normalizedRegion, normalizedRegion, observedAtUtc);
            db.InfrastructureRegions.Add(region);
        }

        var snapshots = CreateSnapshots(region.Id, observations, observedAtUtc);
        db.ServiceHealthSnapshots.AddRange(snapshots);
        await db.SaveChangesAsync(cancellationToken);
        return snapshots.Count;
    }

    internal static IReadOnlyList<ServiceHealthSnapshot> CreateSnapshots(
        Guid regionId,
        IReadOnlyList<AdminServiceHealthObservation> observations,
        DateTimeOffset observedAtUtc) => observations
            .Select(observation => new ServiceHealthSnapshot(
                Guid.NewGuid(),
                regionId,
                observation.Service,
                observation.State,
                observation.LatencyMilliseconds,
                observedAtUtc))
            .ToArray();
}
