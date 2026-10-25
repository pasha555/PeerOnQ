namespace PeerOnQ.Shared.Contracts.V1;

public sealed record SessionStartedEventV1(
    Guid SessionId,
    Guid ViewerDeviceId,
    Guid HostDeviceId,
    PermissionModeV1 PermissionMode,
    string ServerRegion,
    string ClientVersionViewer,
    string ClientVersionHost,
    DateTimeOffset StartedAtUtc);

public sealed record SessionConnectedEventV1(
    Guid SessionId,
    ConnectionPathV1 ConnectionPath,
    bool UsedTurn,
    DateTimeOffset ConnectedAtUtc);

public sealed record SessionEndedEventV1(
    Guid SessionId,
    SessionEndReasonV1 EndReason,
    string? FailureStage,
    string? FailureCode,
    int ReconnectCount,
    DateTimeOffset EndedAtUtc);

public sealed record ClientSessionLifecycleEventV1(
    Guid EventId,
    Guid SessionId,
    SessionEventKindV1 Kind,
    SessionParticipantRoleV1 Role,
    string PeerPublicDeviceId,
    PermissionModeV1 PermissionMode,
    ConnectionPathV1 ConnectionPath,
    string ServerRegion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset OccurredAtUtc,
    SessionEndReasonV1? EndReason,
    string? FailureStage,
    string? FailureCode,
    bool UsedTurn,
    int ReconnectCount,
    string ClientVersion);

public sealed record ClientSessionHeartbeatV1(
    Guid EventId,
    Guid SessionId,
    DateTimeOffset SentAtUtc);

public sealed record SessionHeartbeatResultV1(
    Guid SessionId,
    DateTimeOffset LastActivityAtUtc,
    bool IsDuplicate);

public sealed record DownloadStartRequestV1(
    Guid DownloadId,
    PlatformKindV1 Platform,
    ArchitectureKindV1 Architecture,
    string Version,
    InstallChannelV1 Channel,
    string Source,
    string? Campaign,
    string? CountryCode,
    string? UserAgentFamily,
    string IdempotencyKey);

public sealed record DownloadStartResultV1(Guid DownloadId, DateTimeOffset StartedAtUtc, bool IsDuplicate);

public sealed record DownloadCompleteRequestV1(
    Guid DownloadId,
    DownloadResultV1 Result,
    DateTimeOffset CompletedAtUtc);

public sealed record ReleaseV1(
    Guid ReleaseId,
    string Version,
    InstallChannelV1 Channel,
    ArchitectureKindV1 Architecture,
    DateTimeOffset PublishedAtUtc,
    string MinimumSupportedVersion,
    string SecurityFloorVersion,
    bool IsActive,
    int RolloutPercentage,
    string SignedManifestDigest);

public sealed record UpdateEventRequestV1(
    Guid EventId,
    Guid InstallationId,
    Guid ReleaseId,
    UpdateEventKindV1 Kind,
    string? FailureCode,
    DateTimeOffset OccurredAtUtc);

public sealed record ClientUpdateEventV1(
    Guid EventId,
    UpdateEventKindV1 Kind,
    string Version,
    InstallChannelV1 Channel,
    ArchitectureKindV1 Architecture,
    string? FailureCode,
    DateTimeOffset OccurredAtUtc);
