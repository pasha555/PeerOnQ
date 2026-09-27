using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Infrastructure.Redis;

namespace PeerOnQ.Cloud.Infrastructure.Workers;

public sealed class RetentionWorker(
    IServiceScopeFactory scopeFactory,
    RetentionOptions options,
    CloudWorkerOptions workerOptions,
    IDistributedOperationLeaseManager leases,
    ILogger<RetentionWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), _time);
        do
        {
            try
            {
                await using var ownership = await leases.TryAcquireAsync(
                    "retention",
                    workerOptions.WorkerLeaseDuration,
                    stoppingToken);
                if (ownership is null) continue;
                using var ownedRun = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    ownership.LeaseLost);
                await ExecuteBoundedRunAsync(ownedRun.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Cloud retention run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ExecuteBoundedRunAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        var now = _time.GetUtcNow();
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRetentionRepository>();

        var changed = 0;
        changed += await repository.DeleteExpiredPresenceHistoryAsync(now - options.PresenceHistory, options.BatchSize, cancellationToken);
        changed += await repository.AnonymizeExpiredDownloadEventsAsync(now - options.DownloadEvents, options.BatchSize, cancellationToken);
        changed += await repository.DeleteExpiredSessionMetadataAsync(now - options.SessionMetadata, options.BatchSize, cancellationToken);
        if (!options.DiagnosticsLegalHold)
        {
            var diagnosticBlobs = scope.ServiceProvider.GetRequiredService<IDiagnosticBlobLifecycleStore>();
            var diagnosticClaims = await repository.ClaimExpiredDiagnosticsAsync(now, options.BatchSize, cancellationToken);
            foreach (var claim in diagnosticClaims)
            {
                try
                {
                    await diagnosticBlobs.DeleteAsync(claim.DiagnosticId, claim.StorageObjectKey, cancellationToken);
                    await repository.CompleteDiagnosticExpirationAsync(claim.DiagnosticId, now, cancellationToken);
                    changed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Diagnostic {DiagnosticId} expiration will be retried", claim.DiagnosticId);
                }
            }
        }
        else logger.LogInformation("Diagnostic retention is suspended by legal hold");
        changed += await repository.DeleteExpiredServiceHealthAsync(now - options.ServiceHealthSnapshots, options.BatchSize, cancellationToken);
        changed += await repository.DeleteExpiredAdminSessionsAsync(now - options.CompletedAdminSessions, options.BatchSize, cancellationToken);

        var audit = await repository.ApplyRetentionBatchAsync(new RetentionBatchPolicy(
            RetentionRecordKind.AuditEvents, options.PolicyVersion, options.AuditEvents,
            options.AuditEventsEnabled, options.AuditEventsLegalHold), now, options.BatchSize, cancellationToken);
        var alerts = await repository.ApplyRetentionBatchAsync(new RetentionBatchPolicy(
            RetentionRecordKind.AlertEvents, options.PolicyVersion, options.AlertEvents,
            options.AlertEventsEnabled, options.AlertEventsLegalHold), now, options.BatchSize, cancellationToken);
        changed += audit.DeletedCount + alerts.DeletedCount;
        LogEvidence(audit);
        LogEvidence(alerts);
        logger.LogInformation("Cloud retention run changed {RecordCount} records", changed);
    }

    private void LogEvidence(RetentionBatchResult result)
    {
        if (result.Skipped)
            logger.LogInformation("{RecordType} retention skipped: {Reason}", result.RecordType, result.SkipReason);
        else if (result.EvidenceId is { } evidenceId)
            logger.LogInformation("{RecordType} retention deleted {DeletedCount} records; evidence {EvidenceId}",
                result.RecordType, result.DeletedCount, evidenceId);
    }
}
