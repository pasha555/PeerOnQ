using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Services;

public sealed class AdminQueryService(
    IAnalyticsQueryRepository queries,
    IPresenceLeaseStore presence,
    TimeProvider? timeProvider = null) : IAdminQueryService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<AdminOverviewV1> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        return await queries.GetOverviewAsync(now, await presence.CountActiveDevicesAsync(now, cancellationToken), cancellationToken);
    }

    public Task<AdminVersionDistributionsV1> GetVersionDistributionsAsync(int top = 10, CancellationToken cancellationToken = default)
    {
        if (top is < 1 or > 25)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Distribution size must be between 1 and 25.");
        return queries.GetVersionDistributionsAsync(_time.GetUtcNow(), top, cancellationToken);
    }

    public Task<PageResultV1<AdminDeviceRowV1>> GetDevicesAsync(AdminQueryV1 query, CancellationToken cancellationToken = default) => queries.QueryDevicesAsync(Validate(query), cancellationToken);
    public Task<PageResultV1<AdminInstallationRowV1>> GetInstallationsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default) => queries.QueryInstallationsAsync(Validate(query), cancellationToken);
    public Task<PageResultV1<AdminSessionRowV1>> GetSessionsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default) => queries.QuerySessionsAsync(Validate(query), cancellationToken);
    public Task<PageResultV1<AdminDownloadRowV1>> GetDownloadsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default) => queries.QueryDownloadsAsync(Validate(query), cancellationToken);
    public Task<PageResultV1<AdminReleaseRowV1>> GetReleasesAsync(AdminQueryV1 query, CancellationToken cancellationToken = default) => queries.QueryReleasesAsync(Validate(query), cancellationToken);
    public Task<PageResultV1<AdminDiagnosticRowV1>> GetDiagnosticsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default) => queries.QueryDiagnosticsAsync(Validate(query), cancellationToken);
    public Task<PageResultV1<AdminInfrastructureRowV1>> GetInfrastructureAsync(AdminQueryV1 query, CancellationToken cancellationToken = default) => queries.QueryInfrastructureAsync(Validate(query), cancellationToken);
    public Task<PageResultV1<AdminAuditRowV1>> GetAuditAsync(AdminQueryV1 query, CancellationToken cancellationToken = default) => queries.QueryAuditAsync(Validate(query), cancellationToken);
    public Task<PageResultV1<AdminAlertRowV1>> GetAlertsAsync(AdminQueryV1 query, CancellationToken cancellationToken = default) => queries.QueryAlertsAsync(Validate(query), cancellationToken);

    public async Task<PageResultV1<AdminPresenceRowV1>> GetPresenceAsync(AdminQueryV1 query, CancellationToken cancellationToken = default)
    {
        query = Validate(query);
        if (!string.IsNullOrWhiteSpace(query.Search) || query.FromUtc is not null || query.ToUtc is not null ||
            (query.SortBy is not null && !query.SortBy.Equals("expiresAt", StringComparison.OrdinalIgnoreCase)))
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest,
                "Presence supports pagination and lease-expiry sorting only.");
        var now = _time.GetUtcNow();
        var leases = await presence.QueryActiveAsync(now, query.Offset, query.Limit, query.Descending, cancellationToken);
        var total = await presence.CountActiveAsync(now, cancellationToken);
        return new PageResultV1<AdminPresenceRowV1>(leases.Select(lease => new AdminPresenceRowV1(
            lease.InstallationId, lease.DeviceId, Enum.Parse<PresenceStateV1>(lease.State.ToString()),
            lease.Region, lease.AppVersion, lease.ExpiresAtUtc)).ToArray(), total, query.Offset, query.Limit);
    }

    private static AdminQueryV1 Validate(AdminQueryV1 query)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 500)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Pagination is outside the allowed range.");
        if (query.FromUtc > query.ToUtc)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Date range is invalid.");
        if (query.Search?.Length > 128 || query.SortBy?.Length > 64)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Query text is too long.");
        return query;
    }
}
