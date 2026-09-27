using PeerOnQ.Cloud.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace PeerOnQ.Cloud.Api;

public static class CustomerPortalEndpoints
{
    public static IEndpointRouteBuilder MapCustomerPortalV1(this IEndpointRouteBuilder endpoints)
    {
        var portal = endpoints.MapGroup("/portal/v1");
        var auth = portal.MapGroup("/auth");
        auth.MapGet("/capabilities", (HttpContext context, CustomerAccountService service) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(service.GetCapabilities());
        }).AllowAnonymous().RequireRateLimiting("customer-capabilities");
        auth.MapPost("/register", RegisterAsync).AllowAnonymous().RequireRateLimiting("customer-registration");
        auth.MapPost("/verify-email", VerifyEmailAsync).AllowAnonymous().RequireRateLimiting("customer-authentication");
        auth.MapPost("/verify-email/resend", ResendVerificationAsync).AllowAnonymous().RequireRateLimiting("customer-email");
        auth.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting("customer-authentication");
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting("customer-authentication");
        auth.MapPost("/password-reset/request", RequestPasswordResetAsync).AllowAnonymous().RequireRateLimiting("customer-authentication");
        auth.MapPost("/password-reset/complete", ResetPasswordAsync).AllowAnonymous().RequireRateLimiting("customer-authentication");
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization(CustomerPortalAuthentication.Policy);

        var account = portal.MapGroup("/account").RequireAuthorization(CustomerPortalAuthentication.Policy);
        account.MapGet("/profile", async (HttpContext context, CustomerAccountService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetProfileAsync(context.User.RequireCustomerAccountId(), cancellationToken)));
        account.MapPut("/profile", UpdateProfileAsync);
        account.MapPost("/password/change", ChangePasswordAsync).RequireRateLimiting("customer-sensitive");
        account.MapPost("/mfa/setup", BeginMfaSetupAsync).RequireRateLimiting("customer-sensitive");
        account.MapPost("/mfa/confirm", ConfirmMfaAsync).RequireRateLimiting("customer-sensitive");
        account.MapDelete("/mfa", DisableMfaAsync).RequireRateLimiting("customer-sensitive");
        account.MapGet("/sessions", async (HttpContext context, CustomerAccountService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetSessionsAsync(context.User.RequireCustomerAccountId(), cancellationToken)));
        account.MapDelete("/sessions/{sessionId:guid}", RevokeSessionAsync);
        account.MapGet("/trusted-devices", async (HttpContext context, CustomerAccountService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetTrustedDevicesAsync(context.User.RequireCustomerAccountId(), cancellationToken)));
        account.MapDelete("/trusted-devices/{trustedDeviceId:guid}", RevokeTrustedDeviceAsync);
        account.MapPost("/data-requests", RequestDataAsync);

        portal.MapCustomerOrganizations();
        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(RegisterCustomerRequest request, CustomerAccountService service, CancellationToken cancellationToken) =>
        Results.Accepted(value: await service.RegisterAsync(request, cancellationToken));

    private static async Task<IResult> VerifyEmailAsync(VerifyEmailRequest request, CustomerAccountService service, CancellationToken cancellationToken)
    {
        await service.VerifyEmailAsync(request.Token, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ResendVerificationAsync(PasswordResetRequest request, CustomerAccountService service, CancellationToken cancellationToken)
    {
        await service.ResendVerificationAsync(request.Email, cancellationToken);
        return Results.Accepted(value: new { message = "Request accepted. Check your inbox; if no message arrives, try again later." });
    }

    private static async Task<IResult> ChangePasswordAsync(ChangeCustomerPasswordRequest request, HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        await service.ChangePasswordAsync(context.User.RequireCustomerAccountId(), context.User.RequireCustomerSessionId(), request, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> LoginAsync(LoginCustomerRequest request, HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        var result = await service.LoginAsync(request, context.Request.Headers.UserAgent.ToString(), cancellationToken);
        CustomerPortalAuthentication.SetSessionCookies(context, result.AccessToken, result.AccessExpiresAtUtc, result.RefreshToken, result.RefreshExpiresAtUtc);
        return Results.Ok(new { result.AccountId, result.SessionId, result.AccessExpiresAtUtc });
    }

    private static async Task<IResult> RefreshAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshCustomerRequest? request,
        HttpContext context,
        CustomerAccountService service,
        CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        var rawRefresh = context.Request.Cookies[CustomerPortalAuthentication.RefreshCookie] ?? request?.RefreshToken ?? string.Empty;
        var result = await service.RefreshAsync(rawRefresh, context.Request.Headers.UserAgent.ToString(), cancellationToken);
        CustomerPortalAuthentication.SetSessionCookies(context, result.AccessToken, result.AccessExpiresAtUtc, result.RefreshToken, result.RefreshExpiresAtUtc);
        return Results.Ok(new { result.AccountId, result.SessionId, result.AccessExpiresAtUtc });
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        await service.LogoutAsync(context.User.RequireCustomerSessionId(), cancellationToken);
        CustomerPortalAuthentication.ClearSessionCookies(context);
        return Results.NoContent();
    }

    private static async Task<IResult> RequestPasswordResetAsync(PasswordResetRequest request, CustomerAccountService service, CancellationToken cancellationToken)
    {
        await service.RequestPasswordResetAsync(request.Email, cancellationToken);
        return Results.Accepted();
    }

    private static async Task<IResult> ResetPasswordAsync(PasswordResetCompleteRequest request, CustomerAccountService service, CancellationToken cancellationToken)
    {
        await service.ResetPasswordAsync(request.Token, request.NewPassword, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateProfileAsync(UpdateCustomerProfileRequest request, HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        await service.UpdateProfileAsync(context.User.RequireCustomerAccountId(), request.DisplayName, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> BeginMfaSetupAsync(HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        return Results.Ok(await service.BeginMfaSetupAsync(context.User.RequireCustomerAccountId(), cancellationToken));
    }

    private static async Task<IResult> ConfirmMfaAsync(MfaConfirmRequest request, HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        var codes = await service.ConfirmMfaAsync(context.User.RequireCustomerAccountId(), request.SetupToken, request.Code, cancellationToken);
        return Results.Ok(new MfaConfirmResult(codes));
    }

    private static async Task<IResult> DisableMfaAsync([FromBody] MfaDisableRequest request, HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        await service.DisableMfaAsync(context.User.RequireCustomerAccountId(), request.Password, request.Code, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeSessionAsync(Guid sessionId, HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        await service.RevokeSessionAsync(context.User.RequireCustomerAccountId(), sessionId, cancellationToken);
        if (sessionId == context.User.RequireCustomerSessionId()) CustomerPortalAuthentication.ClearSessionCookies(context);
        return Results.NoContent();
    }

    private static async Task<IResult> RevokeTrustedDeviceAsync(Guid trustedDeviceId, HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        await service.RevokeTrustedDeviceAsync(context.User.RequireCustomerAccountId(), trustedDeviceId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RequestDataAsync(AccountDataRequestDto request, HttpContext context, CustomerAccountService service, CancellationToken cancellationToken)
    {
        CustomerPortalAuthentication.ValidateCsrf(context);
        var id = await service.RequestDataAsync(context.User.RequireCustomerAccountId(), request.Kind, cancellationToken);
        if (request.Kind == AccountDataRequestKind.Delete) CustomerPortalAuthentication.ClearSessionCookies(context);
        return Results.Accepted($"/portal/v1/account/data-requests/{id:D}", new { id });
    }
}
