using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Abstractions;

public sealed record DeviceChallengeRecord(
    string ChallengeId,
    Guid InstallationId,
    string IdentityFingerprint,
    string DisplayName,
    PlatformKindV1 Platform,
    ArchitectureKindV1 Architecture,
    string AppVersion,
    string OsVersion,
    InstallChannelV1 InstallChannel,
    string Region,
    string ProtocolVersion,
    string CanonicalPayload,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public interface IDeviceChallengeStore
{
    Task StoreAsync(DeviceChallengeRecord challenge, CancellationToken cancellationToken);
    Task<DeviceChallengeRecord?> ConsumeAsync(string challengeId, CancellationToken cancellationToken);
}

public interface IPublicDeviceIdService
{
    int ActiveKeyVersion { get; }
    string Normalize(string publicDeviceId);
    byte[] ComputeLookupHash(string publicDeviceId);
    IReadOnlyList<byte[]> ComputeLookupHashes(string publicDeviceId);
    string Mask(string publicDeviceId);
    ServerAssignedPublicDeviceId DeriveFromFingerprint(
        string identityFingerprint,
        int collisionCounter,
        int? keyVersion = null);
}

public sealed record ServerAssignedPublicDeviceId(
    string Value,
    byte[] LookupHash,
    string MaskedValue,
    int CollisionCounter,
    int KeyVersion);

public interface IPrivacyHasher
{
    byte[] ComputeHash(string purpose, string value);
}

public sealed record DeviceProofResult(bool IsValid, string? Fingerprint);
public interface IDeviceProofVerifier
{
    string? GetFingerprint(string publicKeySpkiBase64);
    DeviceProofResult Verify(string publicKeySpkiBase64, string canonicalPayload, string signatureBase64);
}

public sealed record DeviceBootstrapRequest(
    Guid InstallationId,
    string IdentityFingerprint,
    string DisplayName,
    PlatformKind Platform,
    ArchitectureKind Architecture,
    string AppVersion,
    string OsVersion,
    InstallChannel InstallChannel,
    string ProtocolVersion,
    string Region,
    DateTimeOffset AuthenticatedAtUtc);

public sealed record DeviceBootstrapResult(
    Guid DeviceId,
    Guid InstallationId,
    string PublicDeviceId,
    bool DeviceCreated,
    bool InstallationCreated,
    ManagedDevicePolicyAttestation? ManagedPolicy = null);

public sealed record ManagedDevicePolicyAttestation(
    Guid OrganizationId,
    bool ViewOnlyAllowed,
    bool FullControlAllowed,
    bool FileTransferAllowed,
    bool UnattendedAccessAllowed,
    bool ClipboardAllowed,
    bool HybridSecurityRequired,
    string MinimumClientVersion,
    string ApprovedRelayRegionsCsv);

public interface IDeviceBootstrapStore
{
    Task<DeviceBootstrapResult> CompleteAsync(DeviceBootstrapRequest request, CancellationToken cancellationToken);
}

public sealed record DeviceAccessToken(string Token, DateTimeOffset ExpiresAtUtc);
public sealed record DeviceAccessPrincipal(Guid DeviceId, Guid InstallationId, DateTimeOffset ExpiresAtUtc);
public interface IDeviceAccessTokenValidator
{
    Task<DeviceAccessPrincipal?> ValidateAsync(string token, CancellationToken cancellationToken);
}

public interface IDeviceAccessTokenIssuer : IDeviceAccessTokenValidator
{
    Task<DeviceAccessToken> IssueAsync(Guid deviceId, Guid installationId, TimeSpan lifetime, CancellationToken cancellationToken);
    Task RevokeAsync(string token, CancellationToken cancellationToken);
}

public sealed record SignalingAttestationIssueRequest(
    string PublicDeviceId,
    string SpkiSha256,
    Guid DeviceId,
    Guid InstallationId,
    ManagedDevicePolicyAttestation? ManagedPolicy = null);

public sealed record IssuedSignalingAttestation(string Token, DateTimeOffset ExpiresAtUtc);

public interface ISignalingAttestationIssuer
{
    IssuedSignalingAttestation Issue(SignalingAttestationIssueRequest request);
}

public interface IDeviceAccessStateValidator
{
    Task<bool> IsActiveAsync(DeviceAccessPrincipal principal, CancellationToken cancellationToken);
}

public sealed record PresenceLease(
    Guid InstallationId,
    Guid DeviceId,
    string ConnectionId,
    string ServerId,
    PresenceState State,
    string AppVersion,
    string Region,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset LastHeartbeatAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record PresenceLeaseRequest(
    Guid InstallationId,
    Guid DeviceId,
    string ConnectionId,
    string ServerId,
    PresenceState State,
    string AppVersion,
    string Region,
    DateTimeOffset NowUtc,
    TimeSpan LeaseDuration);

public sealed record PresenceLeaseAcquireResult(
    PresenceLease Lease,
    string? DisplacedConnectionId,
    string? DisplacedServerId,
    PresenceLease? DisplacedLease);

public interface IPresenceLeaseStore
{
    Task<PresenceLeaseAcquireResult> AcquireAsync(PresenceLeaseRequest request, CancellationToken cancellationToken);
    Task<PresenceLease?> RefreshAsync(Guid installationId, string connectionId, PresenceState state, DateTimeOffset nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<PresenceLease?> ReleaseAsync(Guid installationId, string connectionId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<PresenceLease?> GetAsync(Guid installationId, CancellationToken cancellationToken);
    Task<long> CountActiveAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<long> CountActiveDevicesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceLease>> QueryActiveAsync(DateTimeOffset nowUtc, int offset, int limit, bool descending, CancellationToken cancellationToken);
    Task<IReadOnlyList<PresenceLease>> CollectExpiredAsync(DateTimeOffset nowUtc, int maximumCount, CancellationToken cancellationToken);
}

public interface IPresenceHistoryWriter
{
    Task RecordEndedLeaseAsync(PresenceLease lease, DateTimeOffset endedAtUtc, CancellationToken cancellationToken);
}

public interface IDeviceRegistrationService
{
    Task<DeviceRegistrationChallengeV1> IssueChallengeAsync(DeviceRegistrationRequestV1 request, CancellationToken cancellationToken = default);
    Task<DeviceAuthenticationResultV1> AuthenticateAsync(DeviceAuthenticationRequestV1 request, CancellationToken cancellationToken = default);
}
