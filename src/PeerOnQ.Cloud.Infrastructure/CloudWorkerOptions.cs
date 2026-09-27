namespace PeerOnQ.Cloud.Infrastructure;

public sealed class CloudWorkerOptions
{
    public bool EnablePresenceExpiration { get; init; }
    public bool EnableSessionReconciliation { get; init; }
    public bool EnableRetention { get; init; }
    public TimeSpan SessionReconciliationInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan SessionNegotiationTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan SessionConnectedInactivityTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public int SessionReconciliationBatchSize { get; init; } = 500;
    public TimeSpan WorkerLeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public void Validate(int heartbeatIntervalSeconds)
    {
        if (SessionReconciliationInterval < TimeSpan.FromSeconds(10) ||
            SessionReconciliationInterval > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Session reconciliation interval must be between 10 seconds and 5 minutes.");
        if (SessionNegotiationTimeout < TimeSpan.FromSeconds(30) ||
            SessionNegotiationTimeout > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("Session negotiation timeout must be between 30 seconds and 15 minutes.");
        if (SessionConnectedInactivityTimeout < TimeSpan.FromMinutes(1) ||
            SessionConnectedInactivityTimeout > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("Connected-session inactivity timeout must be between 1 and 15 minutes.");
        if (SessionConnectedInactivityTimeout < TimeSpan.FromSeconds(heartbeatIntervalSeconds * 3L))
            throw new InvalidOperationException("Connected-session inactivity timeout must allow at least three heartbeat intervals.");
        if (SessionReconciliationBatchSize is < 1 or > 5000)
            throw new InvalidOperationException("Session reconciliation batch size must be between 1 and 5000.");
        if (WorkerLeaseDuration < TimeSpan.FromSeconds(30) || WorkerLeaseDuration > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("Cloud worker lease duration must be between 30 seconds and 10 minutes.");
    }
}
