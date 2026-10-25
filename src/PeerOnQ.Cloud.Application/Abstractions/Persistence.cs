using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Abstractions;

public interface IDeviceRepository
{
    Task<Device?> FindByPublicIdHashAsync(byte[] publicDeviceIdHash, CancellationToken cancellationToken);
    Task<Device?> FindByIdentityFingerprintAsync(string fingerprint, CancellationToken cancellationToken);
    Task<Device?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(Device device);
}

public interface IInstallationRepository
{
    Task<Installation?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(Installation installation);
}

public interface ISessionRepository
{
    Task<RemoteSession?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(RemoteSession session);
    void AddFailure(SessionFailure failure);
    Task<IReadOnlyList<RemoteSession>> FindStaleActiveAsync(DateTimeOffset negotiationBeforeUtc, DateTimeOffset connectedBeforeUtc, int maximumCount, CancellationToken cancellationToken);
}

public interface IDownloadRepository
{
    Task<DownloadEvent?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<DownloadEvent?> FindByIdempotencyHashAsync(byte[] hash, CancellationToken cancellationToken);
    void Add(DownloadEvent download);
}

public sealed record DownloadArtifact(
    string Version,
    PlatformKind Platform,
    ArchitectureKind Architecture,
    InstallChannel Channel,
    Uri ArtifactUri,
    string Sha256,
    string SignedManifestDigest,
    long SizeBytes);

public interface IDownloadArtifactRepository
{
    Task<DownloadArtifact?> ResolveLatestAsync(PlatformKind platform, ArchitectureKind architecture, InstallChannel channel, CancellationToken cancellationToken);
    Task<DownloadArtifact?> ResolveVersionAsync(PlatformKind platform, ArchitectureKind architecture, string version, CancellationToken cancellationToken);
}

public interface IReleaseRepository
{
    Task<AppRelease?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<AppRelease?> FindByVersionAsync(string version, InstallChannel channel, ArchitectureKind architecture, CancellationToken cancellationToken);
    Task<string?> FindMinimumSupportedVersionAsync(InstallChannel channel, ArchitectureKind architecture, CancellationToken cancellationToken);
    Task<bool> UpdateEventExistsAsync(Guid eventId, CancellationToken cancellationToken);
    void AddUpdateEvent(UpdateEvent updateEvent);
}

public interface IDiagnosticRepository
{
    Task<DiagnosticBundle?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    void Add(DiagnosticBundle bundle);
}

public interface IAuditRepository
{
    void Add(AuditEvent auditEvent);
}

public interface ICloudUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IRetentionRepository
{
    Task<int> DeleteExpiredPresenceHistoryAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken);
    Task<int> AnonymizeExpiredDownloadEventsAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken);
    Task<int> DeleteExpiredSessionMetadataAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiagnosticExpirationClaim>> ClaimExpiredDiagnosticsAsync(DateTimeOffset nowUtc, int batchSize, CancellationToken cancellationToken);
    Task CompleteDiagnosticExpirationAsync(Guid diagnosticId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task<int> DeleteExpiredServiceHealthAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken);
    Task<int> DeleteExpiredAdminSessionsAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken);
    Task<RetentionBatchResult> ApplyRetentionBatchAsync(RetentionBatchPolicy policy, DateTimeOffset nowUtc, int batchSize, CancellationToken cancellationToken);
}

public sealed record DiagnosticExpirationClaim(Guid DiagnosticId, string? StorageObjectKey);
public sealed record RetentionBatchPolicy(RetentionRecordKind RecordType, string Version, TimeSpan RetentionPeriod, bool Enabled, bool LegalHold);
public sealed record RetentionBatchResult(RetentionRecordKind RecordType, Guid? EvidenceId, int DeletedCount, bool Skipped, string? SkipReason);

public interface IDiagnosticBlobLifecycleStore
{
    Task DeleteAsync(Guid diagnosticId, string? storageObjectKey, CancellationToken cancellationToken);
}

public interface IAnalyticsQueryRepository
{
    Task<AdminOverviewV1> GetOverviewAsync(DateTimeOffset nowUtc, long onlineDevices, CancellationToken cancellationToken);
    Task<AdminVersionDistributionsV1> GetVersionDistributionsAsync(DateTimeOffset nowUtc, int top, CancellationToken cancellationToken);
    Task<PageResultV1<AdminDeviceRowV1>> QueryDevicesAsync(AdminQueryV1 query, CancellationToken cancellationToken);
    Task<PageResultV1<AdminInstallationRowV1>> QueryInstallationsAsync(AdminQueryV1 query, CancellationToken cancellationToken);
    Task<PageResultV1<AdminSessionRowV1>> QuerySessionsAsync(AdminQueryV1 query, CancellationToken cancellationToken);
    Task<PageResultV1<AdminDownloadRowV1>> QueryDownloadsAsync(AdminQueryV1 query, CancellationToken cancellationToken);
    Task<PageResultV1<AdminReleaseRowV1>> QueryReleasesAsync(AdminQueryV1 query, CancellationToken cancellationToken);
    Task<PageResultV1<AdminDiagnosticRowV1>> QueryDiagnosticsAsync(AdminQueryV1 query, CancellationToken cancellationToken);
    Task<PageResultV1<AdminInfrastructureRowV1>> QueryInfrastructureAsync(AdminQueryV1 query, CancellationToken cancellationToken);
    Task<PageResultV1<AdminAuditRowV1>> QueryAuditAsync(AdminQueryV1 query, CancellationToken cancellationToken);
    Task<PageResultV1<AdminAlertRowV1>> QueryAlertsAsync(AdminQueryV1 query, CancellationToken cancellationToken);
}

public interface IAdminIdentityRepository
{
    Task<AdminUser?> FindUserByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<AdminUser?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlySet<AdminRoleKind>> GetRolesAsync(Guid userId, CancellationToken cancellationToken);
    Task<AdminSession?> FindSessionByRefreshTokenHashAsync(byte[] refreshTokenHash, CancellationToken cancellationToken);
    void AddSession(AdminSession session);
    void AddRecoveryCodes(IEnumerable<AdminRecoveryCode> recoveryCodes);
    Task<AdminRecoveryCode?> FindUnusedRecoveryCodeAsync(Guid userId, byte[] codeHash, CancellationToken cancellationToken);
}
