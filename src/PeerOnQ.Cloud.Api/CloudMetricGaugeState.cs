using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Observability;

namespace PeerOnQ.Cloud.Api;

public sealed class CloudMetricGaugeState
{
    private long _onlineDevices;
    private long _activeSessions;

    public PeerOnQMetricState Metrics => new()
    {
        OnlineDevices = () => Volatile.Read(ref _onlineDevices),
        ActiveSessions = () => Volatile.Read(ref _activeSessions),
    };

    public void Update(long onlineDevices, long activeSessions)
    {
        Interlocked.Exchange(ref _onlineDevices, Math.Max(0, onlineDevices));
        Interlocked.Exchange(ref _activeSessions, Math.Max(0, activeSessions));
    }
}

public sealed class CloudMetricGaugeWorker(
    IServiceScopeFactory scopeFactory,
    CloudMetricGaugeState state,
    ILogger<CloudMetricGaugeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var overview = await scope.ServiceProvider.GetRequiredService<IAdminQueryService>().GetOverviewAsync(stoppingToken);
                state.Update(overview.OnlineDevices, overview.ActiveSessions);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Cloud metric snapshot failed with {EventName} and {ExceptionType}",
                    "metrics.snapshot.failed",
                    exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
