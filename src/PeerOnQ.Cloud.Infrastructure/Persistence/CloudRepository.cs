using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Infrastructure.Persistence;

public sealed class CloudRepository(CloudDbContext db, IHostEnvironment environment) :
    IDeviceRepository,
    IInstallationRepository,
    ISessionRepository,
    IDownloadRepository,
    IDownloadArtifactRepository,
    IReleaseRepository,
    IDiagnosticRepository,
    IAuditRepository,
    IAdminIdentityRepository,
    IRetentionRepository,
    IAnalyticsQueryRepository
{
    Task<Device?> IDeviceRepository.FindByPublicIdHashAsync(byte[] hash, CancellationToken cancellationToken) =>
        db.Devices.SingleOrDefaultAsync(value => value.PublicDeviceIdHash == hash, cancellationToken);

    Task<Device?> IDeviceRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Devices.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);

    Task<Device?> IDeviceRepository.FindByIdentityFingerprintAsync(string fingerprint, CancellationToken cancellationToken) =>
        db.Devices.SingleOrDefaultAsync(value => value.IdentityFingerprint == fingerprint, cancellationToken);

    void IDeviceRepository.Add(Device device) => db.Devices.Add(device);

    Task<Installation?> IInstallationRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Installations.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);

    void IInstallationRepository.Add(Installation installation) => db.Installations.Add(installation);

    Task<RemoteSession?> ISessionRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.RemoteSessions.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);

    void ISessionRepository.Add(RemoteSession session) => db.RemoteSessions.Add(session);
    void ISessionRepository.AddFailure(SessionFailure failure) => db.SessionFailures.Add(failure);

    async Task<IReadOnlyList<RemoteSession>> ISessionRepository.FindStaleActiveAsync(
        DateTimeOffset negotiationBeforeUtc,
        DateTimeOffset connectedBeforeUtc,
        int maximumCount,
        CancellationToken cancellationToken) =>
        await db.RemoteSessions
            .Where(value => value.EndedAtUtc == null &&
                ((value.Lifecycle == SessionLifecycle.Starting && value.LastActivityAtUtc < negotiationBeforeUtc) ||
                 (value.Lifecycle == SessionLifecycle.Connected && value.LastActivityAtUtc < connectedBeforeUtc)))
            .OrderBy(value => value.LastActivityAtUtc)
            .Take(maximumCount)
            .ToListAsync(cancellationToken);

    Task<DownloadEvent?> IDownloadRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.DownloadEvents.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);

    Task<DownloadEvent?> IDownloadRepository.FindByIdempotencyHashAsync(byte[] hash, CancellationToken cancellationToken) =>
        db.DownloadEvents.SingleOrDefaultAsync(value => value.IdempotencyKeyHash == hash, cancellationToken);

    void IDownloadRepository.Add(DownloadEvent download) => db.DownloadEvents.Add(download);

    async Task<DownloadArtifact?> IDownloadArtifactRepository.ResolveLatestAsync(PlatformKind platform, ArchitectureKind architecture, InstallChannel channel, CancellationToken cancellationToken)
    {
        if (platform != PlatformKind.Windows) return null;
        var release = await db.AppReleases.AsNoTracking()
            .Where(value => value.Architecture == architecture && value.Channel == channel && value.IsActive)
            .OrderByDescending(value => value.PublishedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return release is null ? null : ToArtifact(release, platform);
    }

    async Task<DownloadArtifact?> IDownloadArtifactRepository.ResolveVersionAsync(PlatformKind platform, ArchitectureKind architecture, string version, CancellationToken cancellationToken)
    {
        if (platform != PlatformKind.Windows) return null;
        var release = await db.AppReleases.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Architecture == architecture && value.Version == version && value.Channel == InstallChannel.Stable && value.IsActive, cancellationToken);
        return release is null ? null : ToArtifact(release, platform);
    }

    Task<AppRelease?> IReleaseRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.AppReleases.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);

    Task<AppRelease?> IReleaseRepository.FindByVersionAsync(string version, InstallChannel channel, ArchitectureKind architecture, CancellationToken cancellationToken) =>
        db.AppReleases.SingleOrDefaultAsync(value => value.Version == version && value.Channel == channel && value.Architecture == architecture, cancellationToken);

    Task<string?> IReleaseRepository.FindMinimumSupportedVersionAsync(InstallChannel channel, ArchitectureKind architecture, CancellationToken cancellationToken) =>
        db.AppReleases.AsNoTracking()
            .Where(value => value.Channel == channel && value.Architecture == architecture && value.IsActive)
            .OrderByDescending(value => value.PublishedAtUtc)
            .Select(value => value.MinimumSupportedVersion)
            .FirstOrDefaultAsync(cancellationToken);

    void IReleaseRepository.AddUpdateEvent(UpdateEvent updateEvent) => db.UpdateEvents.Add(updateEvent);

    Task<bool> IReleaseRepository.UpdateEventExistsAsync(Guid eventId, CancellationToken cancellationToken) =>
        db.UpdateEvents.AnyAsync(value => value.Id == eventId, cancellationToken);

    Task<DiagnosticBundle?> IDiagnosticRepository.FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.DiagnosticBundles.SingleOrDefaultAsync(value => value.Id == id, cancellationToken);

    void IDiagnosticRepository.Add(DiagnosticBundle bundle) => db.DiagnosticBundles.Add(bundle);
    void IAuditRepository.Add(AuditEvent auditEvent) => db.AuditEvents.Add(auditEvent);

    Task<AdminUser?> IAdminIdentityRepository.FindUserByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        db.AdminUsers.SingleOrDefaultAsync(value => value.Email == normalizedEmail, cancellationToken);

    Task<AdminUser?> IAdminIdentityRepository.FindUserByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        db.AdminUsers.SingleOrDefaultAsync(value => value.Id == userId, cancellationToken);

    async Task<IReadOnlySet<AdminRoleKind>> IAdminIdentityRepository.GetRolesAsync(Guid userId, CancellationToken cancellationToken) =>
        (await (from userRole in db.AdminUserRoles.AsNoTracking()
                join role in db.AdminRoles.AsNoTracking() on userRole.AdminRoleId equals role.Id
                where userRole.AdminUserId == userId
                select role.Name).ToListAsync(cancellationToken)).ToHashSet();

    Task<AdminSession?> IAdminIdentityRepository.FindSessionByRefreshTokenHashAsync(byte[] refreshTokenHash, CancellationToken cancellationToken) =>
        db.AdminSessions.SingleOrDefaultAsync(value => value.RefreshTokenHash == refreshTokenHash, cancellationToken);

    void IAdminIdentityRepository.AddSession(AdminSession session) => db.AdminSessions.Add(session);
    void IAdminIdentityRepository.AddRecoveryCodes(IEnumerable<AdminRecoveryCode> recoveryCodes) => db.AdminRecoveryCodes.AddRange(recoveryCodes);

    Task<AdminRecoveryCode?> IAdminIdentityRepository.FindUnusedRecoveryCodeAsync(Guid userId, byte[] codeHash, CancellationToken cancellationToken) =>
        db.AdminRecoveryCodes.SingleOrDefaultAsync(value => value.AdminUserId == userId && value.CodeHash == codeHash && value.UsedAtUtc == null, cancellationToken);

    Task<int> IRetentionRepository.DeleteExpiredPresenceHistoryAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken) =>
        DeleteBatchAsync(db.DevicePresenceHistory.Where(value => value.IntervalEndedAtUtc < cutoffUtc), value => value.IntervalEndedAtUtc, batchSize, cancellationToken);

    async Task<int> IRetentionRepository.AnonymizeExpiredDownloadEventsAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken)
    {
        var ids = await db.DownloadEvents.Where(value => value.StartedAtUtc < cutoffUtc && value.UniquenessKeyHash != null)
            .OrderBy(value => value.StartedAtUtc).Select(value => value.Id).Take(batchSize).ToListAsync(cancellationToken);
        return ids.Count == 0 ? 0 : await db.DownloadEvents.Where(value => ids.Contains(value.Id)).ExecuteUpdateAsync(
            setters => setters.SetProperty(value => value.UniquenessKeyHash, (byte[]?)null)
                .SetProperty(value => value.Campaign, (string?)null)
                .SetProperty(value => value.CountryCode, (string?)null)
                .SetProperty(value => value.UserAgentFamily, (string?)null), cancellationToken);
    }

    Task<int> IRetentionRepository.DeleteExpiredSessionMetadataAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken) =>
        DeleteBatchAsync(db.RemoteSessions.Where(value => value.EndedAtUtc < cutoffUtc), value => value.EndedAtUtc, batchSize, cancellationToken);

    async Task<IReadOnlyList<DiagnosticExpirationClaim>> IRetentionRepository.ClaimExpiredDiagnosticsAsync(DateTimeOffset nowUtc, int batchSize, CancellationToken cancellationToken)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var bundles = await db.DiagnosticBundles.FromSqlInterpolated($$"""
                SELECT * FROM "DiagnosticBundles"
                WHERE "ExpiresAtUtc" <= {{nowUtc}}
                  AND "Status" NOT IN ('Expired', 'Deleted')
                ORDER BY "ExpiresAtUtc"
                FOR UPDATE SKIP LOCKED
                LIMIT {{batchSize}}
                """).ToListAsync(cancellationToken);
            foreach (var bundle in bundles) bundle.ClaimExpiration(nowUtc);
            if (bundles.Count > 0) await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (IReadOnlyList<DiagnosticExpirationClaim>)bundles
                .Select(value => new DiagnosticExpirationClaim(value.Id, value.StorageObjectKey)).ToArray();
        });
    }

    async Task IRetentionRepository.CompleteDiagnosticExpirationAsync(Guid diagnosticId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var bundle = await db.DiagnosticBundles.SingleOrDefaultAsync(value => value.Id == diagnosticId, cancellationToken);
        if (bundle is null) return;
        if (bundle.Status == DiagnosticStatus.Expired) return;
        bundle.CompleteExpiration(nowUtc);
        await db.SaveChangesAsync(cancellationToken);
    }

    Task<int> IRetentionRepository.DeleteExpiredServiceHealthAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken) =>
        DeleteBatchAsync(db.ServiceHealthSnapshots.Where(value => value.ObservedAtUtc < cutoffUtc), value => value.ObservedAtUtc, batchSize, cancellationToken);

    Task<int> IRetentionRepository.DeleteExpiredAdminSessionsAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken cancellationToken) =>
        DeleteBatchAsync(db.AdminSessions.Where(value => value.ExpiresAtUtc < cutoffUtc), value => value.ExpiresAtUtc, batchSize, cancellationToken);

    async Task<RetentionBatchResult> IRetentionRepository.ApplyRetentionBatchAsync(
        RetentionBatchPolicy policy,
        DateTimeOffset nowUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (!policy.Enabled)
            return new RetentionBatchResult(policy.RecordType, null, 0, true, "policy_disabled");
        if (policy.LegalHold)
            return new RetentionBatchResult(policy.RecordType, null, 0, true, "legal_hold");
        if (batchSize is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (string.IsNullOrWhiteSpace(policy.Version) || policy.Version.Length > 64 ||
            policy.Version.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new ArgumentException("Retention policy version is invalid.", nameof(policy));
        var minimum = policy.RecordType == RetentionRecordKind.AuditEvents ? TimeSpan.FromDays(365) : TimeSpan.FromDays(7);
        if (policy.RetentionPeriod < minimum) throw new ArgumentOutOfRangeException(nameof(policy));

        var evidenceId = Guid.NewGuid();
        var function = policy.RecordType switch
        {
            RetentionRecordKind.AuditEvents => "peeronq_apply_audit_retention",
            RetentionRecordKind.AlertEvents => "peeronq_apply_alert_retention",
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
        var sql = $"SELECT {function}({{0}}, {{1}}, {{2}}, {{3}}, {{4}}) AS \"Value\"";
        var deleted = await db.Database.SqlQueryRaw<int>(sql, evidenceId, policy.Version,
            nowUtc - policy.RetentionPeriod, batchSize, nowUtc).SingleAsync(cancellationToken);
        return new RetentionBatchResult(policy.RecordType, deleted == 0 ? null : evidenceId, deleted, false, null);
    }

    async Task<AdminOverviewV1> IAnalyticsQueryRepository.GetOverviewAsync(DateTimeOffset nowUtc, long onlineDevices, CancellationToken cancellationToken)
    {
        var today = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero);
        var month = new DateTimeOffset(new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc));
        var activeSince = nowUtc.AddDays(-30);
        var totalDownloads = await db.DownloadEvents.LongCountAsync(cancellationToken);
        var completedDownloads = await db.DownloadEvents.LongCountAsync(value => value.Result == DownloadResult.Completed, cancellationToken);
        var uniqueDownloads = await db.DownloadEvents.Where(value => value.UniquenessKeyHash != null).Select(value => value.UniquenessKeyHash).Distinct().LongCountAsync(cancellationToken);
        var totalInstallations = await db.Installations.LongCountAsync(cancellationToken);
        var activeInstallations = await db.Installations.LongCountAsync(value => value.UnregisteredAtUtc == null && !value.IsBlocked && value.LastSeenAtUtc >= activeSince, cancellationToken);
        var activeInstallationIdsToday = db.Installations.Where(value => value.UnregisteredAtUtc == null && !value.IsBlocked && value.LastSeenAtUtc >= today)
            .Select(value => value.Id);
        var activeInstallationsToday = await activeInstallationIdsToday.LongCountAsync(cancellationToken);
        var activeToday = await db.Installations.Where(value => value.UnregisteredAtUtc == null && !value.IsBlocked && value.DeviceId != null && value.LastSeenAtUtc >= today)
            .Select(value => value.DeviceId).Distinct().LongCountAsync(cancellationToken);
        var activeMonth = await db.Installations.Where(value => value.UnregisteredAtUtc == null && !value.IsBlocked && value.DeviceId != null && value.LastSeenAtUtc >= month)
            .Select(value => value.DeviceId).Distinct().LongCountAsync(cancellationToken);
        var activeSessions = await db.RemoteSessions.LongCountAsync(value => value.Lifecycle == SessionLifecycle.Connected, cancellationToken);
        var failures = await db.RemoteSessions.LongCountAsync(value => value.StartedAtUtc >= today && (value.Lifecycle == SessionLifecycle.Failed || value.Lifecycle == SessionLifecycle.Stale), cancellationToken);
        var turnSessions = await db.RemoteSessions.LongCountAsync(value => value.StartedAtUtc >= today && value.UsedTurn, cancellationToken);
        var ended = await db.RemoteSessions.LongCountAsync(value => value.StartedAtUtc >= today && value.EndedAtUtc != null, cancellationToken);
        var succeeded = await db.RemoteSessions.LongCountAsync(value => value.StartedAtUtc >= today && value.EndReason == SessionEndReason.Completed, cancellationToken);
        var crashedInstallations = await db.DiagnosticBundles
            .Where(value => value.CreatedAtUtc >= today && value.IssueCategory == "crash" && activeInstallationIdsToday.Contains(value.InstallationId))
            .Select(value => value.InstallationId).Distinct().LongCountAsync(cancellationToken);
        var updateFailures = await db.UpdateEvents.LongCountAsync(value => value.OccurredAtUtc >= today && value.Kind == UpdateEventKind.Failed, cancellationToken);
        var stable = await db.AppReleases.Where(value => value.Channel == InstallChannel.Stable && value.IsActive)
            .OrderByDescending(value => value.PublishedAtUtc).Select(value => value.Version).FirstOrDefaultAsync(cancellationToken);

        return new AdminOverviewV1(totalDownloads, completedDownloads, uniqueDownloads, totalInstallations,
            activeInstallations, onlineDevices, activeToday, activeMonth, activeSessions, failures, turnSessions,
            activeInstallationsToday == 0 ? null : decimal.Round((decimal)crashedInstallations * 100 / activeInstallationsToday, 2),
            ended == 0 ? null : decimal.Round((decimal)succeeded * 100 / ended, 2), updateFailures, stable, nowUtc);
    }

    async Task<AdminVersionDistributionsV1> IAnalyticsQueryRepository.GetVersionDistributionsAsync(DateTimeOffset nowUtc, int top, CancellationToken cancellationToken)
    {
        if (top is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(top));
        var activeSince = nowUtc.AddDays(-30);
        var active = db.Installations.AsNoTracking().Where(value =>
            value.UnregisteredAtUtc == null && !value.IsBlocked && value.LastSeenAtUtc >= activeSince);
        var activeInstallations = await active.LongCountAsync(cancellationToken);
        var clientVersionRows = await active.GroupBy(value => value.AppVersion)
            .Select(group => new { Label = group.Key, Count = group.LongCount() })
            .OrderByDescending(value => value.Count).ThenBy(value => value.Label)
            .Take(top).ToListAsync(cancellationToken);
        var clientVersions = clientVersionRows.Select(value => new DistributionValue(value.Label, value.Count)).ToArray();

        var activeWindows = active.Where(value => value.Platform == PlatformKind.Windows);
        var activeWindowsInstallations = await activeWindows.LongCountAsync(cancellationToken);
        var windowsVersionRows = await activeWindows.GroupBy(value => value.OsVersion)
            .Select(group => new { Label = group.Key, Count = group.LongCount() })
            .OrderByDescending(value => value.Count).ThenBy(value => value.Label)
            .Take(top).ToListAsync(cancellationToken);
        var windowsVersions = windowsVersionRows.Select(value => new DistributionValue(value.Label, value.Count)).ToArray();

        return new AdminVersionDistributionsV1(
            BuildDistribution(clientVersions, activeInstallations),
            BuildDistribution(windowsVersions, activeWindowsInstallations),
            activeInstallations,
            activeWindowsInstallations,
            activeSince,
            nowUtc);
    }

    async Task<PageResultV1<AdminDeviceRowV1>> IAnalyticsQueryRepository.QueryDevicesAsync(AdminQueryV1 query, CancellationToken cancellationToken)
    {
        var source = db.Devices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search)) source = source.Where(value => value.DisplayName.Contains(query.Search) || value.MaskedPublicDeviceId.Contains(query.Search));
        if (query.FromUtc is { } from) source = source.Where(value => value.LastSeenAtUtc >= from);
        if (query.ToUtc is { } to) source = source.Where(value => value.LastSeenAtUtc <= to);
        var total = await source.LongCountAsync(cancellationToken);
        source = (query.SortBy?.ToLowerInvariant(), query.Descending) switch
        {
            ("createdat", true) => source.OrderByDescending(value => value.CreatedAtUtc).ThenByDescending(value => value.Id),
            ("createdat", false) => source.OrderBy(value => value.CreatedAtUtc).ThenBy(value => value.Id),
            ("displayname", true) => source.OrderByDescending(value => value.DisplayName).ThenByDescending(value => value.Id),
            ("displayname", false) => source.OrderBy(value => value.DisplayName).ThenBy(value => value.Id),
            (_, true) => source.OrderByDescending(value => value.LastSeenAtUtc).ThenByDescending(value => value.Id),
            _ => source.OrderBy(value => value.LastSeenAtUtc).ThenBy(value => value.Id),
        };
        var rows = await source.Skip(query.Offset).Take(query.Limit).Select(value => new AdminDeviceRowV1(value.Id, value.MaskedPublicDeviceId, value.DisplayName, value.IdentityFingerprint.Substring(0, 12), value.CreatedAtUtc, value.LastSeenAtUtc, value.IsRevoked)).ToListAsync(cancellationToken);
        return Page(rows, total, query);
    }

    async Task<PageResultV1<AdminInstallationRowV1>> IAnalyticsQueryRepository.QueryInstallationsAsync(AdminQueryV1 query, CancellationToken cancellationToken)
    {
        var source = db.Installations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search;
            var channelMatch = Enum.TryParse<InstallChannel>(search, true, out var channel);
            source = source.Where(value => value.AppVersion.Contains(search) || value.OsVersion.Contains(search) ||
                value.CurrentRegion.Contains(search) || (channelMatch && value.InstallChannel == channel));
        }
        if (query.FromUtc is { } from) source = source.Where(value => value.LastSeenAtUtc >= from);
        if (query.ToUtc is { } to) source = source.Where(value => value.LastSeenAtUtc <= to);
        var total = await source.LongCountAsync(cancellationToken);
        source = (query.SortBy?.ToLowerInvariant(), query.Descending) switch
        {
            ("firstseen", true) => source.OrderByDescending(value => value.FirstSeenAtUtc).ThenByDescending(value => value.Id),
            ("firstseen", false) => source.OrderBy(value => value.FirstSeenAtUtc).ThenBy(value => value.Id),
            ("appversion", true) => source.OrderByDescending(value => value.AppVersion).ThenByDescending(value => value.Id),
            ("appversion", false) => source.OrderBy(value => value.AppVersion).ThenBy(value => value.Id),
            ("region", true) => source.OrderByDescending(value => value.CurrentRegion).ThenByDescending(value => value.Id),
            ("region", false) => source.OrderBy(value => value.CurrentRegion).ThenBy(value => value.Id),
            (_, true) => source.OrderByDescending(value => value.LastSeenAtUtc).ThenByDescending(value => value.Id),
            _ => source.OrderBy(value => value.LastSeenAtUtc).ThenBy(value => value.Id),
        };
        var entities = await source.Skip(query.Offset).Take(query.Limit).ToListAsync(cancellationToken);
        return Page(entities.Select(value => new AdminInstallationRowV1(value.Id, value.DeviceId, ToV1(value.Platform), ToV1(value.Architecture), value.AppVersion, value.OsVersion, ToV1(value.InstallChannel), value.FirstSeenAtUtc, value.LastSeenAtUtc, value.LastOnlineAtUtc, value.CurrentRegion, value.IsBlocked)).ToList(), total, query);
    }

    async Task<PageResultV1<AdminSessionRowV1>> IAnalyticsQueryRepository.QuerySessionsAsync(AdminQueryV1 query, CancellationToken cancellationToken)
    {
        var source = from session in db.RemoteSessions.AsNoTracking()
                     join viewer in db.Devices.AsNoTracking() on session.ViewerDeviceId equals viewer.Id
                     join host in db.Devices.AsNoTracking() on session.HostDeviceId equals host.Id
                     select new { session, viewer, host };
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search;
            var lifecycleMatch = Enum.TryParse<SessionLifecycle>(search, true, out var lifecycle);
            source = source.Where(value => value.viewer.MaskedPublicDeviceId.Contains(search) ||
                value.host.MaskedPublicDeviceId.Contains(search) || value.session.ServerRegion.Contains(search) ||
                (lifecycleMatch && value.session.Lifecycle == lifecycle));
        }
        if (query.FromUtc is { } from) source = source.Where(value => value.session.StartedAtUtc >= from);
        if (query.ToUtc is { } to) source = source.Where(value => value.session.StartedAtUtc <= to);
        var total = await source.LongCountAsync(cancellationToken);
        source = (query.SortBy?.ToLowerInvariant(), query.Descending) switch
        {
            ("endedat", true) => source.OrderByDescending(value => value.session.EndedAtUtc).ThenByDescending(value => value.session.Id),
            ("endedat", false) => source.OrderBy(value => value.session.EndedAtUtc).ThenBy(value => value.session.Id),
            ("region", true) => source.OrderByDescending(value => value.session.ServerRegion).ThenByDescending(value => value.session.Id),
            ("region", false) => source.OrderBy(value => value.session.ServerRegion).ThenBy(value => value.session.Id),
            (_, true) => source.OrderByDescending(value => value.session.StartedAtUtc).ThenByDescending(value => value.session.Id),
            _ => source.OrderBy(value => value.session.StartedAtUtc).ThenBy(value => value.session.Id),
        };
        var values = await source.Skip(query.Offset).Take(query.Limit).ToListAsync(cancellationToken);
        return Page(values.Select(value => new AdminSessionRowV1(value.session.Id, value.viewer.MaskedPublicDeviceId, value.host.MaskedPublicDeviceId,
            Enum.Parse<PermissionModeV1>(value.session.PermissionMode.ToString()), Enum.Parse<ConnectionPathV1>(value.session.ConnectionPath.ToString()), value.session.ServerRegion,
            value.session.StartedAtUtc, value.session.EndedAtUtc, value.session.Lifecycle.ToString(), value.session.FailureStage, value.session.ReconnectCount, value.session.UsedTurn)).ToList(), total, query);
    }

    async Task<PageResultV1<AdminDownloadRowV1>> IAnalyticsQueryRepository.QueryDownloadsAsync(AdminQueryV1 query, CancellationToken cancellationToken)
    {
        var source = db.DownloadEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search;
            source = source.Where(value => value.Version.Contains(search) || value.Source.Contains(search) ||
                (value.Campaign != null && value.Campaign.Contains(search)));
        }
        if (query.FromUtc is { } from) source = source.Where(value => value.StartedAtUtc >= from);
        if (query.ToUtc is { } to) source = source.Where(value => value.StartedAtUtc <= to);
        var total = await source.LongCountAsync(cancellationToken);
        source = (query.SortBy?.ToLowerInvariant(), query.Descending) switch
        {
            ("completedat", true) => source.OrderByDescending(value => value.CompletedAtUtc).ThenByDescending(value => value.Id),
            ("completedat", false) => source.OrderBy(value => value.CompletedAtUtc).ThenBy(value => value.Id),
            ("version", true) => source.OrderByDescending(value => value.Version).ThenByDescending(value => value.Id),
            ("version", false) => source.OrderBy(value => value.Version).ThenBy(value => value.Id),
            (_, true) => source.OrderByDescending(value => value.StartedAtUtc).ThenByDescending(value => value.Id),
            _ => source.OrderBy(value => value.StartedAtUtc).ThenBy(value => value.Id),
        };
        var values = await source.Skip(query.Offset).Take(query.Limit).ToListAsync(cancellationToken);
        return Page(values.Select(value => new AdminDownloadRowV1(value.Id, ToV1(value.Platform), ToV1(value.Architecture), value.Version, ToV1(value.Channel), value.Source, value.Campaign, value.CountryCode, value.StartedAtUtc, value.CompletedAtUtc, Enum.Parse<DownloadResultV1>(value.Result.ToString()))).ToList(), total, query);
    }

    async Task<PageResultV1<AdminReleaseRowV1>> IAnalyticsQueryRepository.QueryReleasesAsync(AdminQueryV1 query, CancellationToken cancellationToken)
    {
        var source = db.AppReleases.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search;
            var channelMatch = Enum.TryParse<InstallChannel>(search, true, out var channel);
            source = source.Where(value => value.Version.Contains(search) || (channelMatch && value.Channel == channel));
        }
        if (query.FromUtc is { } from) source = source.Where(value => value.PublishedAtUtc >= from);
        if (query.ToUtc is { } to) source = source.Where(value => value.PublishedAtUtc <= to);
        var total = await source.LongCountAsync(cancellationToken);
        source = (query.SortBy?.ToLowerInvariant(), query.Descending) switch
        {
            ("version", true) => source.OrderByDescending(value => value.Version).ThenByDescending(value => value.Id),
            ("version", false) => source.OrderBy(value => value.Version).ThenBy(value => value.Id),
            ("rollout", true) => source.OrderByDescending(value => value.RolloutPercentage).ThenByDescending(value => value.Id),
            ("rollout", false) => source.OrderBy(value => value.RolloutPercentage).ThenBy(value => value.Id),
            (_, true) => source.OrderByDescending(value => value.PublishedAtUtc).ThenByDescending(value => value.Id),
            _ => source.OrderBy(value => value.PublishedAtUtc).ThenBy(value => value.Id),
        };
        var releases = await source.Skip(query.Offset).Take(query.Limit).ToListAsync(cancellationToken);
        var ids = releases.Select(value => value.Id).ToArray();
        var counts = await db.UpdateEvents.AsNoTracking().Where(value => ids.Contains(value.ReleaseId))
            .GroupBy(value => new { value.ReleaseId, value.Kind }).Select(group => new { group.Key.ReleaseId, group.Key.Kind, Count = group.LongCount() }).ToListAsync(cancellationToken);
        var countLookup = counts.ToDictionary(value => (value.ReleaseId, value.Kind), value => value.Count);
        return Page(releases.Select(value => new AdminReleaseRowV1(value.Id, value.Version, ToV1(value.Channel), ToV1(value.Architecture), value.PublishedAtUtc, value.RolloutPercentage, value.IsActive,
            GetCount(countLookup, value.Id, UpdateEventKind.Offered), GetCount(countLookup, value.Id, UpdateEventKind.Installed), GetCount(countLookup, value.Id, UpdateEventKind.Failed), GetCount(countLookup, value.Id, UpdateEventKind.RolledBack))).ToList(), total, query);
    }

    async Task<PageResultV1<AdminDiagnosticRowV1>> IAnalyticsQueryRepository.QueryDiagnosticsAsync(AdminQueryV1 query, CancellationToken cancellationToken)
    {
        var source = db.DiagnosticBundles.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search;
            source = source.Where(value => value.AppVersion.Contains(search) ||
                (value.ErrorId != null && value.ErrorId.Contains(search)) ||
                (value.IssueCategory != null && value.IssueCategory.Contains(search)));
        }
        if (query.FromUtc is { } from) source = source.Where(value => value.CreatedAtUtc >= from);
        if (query.ToUtc is { } to) source = source.Where(value => value.CreatedAtUtc <= to);
        var total = await source.LongCountAsync(cancellationToken);
        source = (query.SortBy?.ToLowerInvariant(), query.Descending) switch
        {
            ("expiresat", true) => source.OrderByDescending(value => value.ExpiresAtUtc).ThenByDescending(value => value.Id),
            ("expiresat", false) => source.OrderBy(value => value.ExpiresAtUtc).ThenBy(value => value.Id),
            ("appversion", true) => source.OrderByDescending(value => value.AppVersion).ThenByDescending(value => value.Id),
            ("appversion", false) => source.OrderBy(value => value.AppVersion).ThenBy(value => value.Id),
            (_, true) => source.OrderByDescending(value => value.CreatedAtUtc).ThenByDescending(value => value.Id),
            _ => source.OrderBy(value => value.CreatedAtUtc).ThenBy(value => value.Id),
        };
        var values = await source.Skip(query.Offset).Take(query.Limit).ToListAsync(cancellationToken);
        return Page(values.Select(value => new AdminDiagnosticRowV1(value.Id, value.InstallationId, Enum.Parse<DiagnosticStatusV1>(value.Status.ToString()), value.AppVersion, value.ErrorId, value.IssueCategory, value.ConsentGranted, value.CreatedAtUtc, value.ExpiresAtUtc)).ToList(), total, query);
    }

    async Task<PageResultV1<AdminInfrastructureRowV1>> IAnalyticsQueryRepository.QueryInfrastructureAsync(AdminQueryV1 query, CancellationToken cancellationToken)
    {
        var source = from health in db.ServiceHealthSnapshots.AsNoTracking()
                     join region in db.InfrastructureRegions.AsNoTracking() on health.RegionId equals region.Id
                     select new { health, region };
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search;
            source = source.Where(value => value.health.Service.Contains(search) || value.region.Code.Contains(search));
        }
        if (query.FromUtc is { } from) source = source.Where(value => value.health.ObservedAtUtc >= from);
        if (query.ToUtc is { } to) source = source.Where(value => value.health.ObservedAtUtc <= to);
        var total = await source.LongCountAsync(cancellationToken);
        source = (query.SortBy?.ToLowerInvariant(), query.Descending) switch
        {
            ("region", true) => source.OrderByDescending(value => value.region.Code).ThenByDescending(value => value.health.Id),
            ("region", false) => source.OrderBy(value => value.region.Code).ThenBy(value => value.health.Id),
            ("service", true) => source.OrderByDescending(value => value.health.Service).ThenByDescending(value => value.health.Id),
            ("service", false) => source.OrderBy(value => value.health.Service).ThenBy(value => value.health.Id),
            (_, true) => source.OrderByDescending(value => value.health.ObservedAtUtc).ThenByDescending(value => value.health.Id),
            _ => source.OrderBy(value => value.health.ObservedAtUtc).ThenBy(value => value.health.Id),
        };
        var rows = await source.Skip(query.Offset).Take(query.Limit).Select(value => new AdminInfrastructureRowV1(value.region.Code, value.health.Service, value.health.State.ToString(), value.health.LatencyMilliseconds, value.health.ObservedAtUtc)).ToListAsync(cancellationToken);
        return Page(rows, total, query);
    }

    async Task<PageResultV1<AdminAuditRowV1>> IAnalyticsQueryRepository.QueryAuditAsync(AdminQueryV1 query, CancellationToken cancellationToken)
    {
        var source = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search;
            source = source.Where(value => value.ActorId.Contains(search) || value.Action.Contains(search) ||
                value.TargetType.Contains(search) || value.CorrelationId.Contains(search));
        }
        if (query.FromUtc is { } from) source = source.Where(value => value.TimestampUtc >= from);
        if (query.ToUtc is { } to) source = source.Where(value => value.TimestampUtc <= to);
        var total = await source.LongCountAsync(cancellationToken);
        source = (query.SortBy?.ToLowerInvariant(), query.Descending) switch
        {
            ("action", true) => source.OrderByDescending(value => value.Action).ThenByDescending(value => value.Id),
            ("action", false) => source.OrderBy(value => value.Action).ThenBy(value => value.Id),
            (_, true) => source.OrderByDescending(value => value.TimestampUtc).ThenByDescending(value => value.Id),
            _ => source.OrderBy(value => value.TimestampUtc).ThenBy(value => value.Id),
        };
        var rows = await source.Skip(query.Offset).Take(query.Limit).Select(value => new AdminAuditRowV1(value.Id, value.ActorId, value.ActorType, value.Action, value.TargetType, value.TargetId, value.Result.ToString(), value.TimestampUtc, value.CorrelationId, value.Reason)).ToListAsync(cancellationToken);
        return Page(rows, total, query);
    }

    async Task<PageResultV1<AdminAlertRowV1>> IAnalyticsQueryRepository.QueryAlertsAsync(AdminQueryV1 query, CancellationToken cancellationToken)
    {
        var source = db.AlertEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search;
            var severityMatch = Enum.TryParse<AlertSeverity>(search, true, out var severity);
            source = source.Where(value => value.RuleName.Contains(search) || value.Summary.Contains(search) ||
                value.Region.Contains(search) || (severityMatch && value.Severity == severity));
        }
        if (query.FromUtc is { } from) source = source.Where(value => value.StartedAtUtc >= from);
        if (query.ToUtc is { } to) source = source.Where(value => value.StartedAtUtc <= to);
        var total = await source.LongCountAsync(cancellationToken);
        source = (query.SortBy?.ToLowerInvariant(), query.Descending) switch
        {
            ("severity", true) => source.OrderByDescending(value => value.Severity).ThenByDescending(value => value.Id),
            ("severity", false) => source.OrderBy(value => value.Severity).ThenBy(value => value.Id),
            ("region", true) => source.OrderByDescending(value => value.Region).ThenByDescending(value => value.Id),
            ("region", false) => source.OrderBy(value => value.Region).ThenBy(value => value.Id),
            (_, true) => source.OrderByDescending(value => value.StartedAtUtc).ThenByDescending(value => value.Id),
            _ => source.OrderBy(value => value.StartedAtUtc).ThenBy(value => value.Id),
        };
        var rows = await source.Skip(query.Offset).Take(query.Limit).Select(value => new AdminAlertRowV1(value.Id, value.RuleName, value.Severity.ToString(), value.Region, value.Summary, value.RunbookUrl, value.StartedAtUtc, value.ResolvedAtUtc)).ToListAsync(cancellationToken);
        return Page(rows, total, query);
    }

    private DownloadArtifact ToArtifact(AppRelease release, PlatformKind platform)
    {
        var uri = new Uri(release.ArtifactUri);
        if (!environment.IsDevelopment() && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Non-development release artifacts must use HTTPS.");
        return new DownloadArtifact(release.Version, platform, release.Architecture, release.Channel, uri,
            release.ArtifactSha256, release.SignedManifestDigest, release.ArtifactSizeBytes);
    }

    private async Task<int> DeleteBatchAsync<TEntity, TOrder>(IQueryable<TEntity> source, System.Linq.Expressions.Expression<Func<TEntity, TOrder>> order, int batchSize, CancellationToken cancellationToken) where TEntity : class
    {
        var items = await source.OrderBy(order).Take(batchSize).ToListAsync(cancellationToken);
        if (items.Count == 0) return 0;
        db.RemoveRange(items);
        await db.SaveChangesAsync(cancellationToken);
        return items.Count;
    }

    private static PageResultV1<T> Page<T>(IReadOnlyList<T> rows, long total, AdminQueryV1 query) => new(rows, total, query.Offset, query.Limit);
    private static PlatformKindV1 ToV1(PlatformKind value) => Enum.Parse<PlatformKindV1>(value.ToString());
    private static ArchitectureKindV1 ToV1(ArchitectureKind value) => Enum.Parse<ArchitectureKindV1>(value.ToString());
    private static InstallChannelV1 ToV1(InstallChannel value) => Enum.Parse<InstallChannelV1>(value.ToString());
    private static long GetCount(IReadOnlyDictionary<(Guid ReleaseId, UpdateEventKind Kind), long> counts, Guid releaseId, UpdateEventKind kind) =>
        counts.GetValueOrDefault((releaseId, kind));

    private static IReadOnlyList<DistributionBucketV1> BuildDistribution(IReadOnlyList<DistributionValue> values, long total)
    {
        if (total == 0) return [];
        var buckets = values.Select(value => new DistributionBucketV1(
            value.Label,
            value.Count,
            decimal.Round((decimal)value.Count * 100 / total, 2))).ToList();
        var represented = values.Sum(value => value.Count);
        if (represented < total)
            buckets.Add(new DistributionBucketV1("Other", total - represented,
                decimal.Round((decimal)(total - represented) * 100 / total, 2)));
        return buckets;
    }

    private sealed record DistributionValue(string Label, long Count);
}
