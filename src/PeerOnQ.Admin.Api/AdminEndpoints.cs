using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Observability;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Admin.Api;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(AlertmanagerIngestion.Path, AlertmanagerIngestion.IngestAsync)
            .WithMetadata(new RequestSizeLimitAttribute(256 * 1024))
            .AllowAnonymous()
            .RequireRateLimiting("internal-alerts");

        var auth = endpoints.MapGroup("/admin/v1/auth");
        auth.WithMetadata(new RequestSizeLimitAttribute(256 * 1024));
        auth.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting("admin-login");
        auth.MapPost("/mfa/verify", VerifyMfaAsync).AllowAnonymous().RequireRateLimiting("admin-mfa");
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting("admin-refresh");
        auth.MapPost("/logout", LogoutAsync).AllowAnonymous().RequireRateLimiting("admin-refresh");
        auth.MapGet("/session", Session).RequireAuthorization("admin.read").RequireRateLimiting("admin-api");

        var admin = endpoints.MapGroup("/admin/v1")
            .RequireAuthorization("admin.read")
            .RequireRateLimiting("admin-api");
        admin.WithMetadata(new RequestSizeLimitAttribute(256 * 1024));
        admin.AddEndpointFilter(async (invocation, next) =>
        {
            var request = invocation.HttpContext.Request;
            if (!HttpMethods.IsGet(request.Method)
                && !HttpMethods.IsHead(request.Method)
                && !HttpMethods.IsOptions(request.Method))
                AdminAuthService.ValidateCsrf(invocation.HttpContext);
            return await next(invocation);
        });
        admin.MapGet("/overview", GetOverviewAsync);
        admin.MapGet("/overview/distributions", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetVersionDistributionsAsync(
                ParseBoundedInt(request.Query["top"].FirstOrDefault(), 10, 1, 25, "top"),
                ct));
        admin.MapGet("/devices", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetDevicesAsync(Query(request, "lastSeen", "createdAt", "displayName"), ct));
        admin.MapGet("/installations", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetInstallationsAsync(Query(request, "lastSeen", "firstSeen", "appVersion", "region"), ct));
        admin.MapGet("/presence", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetPresenceAsync(Query(request, "expiresAt"), ct));
        admin.MapGet("/sessions", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetSessionsAsync(Query(request, "startedAt", "endedAt", "region"), ct));
        admin.MapGet("/downloads", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetDownloadsAsync(Query(request, "startedAt", "completedAt", "version"), ct));
        admin.MapGet("/releases", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetReleasesAsync(Query(request, "publishedAt", "version", "rollout"), ct));
        admin.MapGet("/website-releases", (WebsitePublicationService publication) => publication.List())
            .RequireAuthorization("admin.release");
        MapPlatformUpgradeEndpoints(admin);
        admin.MapGet("/diagnostics", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetDiagnosticsAsync(Query(request, "createdAt", "expiresAt", "appVersion"), ct))
            .RequireAuthorization("admin.diagnostics");
        admin.MapGet("/infrastructure", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetInfrastructureAsync(Query(request, "observedAt", "region", "service"), ct))
            .RequireAuthorization("admin.operations");
        admin.MapGet("/infrastructure/metrics", (AdminInfrastructureMetricsService service, CancellationToken ct) => service.GetAsync(ct))
            .RequireAuthorization("admin.operations");
        admin.MapGet("/audit", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetAuditAsync(Query(request, "timestamp", "action"), ct))
            .RequireAuthorization("admin.security");
        admin.MapGet("/alerts", (HttpRequest request, IAdminQueryService service, CancellationToken ct) =>
            service.GetAlertsAsync(Query(request, "startedAt", "severity", "region"), ct))
            .RequireAuthorization("admin.operations");
        admin.MapGet("/admin-sessions", ListAdminSessionsAsync)
            .RequireAuthorization("admin.security");
        admin.MapGet("/diagnostics/{diagnosticId:guid}", OpenDiagnosticAsync)
            .RequireAuthorization("admin.diagnostics");

        admin.MapPut("/installations/{installationId:guid}/block", SetInstallationBlockAsync)
            .RequireAuthorization("admin.security-or-operations");
        admin.MapPut("/releases/{releaseId:guid}/rollout", SetReleaseRolloutAsync)
            .RequireAuthorization("admin.release");
        admin.MapPost("/releases", PublishReleaseAsync)
            .WithMetadata(new RequestSizeLimitAttribute(ReleasePublicationOptions.MaximumRequestBodyBytes))
            .DisableAntiforgery()
            .RequireAuthorization("admin.release");
        admin.MapPut("/releases/{releaseId:guid}/manifest", ReplaceReleaseManifestAsync)
            .RequireAuthorization("admin.release");
        admin.MapPost("/website-releases", PublishWebsiteReleaseAsync)
            .WithMetadata(new RequestSizeLimitAttribute(WebsitePublicationOptions.MaximumRequestBodyBytes))
            .DisableAntiforgery()
            .RequireAuthorization("admin.release");
        admin.MapPost("/website-releases/{version}/activate", ActivateWebsiteReleaseAsync)
            .RequireAuthorization("admin.release");
        admin.MapPost("/devices/{deviceId:guid}/revoke", RevokeDeviceAsync)
            .RequireAuthorization("admin.security");
        admin.MapPost("/admin-sessions/{sessionId:guid}/revoke", RevokeAdminSessionAsync)
            .RequireAuthorization("admin.security");

        return endpoints;
    }

    internal static void MapPlatformUpgradeEndpoints(RouteGroupBuilder admin)
    {
        admin.MapGet("/platform-upgrades/status", (PlatformUpgradeService service) => service.GetStatus())
            .RequireAuthorization("admin.release");
        admin.MapPost("/platform-upgrades/stage", StagePlatformUpgradeAsync)
            .WithMetadata(
                new RequestSizeLimitAttribute(PlatformUpgradeOptions.MaximumRequestBodyBytes),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = PlatformUpgradeOptions.MaximumRequestBodyBytes })
            .DisableAntiforgery()
            .RequireAuthorization("admin.release");
        admin.MapPost("/platform-upgrades/apply", ApplyPlatformUpgradeAsync)
            .RequireAuthorization("admin.platform-upgrade");
        admin.MapPost("/platform-upgrades/rollback", RollbackPlatformUpgradeAsync)
            .RequireAuthorization("admin.platform-upgrade");
    }

    internal static async Task<AdminOverviewV1> GetOverviewAsync(
        IAdminQueryService service,
        AdminInfrastructureMetricsService metrics,
        CancellationToken cancellationToken)
    {
        var persisted = await service.GetOverviewAsync(cancellationToken);
        var live = await metrics.GetLiveOverviewAsync(cancellationToken);
        return AdminOverviewLiveMetricsOverlay.Apply(persisted, live);
    }

    private static Task<AdminAuthResponse> LoginAsync(
        AdminLoginRequest request,
        HttpContext context,
        AdminAuthService service,
        CancellationToken cancellationToken) => service.LoginAsync(request, context, cancellationToken);

    private static Task<AdminAuthResponse> VerifyMfaAsync(
        AdminMfaVerifyRequest request,
        HttpContext context,
        AdminAuthService service,
        CancellationToken cancellationToken) => service.VerifyMfaAsync(request, context, cancellationToken);

    private static Task<AdminRefreshResponse> RefreshAsync(
        HttpContext context,
        AdminAuthService service,
        CancellationToken cancellationToken) => service.RefreshAsync(context, cancellationToken);

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        AdminAuthService service,
        CancellationToken cancellationToken)
    {
        await service.LogoutAsync(context, cancellationToken);
        return Results.NoContent();
    }

    private static AdminSessionResponse Session(
        HttpContext context,
        IOptions<ReleasePublicationOptions> releasePublication,
        IOptions<WebsitePublicationOptions> websitePublication) => new(
        AdminTokenService.RequireAdminUserId(context.User),
        context.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(claim => claim.Value).Order().ToArray(),
        context.User.FindFirst("mfa")?.Value == "true",
        AdminTokenService.RequireExpiry(context.User),
        releasePublication.Value.Enabled,
        websitePublication.Value.Enabled);

    private static async Task<IResult> SetInstallationBlockAsync(
        Guid installationId,
        AdminBlockRequestV1 request,
        HttpContext context,
        CloudDbContext db,
        AdminAuditWriter audit,
        AdminRequestContext requestContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
            throw new ArgumentException("A bounded reason is required.", nameof(request));
        var installation = await db.Installations.SingleOrDefaultAsync(value => value.Id == installationId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (request.Blocked) installation.Block(request.Reason); else installation.Unblock();
        var actor = AdminTokenService.RequireAdminUserId(context.User).ToString("N");
        audit.Add(context, actor, request.Blocked ? "installation.block" : "installation.unblock", "Installation", installationId.ToString("N"), AuditResult.Succeeded, request.Reason, requestContext, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<AdminPageResponse<AdminSessionRow>> ListAdminSessionsAsync(
        HttpRequest request,
        CloudDbContext db,
        CancellationToken cancellationToken)
    {
        var offset = ParseBoundedInt(request.Query["offset"].FirstOrDefault(), 0, 0, 1_000_000, "offset");
        var limit = ParseBoundedInt(request.Query["limit"].FirstOrDefault(), 50, 1, 200, "limit");
        var sessions = db.AdminSessions.AsNoTracking();
        if (Guid.TryParse(request.Query["userId"].FirstOrDefault(), out var userId))
            sessions = sessions.Where(value => value.AdminUserId == userId);
        else if (!string.IsNullOrWhiteSpace(request.Query["userId"].FirstOrDefault()))
            throw new ArgumentException("The admin user ID is invalid.", nameof(request));
        var includeRevokedValue = request.Query["includeRevoked"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(includeRevokedValue) && !bool.TryParse(includeRevokedValue, out _))
            throw new ArgumentException("The includeRevoked value is invalid.", nameof(request));
        var includeRevoked = bool.TryParse(includeRevokedValue, out var parsedIncludeRevoked) && parsedIncludeRevoked;
        if (!includeRevoked)
            sessions = sessions.Where(value => value.RevokedAtUtc == null);

        var total = await sessions.LongCountAsync(cancellationToken);
        var rows = await sessions
            .OrderByDescending(value => value.CreatedAtUtc)
            .ThenBy(value => value.Id)
            .Skip(offset)
            .Take(limit)
            .Select(value => new AdminSessionRow(
                value.Id,
                value.AdminUserId,
                value.CreatedAtUtc,
                value.ExpiresAtUtc,
                value.RevokedAtUtc,
                value.UserAgentSummary))
            .ToListAsync(cancellationToken);
        return new AdminPageResponse<AdminSessionRow>(rows, offset, limit, total);
    }

    private static async Task<IResult> RevokeAdminSessionAsync(
        Guid sessionId,
        AdminReasonRequest request,
        HttpContext context,
        CloudDbContext db,
        AdminAuditWriter audit,
        AdminRequestContext requestContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ValidateReason(request.Reason, 512);
        var session = await db.AdminSessions.SingleOrDefaultAsync(value => value.Id == sessionId, cancellationToken)
            ?? throw new KeyNotFoundException();
        var now = timeProvider.GetUtcNow();
        session.Revoke(now);
        audit.Add(
            context,
            AdminTokenService.RequireAdminUserId(context.User).ToString("N"),
            "admin.session.revoke",
            "AdminSession",
            sessionId.ToString("N"),
            AuditResult.Succeeded,
            request.Reason,
            requestContext,
            now);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> OpenDiagnosticAsync(
        Guid diagnosticId,
        HttpContext context,
        CloudDbContext db,
        AdminAuditWriter audit,
        AdminRequestContext requestContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var diagnostic = await db.DiagnosticBundles.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == diagnosticId, cancellationToken)
            ?? throw new KeyNotFoundException();
        var actorId = AdminTokenService.RequireAdminUserId(context.User);
        var now = timeProvider.GetUtcNow();
        db.DiagnosticAccessEvents.Add(new PeerOnQ.Cloud.Domain.Entities.DiagnosticAccessEvent(
            Guid.NewGuid(), diagnosticId, actorId, "metadata.opened", now));
        audit.Add(
            context,
            actorId.ToString("N"),
            "diagnostic.open",
            "DiagnosticBundle",
            diagnosticId.ToString("N"),
            AuditResult.Succeeded,
            null,
            requestContext,
            now);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new AdminDiagnosticDetailResponse(
            diagnostic.Id,
            diagnostic.InstallationId,
            diagnostic.Status.ToString(),
            diagnostic.ConsentGranted,
            diagnostic.ConsentGrantedAtUtc,
            diagnostic.AppVersion,
            diagnostic.OsVersion,
            diagnostic.Architecture.ToString(),
            diagnostic.ErrorId,
            diagnostic.IssueCategory,
            diagnostic.ReferenceCode,
            diagnostic.SanitizedArchiveSizeBytes,
            diagnostic.CreatedAtUtc,
            diagnostic.ExpiresAtUtc));
    }

    private static IResult SetReleaseRolloutAsync() =>
        throw new ApiProblemException(
            StatusCodes.Status409Conflict,
            "signed_manifest_required",
            "Release rollout is signed. Upload a newly signed manifest instead of changing a database percentage.");

    private static async Task<IResult> PublishReleaseAsync(
        HttpRequest request,
        HttpContext context,
        ReleasePublicationService publication,
        ReleaseUploadGate uploadGate,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            throw new ApiProblemException(StatusCodes.Status415UnsupportedMediaType, "multipart_required", "A multipart signed release upload is required.");
        using var lease = await uploadGate.TryAcquireAsync(cancellationToken);
        if (lease is null)
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "release_upload_busy", "Another release upload is being verified.");

        var form = await request.ReadFormAsync(cancellationToken);
        if (form.Files.Count != 2 || form.Keys.Any(key => key != "reason"))
            throw new ArgumentException("Exactly one manifest, one package, and one audit reason are required.", nameof(request));
        var manifest = form.Files.GetFile("manifest")
            ?? throw new ArgumentException("The signed manifest is required.", nameof(request));
        var package = form.Files.GetFile("package")
            ?? throw new ArgumentException("The signed MSI package is required.", nameof(request));
        var result = await publication.PublishAsync(
            manifest,
            package,
            form["reason"].FirstOrDefault() ?? string.Empty,
            context,
            cancellationToken);
        return Results.Created($"/admin/v1/releases/{result.ReleaseId:D}", result);
    }

    private static async Task<IResult> ReplaceReleaseManifestAsync(
        Guid releaseId,
        AdminReleaseManifestRequestV1 request,
        HttpContext context,
        ReleasePublicationService publication,
        CancellationToken cancellationToken) =>
        Results.Ok(await publication.ReplaceManifestAsync(releaseId, request, context, cancellationToken));

    private static async Task<IResult> PublishWebsiteReleaseAsync(
        HttpRequest request,
        HttpContext context,
        WebsitePublicationService publication,
        WebsiteUploadGate uploadGate,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            throw new ApiProblemException(StatusCodes.Status415UnsupportedMediaType, "multipart_required", "A multipart signed website upload is required.");
        using var lease = await uploadGate.TryAcquireAsync(cancellationToken);
        if (lease is null)
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "website_upload_busy", "Another website upload is being verified.");

        var form = await request.ReadFormAsync(cancellationToken);
        if (form.Files.Count != 2 || form.Keys.Any(key => key != "reason"))
            throw new ArgumentException("Exactly one manifest, one archive, and one audit reason are required.", nameof(request));
        var manifest = form.Files.GetFile("manifest")
            ?? throw new ArgumentException("The signed website manifest is required.", nameof(request));
        var archive = form.Files.GetFile("archive")
            ?? throw new ArgumentException("The signed website ZIP archive is required.", nameof(request));
        var result = await publication.PublishAsync(
            manifest,
            archive,
            form["reason"].FirstOrDefault() ?? string.Empty,
            context,
            cancellationToken);
        return Results.Created($"/admin/v1/website-releases/{result.Version}", result);
    }

    private static async Task<IResult> ActivateWebsiteReleaseAsync(
        string version,
        WebsiteReleaseActivationRequestV1 request,
        HttpContext context,
        WebsitePublicationService publication,
        CancellationToken cancellationToken) =>
        Results.Ok(await publication.ActivateAsync(version, request, context, cancellationToken));

    internal static async Task<IResult> StagePlatformUpgradeAsync(
        HttpRequest request,
        HttpContext context,
        PlatformUpgradeService service,
        PlatformUpgradeUploadGate uploadGate,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType
            || request.ContentType is null
            || !request.ContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new ApiProblemException(
                StatusCodes.Status415UnsupportedMediaType,
                "multipart_required",
                "A multipart platform-upgrade upload is required.");
        if (request.ContentLength is > PlatformUpgradeOptions.MaximumRequestBodyBytes)
            throw new ApiProblemException(
                StatusCodes.Status413PayloadTooLarge,
                "platform_upgrade_request_too_large",
                "The platform-upgrade upload exceeds the request limit.");

        using var lease = await uploadGate.TryAcquireAsync(cancellationToken);
        if (lease is null)
            throw new ApiProblemException(
                StatusCodes.Status429TooManyRequests,
                "platform_upgrade_upload_busy",
                "Another platform-upgrade upload is being received.");

        var form = await request.ReadFormAsync(cancellationToken);
        if (form.Files.Count != 3
            || form.Keys.Count() != 1
            || !form.Keys.Contains("reason", StringComparer.Ordinal)
            || form["reason"].Count != 1
            || form.Files.Count(file => file.Name == "bundle") != 1
            || form.Files.Count(file => file.Name == "checksum") != 1
            || form.Files.Count(file => file.Name == "signature") != 1)
            throw new ArgumentException(
                "Exactly one bundle, checksum, signature, and audit reason are required.",
                nameof(request));

        var result = await service.StageAsync(
            form.Files.GetFile("bundle")!,
            form.Files.GetFile("checksum")!,
            form.Files.GetFile("signature")!,
            form["reason"].Single() ?? string.Empty,
            context,
            cancellationToken);
        return Results.Accepted("/admin/v1/platform-upgrades/status", result);
    }

    internal static async Task<IResult> ApplyPlatformUpgradeAsync(
        PlatformUpgradeActionRequestV1 request,
        HttpContext context,
        PlatformUpgradeService service,
        CancellationToken cancellationToken) =>
        Results.Accepted(
            "/admin/v1/platform-upgrades/status",
            await service.ApplyAsync(request, context, cancellationToken));

    internal static async Task<IResult> RollbackPlatformUpgradeAsync(
        PlatformUpgradeActionRequestV1 request,
        HttpContext context,
        PlatformUpgradeService service,
        CancellationToken cancellationToken) =>
        Results.Accepted(
            "/admin/v1/platform-upgrades/status",
            await service.RollbackAsync(request, context, cancellationToken));

    private static async Task<IResult> RevokeDeviceAsync(
        Guid deviceId,
        AdminBlockRequestV1 request,
        HttpContext context,
        CloudDbContext db,
        AdminAuditWriter audit,
        AdminRequestContext requestContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!request.Blocked || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
            throw new ArgumentException("Device revocation is irreversible and requires Blocked=true with a reason.", nameof(request));
        var device = await db.Devices.SingleOrDefaultAsync(value => value.Id == deviceId, cancellationToken)
            ?? throw new KeyNotFoundException();
        device.Revoke(request.Reason, timeProvider.GetUtcNow());
        audit.Add(context, AdminTokenService.RequireAdminUserId(context.User).ToString("N"), "device.revoke", "Device", deviceId.ToString("N"), AuditResult.Succeeded, request.Reason, requestContext, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static AdminQueryV1 Query(HttpRequest request, params string[] allowedSorts)
    {
        static int Int(string? value, int fallback, int minimum, int maximum, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
                throw new ArgumentException($"The {name} value is invalid.", name);
            return parsed;
        }
        static DateTimeOffset? Date(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
                throw new ArgumentException($"The {name} value is invalid.", name);
            return parsed;
        }
        static bool Bool(string? value, bool fallback, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (!bool.TryParse(value, out var parsed))
                throw new ArgumentException($"The {name} value is invalid.", name);
            return parsed;
        }
        var query = request.Query;
        return new AdminQueryParameters
        {
            Offset = Int(query["offset"].FirstOrDefault(), 0, 0, 1_000_000, "offset"),
            Limit = Int(query["limit"].FirstOrDefault(), 50, 1, 200, "limit"),
            Search = query["search"].FirstOrDefault(),
            FromUtc = Date(query["fromUtc"].FirstOrDefault(), "fromUtc"),
            ToUtc = Date(query["toUtc"].FirstOrDefault(), "toUtc"),
            SortBy = query["sortBy"].FirstOrDefault(),
            Descending = Bool(query["descending"].FirstOrDefault(), true, "descending"),
        }.ToContract(allowedSorts);
    }

    private static int ParseBoundedInt(string? value, int fallback, int minimum, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentException($"The {name} value is invalid.", name);
        return parsed;
    }

    private static void ValidateReason(string? reason, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > maximumLength)
            throw new ArgumentException("A bounded reason is required.", nameof(reason));
    }
}
