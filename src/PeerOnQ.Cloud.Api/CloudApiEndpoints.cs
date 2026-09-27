using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Observability;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Api;

public static class CloudApiEndpoints
{
    public static IEndpointRouteBuilder MapCloudApiV1(this IEndpointRouteBuilder endpoints)
    {
        var version = endpoints.MapGroup("/v1");

        var installations = version.MapGroup("/installations");
        installations.MapPost("/register", RegisterInstallationAsync)
            .RequireAuthorization(DeviceAccessAuthentication.Policy)
            .RequireRateLimiting("device");
        installations.MapPost("/heartbeat", HeartbeatAsync)
            .RequireAuthorization(DeviceAccessAuthentication.Policy)
            .RequireRateLimiting("device");
        installations.MapPost("/version", UpdateVersionAsync)
            .RequireAuthorization(DeviceAccessAuthentication.Policy)
            .RequireRateLimiting("device");
        installations.MapPost("/unregister", UnregisterAsync)
            .RequireAuthorization(DeviceAccessAuthentication.Policy)
            .RequireRateLimiting("device");

        var devices = version.MapGroup("/devices");
        devices.MapPost("/register", IssueDeviceChallengeAsync)
            .AllowAnonymous()
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .RequireRateLimiting("registration");
        devices.MapPost("/authenticate", AuthenticateDeviceAsync)
            .AllowAnonymous()
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .RequireRateLimiting("authentication");

        var sessions = version.MapGroup("/sessions")
            .RequireAuthorization(DeviceAccessAuthentication.Policy)
            .RequireRateLimiting("telemetry");
        sessions.MapPost("/start", (ClientSessionLifecycleEventV1 request, HttpContext context, ISessionTelemetryService service, PeerOnQMetrics metrics, CancellationToken cancellationToken) =>
            RecordSessionAsync(request, SessionEventKindV1.Started, context, service, metrics, cancellationToken));
        sessions.MapPost("/connected", (ClientSessionLifecycleEventV1 request, HttpContext context, ISessionTelemetryService service, PeerOnQMetrics metrics, CancellationToken cancellationToken) =>
            RecordSessionAsync(request, SessionEventKindV1.Connected, context, service, metrics, cancellationToken));
        sessions.MapPost("/end", (ClientSessionLifecycleEventV1 request, HttpContext context, ISessionTelemetryService service, PeerOnQMetrics metrics, CancellationToken cancellationToken) =>
            RecordSessionAsync(request, SessionEventKindV1.Ended, context, service, metrics, cancellationToken));
        sessions.MapPost("/heartbeat", HeartbeatSessionAsync)
            .WithMetadata(new RequestSizeLimitAttribute(4 * 1024));

        version.MapPost("/updates/events", RecordUpdateAsync)
            .RequireAuthorization(DeviceAccessAuthentication.Policy)
            .RequireRateLimiting("device");

        var diagnostics = version.MapGroup("/diagnostics")
            .RequireAuthorization(DeviceAccessAuthentication.Policy)
            .RequireRateLimiting("diagnostics");
        diagnostics.MapPost("/requests", CreateDiagnosticsRequestAsync);
        diagnostics.MapPost("/uploads", CompleteDiagnosticsUploadAsync)
            .WithMetadata(new RequestSizeLimitAttribute(DiagnosticStorageOptions.MaximumArchiveBytes + (256 * 1024)))
            .DisableAntiforgery();
        diagnostics.MapGet("/{diagnosticId:guid}/status", GetDiagnosticsStatusAsync);

        return endpoints;
    }

    private static async Task<IResult> RegisterInstallationAsync(
        InstallationRegistrationRequestV1 request,
        HttpContext context,
        IInstallationService service,
        PeerOnQMetrics metrics,
        CancellationToken cancellationToken)
    {
        EnsureInstallationOwner(context.User, request.InstallationId);
        var result = await service.RegisterAsync(
            context.User.RequireDeviceAccessPrincipal(), request, cancellationToken);
        metrics.InstallationRegistered(request.Platform.ToString(), request.Architecture.ToString());
        return Results.Ok(result);
    }

    private static async Task<IResult> HeartbeatAsync(
        InstallationHeartbeatRequestV1 request,
        HttpContext context,
        IInstallationService service,
        CancellationToken cancellationToken)
    {
        EnsureInstallationOwner(context.User, request.InstallationId);
        return Results.Ok(await service.HeartbeatAsync(request, cancellationToken));
    }

    private static async Task<IResult> UpdateVersionAsync(
        InstallationVersionRequestV1 request,
        HttpContext context,
        IInstallationService service,
        CancellationToken cancellationToken)
    {
        EnsureInstallationOwner(context.User, request.InstallationId);
        await service.UpdateVersionAsync(request, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> UnregisterAsync(
        InstallationUnregisterRequestV1 request,
        HttpContext context,
        IInstallationService service,
        CancellationToken cancellationToken)
    {
        EnsureInstallationOwner(context.User, request.InstallationId);
        await service.UnregisterAsync(request, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> IssueDeviceChallengeAsync(
        DeviceRegistrationRequestV1 request,
        IDeviceRegistrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.IssueChallengeAsync(request, cancellationToken));

    private static async Task<IResult> AuthenticateDeviceAsync(
        DeviceAuthenticationRequestV1 request,
        IDeviceRegistrationService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.AuthenticateAsync(request, cancellationToken));

    private static async Task<IResult> RecordSessionAsync(
        ClientSessionLifecycleEventV1 request,
        SessionEventKindV1 expectedKind,
        HttpContext context,
        ISessionTelemetryService service,
        PeerOnQMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (request.Kind != expectedKind)
            throw new ArgumentException("The session event kind does not match the endpoint.", nameof(request));
        await service.RecordClientLifecycleAsync(context.User.RequireDeviceAccessPrincipal(), request, cancellationToken);
        if (request.Kind == SessionEventKindV1.Ended)
        {
            var succeeded = request.EndReason == SessionEndReasonV1.Completed;
            metrics.SessionCompleted(succeeded);
            if (!succeeded) metrics.SessionFailed(request.FailureStage ?? "unknown");
        }
        return Results.Accepted();
    }

    private static async Task<IResult> HeartbeatSessionAsync(
        ClientSessionHeartbeatV1 request,
        HttpContext context,
        ISessionTelemetryService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.HeartbeatAsync(
            context.User.RequireDeviceAccessPrincipal(),
            request,
            cancellationToken));

    private static async Task<IResult> RecordUpdateAsync(
        ClientUpdateEventV1 request,
        HttpContext context,
        IReleaseTelemetryService service,
        PeerOnQMetrics metrics,
        CancellationToken cancellationToken)
    {
        await service.RecordClientUpdateEventAsync(context.User.RequireDeviceAccessPrincipal(), request, cancellationToken);
        if (request.Kind == UpdateEventKindV1.Failed) metrics.UpdateFailed("update");
        return Results.Accepted();
    }

    private static async Task<IResult> CreateDiagnosticsRequestAsync(
        DiagnosticCreateRequestV1 request,
        HttpContext context,
        IDiagnosticsService service,
        CancellationToken cancellationToken)
    {
        EnsureInstallationOwner(context.User, request.InstallationId);
        var result = await service.CreateRequestAsync(context.User.RequireDeviceAccessPrincipal(), request, cancellationToken);
        return Results.Created($"/v1/diagnostics/{result.DiagnosticId:D}/status", result);
    }

    private static async Task<IResult> CompleteDiagnosticsUploadAsync(
        HttpRequest request,
        DiagnosticUploadProcessor processor,
        DiagnosticUploadGate uploadGate,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            throw new ApiProblemException(StatusCodes.Status415UnsupportedMediaType, "multipart_required", "A multipart diagnostic upload is required.");

        using var uploadLease = await uploadGate.TryAcquireAsync(cancellationToken);
        if (uploadLease is null)
            throw new ApiProblemException(StatusCodes.Status429TooManyRequests, "diagnostic_upload_busy", "The diagnostic upload capacity is temporarily full.");

        var form = await request.ReadFormAsync(cancellationToken);
        var archive = form.Files.GetFile("archive")
            ?? throw new ArgumentException("The diagnostic archive is required.", nameof(request));
        if (!Guid.TryParse(form["diagnosticId"].FirstOrDefault(), out var diagnosticId))
            throw new ArgumentException("The diagnostic ID is invalid.", nameof(request));

        var result = await processor.ProcessAsync(
            request.HttpContext.User.RequireDeviceAccessPrincipal(),
            diagnosticId,
            form["uploadToken"].FirstOrDefault() ?? string.Empty,
            form["sha256Base64"].FirstOrDefault() ?? string.Empty,
            archive,
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetDiagnosticsStatusAsync(
        Guid diagnosticId,
        HttpContext context,
        IDiagnosticsService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetStatusAsync(context.User.RequireDeviceAccessPrincipal(), diagnosticId, cancellationToken));

    private static void EnsureInstallationOwner(System.Security.Claims.ClaimsPrincipal principal, Guid installationId)
    {
        if (installationId == Guid.Empty || principal.RequireInstallationId() != installationId)
        {
            throw new UnauthorizedAccessException("The access token does not own the requested installation.");
        }
    }
}
