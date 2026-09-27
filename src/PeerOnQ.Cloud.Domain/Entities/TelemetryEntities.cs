namespace PeerOnQ.Cloud.Domain.Entities;

public sealed class SessionFailure
{
    private SessionFailure() { }
    public SessionFailure(Guid id, Guid sessionId, string stage, string code, DateTimeOffset occurredAtUtc)
    {
        if (id == Guid.Empty || sessionId == Guid.Empty) throw new ArgumentException("Failure and session IDs are required.");
        Id = id;
        SessionId = sessionId;
        Stage = Device.NormalizeRequired(stage, 64, nameof(stage));
        Code = Device.NormalizeRequired(code, 128, nameof(code));
        OccurredAtUtc = occurredAtUtc;
    }
    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public string Stage { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }
}

public sealed class DevicePresenceHistory
{
    private DevicePresenceHistory() { }
    public DevicePresenceHistory(Guid id, Guid installationId, PresenceState state, string region, DateTimeOffset intervalStartedAtUtc, DateTimeOffset intervalEndedAtUtc)
    {
        if (id == Guid.Empty || installationId == Guid.Empty) throw new ArgumentException("Presence and installation IDs are required.");
        if (intervalEndedAtUtc < intervalStartedAtUtc) throw new ArgumentOutOfRangeException(nameof(intervalEndedAtUtc));
        Id = id;
        InstallationId = installationId;
        State = state;
        Region = Device.NormalizeRequired(region, 64, nameof(region));
        IntervalStartedAtUtc = intervalStartedAtUtc;
        IntervalEndedAtUtc = intervalEndedAtUtc;
    }
    public Guid Id { get; private set; }
    public Guid InstallationId { get; private set; }
    public PresenceState State { get; private set; }
    public string Region { get; private set; } = string.Empty;
    public DateTimeOffset IntervalStartedAtUtc { get; private set; }
    public DateTimeOffset IntervalEndedAtUtc { get; private set; }
}

public sealed class DownloadEvent
{
    private DownloadEvent() { }
    private DownloadEvent(Guid id, byte[] idempotencyKeyHash, byte[]? uniquenessKeyHash, PlatformKind platform, ArchitectureKind architecture, string version, InstallChannel channel, string source, string? campaign, string? countryCode, string? userAgentFamily, DateTimeOffset startedAtUtc)
    {
        Id = id;
        IdempotencyKeyHash = idempotencyKeyHash.ToArray();
        UniquenessKeyHash = uniquenessKeyHash?.ToArray();
        Platform = platform;
        Architecture = architecture;
        Version = version;
        Channel = channel;
        Source = source;
        Campaign = campaign;
        CountryCode = countryCode;
        UserAgentFamily = userAgentFamily;
        StartedAtUtc = startedAtUtc;
        Result = DownloadResult.Started;
        ConcurrencyVersion = 1;
    }

    public Guid Id { get; private set; }
    public byte[] IdempotencyKeyHash { get; private set; } = [];
    public byte[]? UniquenessKeyHash { get; private set; }
    public PlatformKind Platform { get; private set; }
    public ArchitectureKind Architecture { get; private set; }
    public string Version { get; private set; } = string.Empty;
    public InstallChannel Channel { get; private set; }
    public string Source { get; private set; } = string.Empty;
    public string? Campaign { get; private set; }
    public string? CountryCode { get; private set; }
    public string? UserAgentFamily { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DownloadResult Result { get; private set; }
    public long ConcurrencyVersion { get; private set; }

    public static DownloadEvent Start(Guid id, byte[] idempotencyKeyHash, byte[]? uniquenessKeyHash, PlatformKind platform, ArchitectureKind architecture, string version, InstallChannel channel, string source, string? campaign, string? countryCode, string? userAgentFamily, DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Download ID is required.", nameof(id));
        if (idempotencyKeyHash is not { Length: 32 }) throw new ArgumentException("Idempotency hash must contain 32 bytes.", nameof(idempotencyKeyHash));
        if (uniquenessKeyHash is not null && uniquenessKeyHash.Length != 32) throw new ArgumentException("Uniqueness hash must contain 32 bytes.", nameof(uniquenessKeyHash));
        return new DownloadEvent(id, idempotencyKeyHash, uniquenessKeyHash, platform, architecture,
            Device.NormalizeRequired(version, 64, nameof(version)), channel,
            Device.NormalizeRequired(source, 64, nameof(source)), Normalize(campaign, 128),
            NormalizeCountry(countryCode), Normalize(userAgentFamily, 64), now);
    }

    public void Complete(DownloadResult result, DateTimeOffset completedAtUtc)
    {
        if (result == DownloadResult.Started) throw new ArgumentException("A terminal result is required.", nameof(result));
        if (CompletedAtUtc is not null) return;
        if (completedAtUtc < StartedAtUtc) throw new ArgumentOutOfRangeException(nameof(completedAtUtc));
        Result = result;
        CompletedAtUtc = completedAtUtc;
        ConcurrencyVersion++;
    }

    private static string? Normalize(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsAsciiLetter) ? normalized : null;
    }
}
