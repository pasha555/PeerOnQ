namespace PeerOnQ.Shared.Contracts.V1;

public sealed record DiagnosticCreateRequestV1(
    Guid InstallationId,
    bool ConsentGranted,
    IReadOnlyList<string> IncludedCategories,
    string AppVersion,
    string OsVersion,
    ArchitectureKindV1 Architecture,
    string? ErrorId,
    string? IssueCategory,
    int DatabaseSchemaVersion);

public sealed record DiagnosticCreateResultV1(
    Guid DiagnosticId,
    string UploadToken,
    DateTimeOffset UploadTokenExpiresAtUtc,
    DateTimeOffset DiagnosticExpiresAtUtc);

public sealed record DiagnosticUploadCompleteRequestV1(
    Guid DiagnosticId,
    string UploadToken,
    long SanitizedArchiveSizeBytes,
    string Sha256Base64);

public sealed record DiagnosticStatusResponseV1(
    Guid DiagnosticId,
    DiagnosticStatusV1 Status,
    bool ConsentGranted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? ReferenceCode);

public sealed record AdminOverviewV1(
    long TotalDownloads,
    long CompletedDownloads,
    long UniqueDownloadEstimate,
    long TotalInstallations,
    long ActiveInstallations,
    long OnlineDevices,
    long ActiveDevicesToday,
    long ActiveDevicesThisMonth,
    long ActiveSessions,
    long FailedConnectionAttempts,
    long TurnSessions,
    decimal? CrashRate,
    decimal? SessionSuccessRate,
    long UpdateFailures,
    string? CurrentStableVersion,
    DateTimeOffset GeneratedAtUtc);

public sealed record DistributionBucketV1(string Label, long Count, decimal Percentage);

public sealed record AdminVersionDistributionsV1(
    IReadOnlyList<DistributionBucketV1> ClientVersions,
    IReadOnlyList<DistributionBucketV1> WindowsVersions,
    long ActiveInstallations,
    long ActiveWindowsInstallations,
    DateTimeOffset ActiveSinceUtc,
    DateTimeOffset GeneratedAtUtc);

public sealed record PageRequestV1(int Offset = 0, int Limit = 50, string? SortBy = null, bool Descending = true);
public sealed record PageResultV1<T>(IReadOnlyList<T> Items, long Total, int Offset, int Limit);

public sealed record AdminQueryV1(
    int Offset = 0,
    int Limit = 50,
    string? Search = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? SortBy = null,
    bool Descending = true);

public sealed record AdminDeviceRowV1(Guid DeviceId, string MaskedPublicDeviceId, string DisplayName, string IdentityFingerprintPrefix, DateTimeOffset CreatedAtUtc, DateTimeOffset LastSeenAtUtc, bool IsRevoked);
public sealed record AdminInstallationRowV1(Guid InstallationId, Guid? DeviceId, PlatformKindV1 Platform, ArchitectureKindV1 Architecture, string AppVersion, string OsVersion, InstallChannelV1 Channel, DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc, DateTimeOffset? LastOnlineAtUtc, string Region, bool IsBlocked);
public sealed record AdminPresenceRowV1(Guid InstallationId, Guid DeviceId, PresenceStateV1 State, string Region, string AppVersion, DateTimeOffset LeaseExpiresAtUtc);
public sealed record AdminSessionRowV1(Guid SessionId, string MaskedViewer, string MaskedHost, PermissionModeV1 PermissionMode, ConnectionPathV1 ConnectionPath, string Region, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc, string Result, string? FailureStage, int ReconnectCount, bool UsedTurn);
public sealed record AdminDownloadRowV1(Guid DownloadId, PlatformKindV1 Platform, ArchitectureKindV1 Architecture, string Version, InstallChannelV1 Channel, string Source, string? Campaign, string? CountryCode, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc, DownloadResultV1 Result);
public sealed record AdminReleaseRowV1(Guid ReleaseId, string Version, InstallChannelV1 Channel, ArchitectureKindV1 Architecture, DateTimeOffset PublishedAtUtc, int RolloutPercentage, bool IsActive, long Offered, long Installed, long Failed, long RolledBack);
public sealed record AdminDiagnosticRowV1(Guid DiagnosticId, Guid InstallationId, DiagnosticStatusV1 Status, string AppVersion, string? ErrorId, string? IssueCategory, bool ConsentGranted, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc);
public sealed record AdminInfrastructureRowV1(string Region, string Service, string State, double LatencyMilliseconds, DateTimeOffset ObservedAtUtc);
public sealed record AdminAuditRowV1(Guid AuditEventId, string ActorId, string ActorType, string Action, string TargetType, string? TargetId, string Result, DateTimeOffset TimestampUtc, string CorrelationId, string? Reason);
public sealed record AdminAlertRowV1(Guid AlertId, string RuleName, string Severity, string Region, string Summary, string RunbookUrl, DateTimeOffset StartedAtUtc, DateTimeOffset? ResolvedAtUtc);

public sealed record AdminBlockRequestV1(bool Blocked, string Reason);
public sealed record AdminRolloutRequestV1(int RolloutPercentage, string Reason);
public sealed record AdminReleaseManifestRequestV1(string SignedManifest, string Reason);
public sealed record AdminReleasePublicationV1(
    Guid ReleaseId,
    string Version,
    InstallChannelV1 Channel,
    ArchitectureKindV1 Architecture,
    int RolloutPercentage,
    DateTimeOffset PublishedAtUtc);
