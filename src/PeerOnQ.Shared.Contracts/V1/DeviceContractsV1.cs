namespace PeerOnQ.Shared.Contracts.V1;

public sealed record DeviceRegistrationRequestV1(
    Guid InstallationId,
    string DisplayName,
    string PublicKeySpkiBase64,
    PlatformKindV1 Platform,
    ArchitectureKindV1 Architecture,
    string AppVersion,
    string OsVersion,
    InstallChannelV1 InstallChannel,
    string Region,
    string ProtocolVersion);

public sealed record DeviceRegistrationChallengeV1(
    string ChallengeId,
    string CanonicalPayload,
    DateTimeOffset ExpiresAtUtc);

public sealed record DeviceAuthenticationRequestV1(
    string ChallengeId,
    string SignatureBase64,
    string PublicKeySpkiBase64,
    Guid InstallationId);

public sealed record DeviceAuthenticationResultV1(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    Guid DeviceId,
    Guid InstallationId,
    string PublicDeviceId,
    string SignalingAttestation,
    DateTimeOffset SignalingAttestationExpiresAtUtc);

public sealed record InstallationRegistrationRequestV1(
    Guid InstallationId,
    PlatformKindV1 Platform,
    ArchitectureKindV1 Architecture,
    string AppVersion,
    string OsVersion,
    InstallChannelV1 InstallChannel,
    string ProtocolVersion,
    string Region);

public sealed record InstallationRegistrationResultV1(
    Guid InstallationId,
    DateTimeOffset RegisteredAtUtc,
    bool IsNew,
    bool IsBlocked,
    string? MinimumSupportedVersion);

public sealed record InstallationHeartbeatRequestV1(
    Guid InstallationId,
    PresenceStateV1 State,
    string AppVersion,
    string Region,
    DateTimeOffset SentAtUtc);

public sealed record InstallationHeartbeatResultV1(
    DateTimeOffset ServerTimeUtc,
    int NextHeartbeatSeconds,
    bool IsBlocked,
    string? MinimumSupportedVersion);

public sealed record InstallationVersionRequestV1(
    Guid InstallationId,
    string AppVersion,
    string OsVersion,
    ArchitectureKindV1 Architecture,
    InstallChannelV1 InstallChannel);

public sealed record InstallationUnregisterRequestV1(Guid InstallationId, string Reason);

public sealed record PresenceHeartbeatV1(
    PresenceStateV1 State,
    string AppVersion,
    string Region,
    DateTimeOffset SentAtUtc);

public sealed record PresenceHeartbeatResultV1(
    DateTimeOffset ServerTimeUtc,
    int NextHeartbeatSeconds,
    DateTimeOffset LeaseExpiresAtUtc);

public sealed record PresenceConnectionAcceptedV1(
    DateTimeOffset ServerTimeUtc,
    int HeartbeatIntervalSeconds,
    string Region);

public sealed record PresenceSessionStateRequestV1(PresenceStateV1 State);
