using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Abstractions;

public interface IInstallationService
{
    Task<InstallationRegistrationResultV1> RegisterAsync(DeviceAccessPrincipal caller, InstallationRegistrationRequestV1 request, CancellationToken cancellationToken = default);
    Task<InstallationHeartbeatResultV1> HeartbeatAsync(InstallationHeartbeatRequestV1 request, CancellationToken cancellationToken = default);
    Task UpdateVersionAsync(InstallationVersionRequestV1 request, CancellationToken cancellationToken = default);
    Task UnregisterAsync(InstallationUnregisterRequestV1 request, CancellationToken cancellationToken = default);
}

public interface ISessionTelemetryService
{
    Task RecordClientLifecycleAsync(DeviceAccessPrincipal caller, ClientSessionLifecycleEventV1 request, CancellationToken cancellationToken = default);
    Task<SessionHeartbeatResultV1> HeartbeatAsync(DeviceAccessPrincipal caller, ClientSessionHeartbeatV1 request, CancellationToken cancellationToken = default);
    Task StartAsync(SessionStartedEventV1 request, CancellationToken cancellationToken = default);
    Task MarkConnectedAsync(SessionConnectedEventV1 request, CancellationToken cancellationToken = default);
    Task EndAsync(SessionEndedEventV1 request, CancellationToken cancellationToken = default);
    Task<int> ReconcileStaleAsync(DateTimeOffset negotiationBeforeUtc, DateTimeOffset connectedBeforeUtc, int maximumCount, CancellationToken cancellationToken = default);
}

public interface IDownloadTrackingService
{
    Task<DownloadStartResultV1> StartAsync(DownloadStartRequestV1 request, string? privacyScopedUniquenessValue, CancellationToken cancellationToken = default);
    Task CompleteAsync(DownloadCompleteRequestV1 request, CancellationToken cancellationToken = default);
    Task<DownloadArtifact?> ResolveLatestAsync(PlatformKindV1 platform, ArchitectureKindV1 architecture, InstallChannelV1 channel, CancellationToken cancellationToken = default);
    Task<DownloadArtifact?> ResolveVersionAsync(PlatformKindV1 platform, ArchitectureKindV1 architecture, string version, CancellationToken cancellationToken = default);
}

public interface IReleaseTelemetryService
{
    Task RecordClientUpdateEventAsync(DeviceAccessPrincipal caller, ClientUpdateEventV1 request, CancellationToken cancellationToken = default);
    Task RecordUpdateEventAsync(UpdateEventRequestV1 request, CancellationToken cancellationToken = default);
}

public interface IDiagnosticsService
{
    Task<DiagnosticCreateResultV1> CreateRequestAsync(DeviceAccessPrincipal caller, DiagnosticCreateRequestV1 request, CancellationToken cancellationToken = default);
    Task<DiagnosticStatusResponseV1> CompleteUploadAsync(DeviceAccessPrincipal caller, DiagnosticUploadCompleteRequestV1 request, CancellationToken cancellationToken = default);
    Task<DiagnosticStatusResponseV1> GetStatusAsync(DeviceAccessPrincipal caller, Guid diagnosticId, CancellationToken cancellationToken = default);
}

public interface IAdminQueryService
{
    Task<AdminOverviewV1> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<AdminVersionDistributionsV1> GetVersionDistributionsAsync(int top = 10, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminDeviceRowV1>> GetDevicesAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminInstallationRowV1>> GetInstallationsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminPresenceRowV1>> GetPresenceAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminSessionRowV1>> GetSessionsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminDownloadRowV1>> GetDownloadsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminReleaseRowV1>> GetReleasesAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminDiagnosticRowV1>> GetDiagnosticsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminInfrastructureRowV1>> GetInfrastructureAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminAuditRowV1>> GetAuditAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
    Task<PageResultV1<AdminAlertRowV1>> GetAlertsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default);
}
