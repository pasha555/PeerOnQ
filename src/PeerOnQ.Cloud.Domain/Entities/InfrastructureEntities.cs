namespace PeerOnQ.Cloud.Domain.Entities;

public sealed class InfrastructureRegion
{
    private InfrastructureRegion() { }
    public InfrastructureRegion(Guid id, string code, string displayName, DateTimeOffset createdAtUtc)
    {
        Id = id;
        Code = Device.NormalizeRequired(code, 64, nameof(code)).ToLowerInvariant();
        DisplayName = Device.NormalizeRequired(displayName, 128, nameof(displayName));
        CreatedAtUtc = createdAtUtc;
        IsEnabled = true;
    }
    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public bool IsEnabled { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}

public sealed class ServiceHealthSnapshot
{
    private ServiceHealthSnapshot() { }
    public ServiceHealthSnapshot(Guid id, Guid regionId, string service, ServiceHealthState state, double latencyMilliseconds, DateTimeOffset observedAtUtc)
    {
        if (!double.IsFinite(latencyMilliseconds) || latencyMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(latencyMilliseconds));
        Id = id;
        RegionId = regionId;
        Service = Device.NormalizeRequired(service, 128, nameof(service));
        State = state;
        LatencyMilliseconds = latencyMilliseconds;
        ObservedAtUtc = observedAtUtc;
    }
    public Guid Id { get; private set; }
    public Guid RegionId { get; private set; }
    public string Service { get; private set; } = string.Empty;
    public ServiceHealthState State { get; private set; }
    public double LatencyMilliseconds { get; private set; }
    public DateTimeOffset ObservedAtUtc { get; private set; }
}

public sealed class AlertEvent
{
    private AlertEvent() { }
    public AlertEvent(Guid id, string ruleName, AlertSeverity severity, string region, string summary, string runbookUrl, DateTimeOffset startedAtUtc)
    {
        Id = id;
        RuleName = Device.NormalizeRequired(ruleName, 128, nameof(ruleName));
        Severity = severity;
        Region = Device.NormalizeRequired(region, 64, nameof(region));
        Summary = Device.NormalizeRequired(summary, 512, nameof(summary));
        RunbookUrl = Device.NormalizeRequired(runbookUrl, 512, nameof(runbookUrl));
        StartedAtUtc = startedAtUtc;
    }
    public Guid Id { get; private set; }
    public string RuleName { get; private set; } = string.Empty;
    public AlertSeverity Severity { get; private set; }
    public string Region { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string RunbookUrl { get; private set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? ResolvedAtUtc { get; private set; }
    public void Resolve(DateTimeOffset now) => ResolvedAtUtc ??= now;
}
