using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application.Abstractions;

namespace PeerOnQ.Observability;

public static class DeviceAccessAuthentication
{
    public const string Scheme = "PeerOnQDevice";
    public const string Policy = "device";
    public const string DeviceIdClaim = "device_id";
    public const string InstallationIdClaim = "installation_id";

    public static AuthenticationBuilder AddPeerOnQDeviceAuthentication(
        this IServiceCollection services,
        Action<DeviceAccessAuthenticationOptions>? configure = null)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(DeviceIdClaim)
                .RequireClaim(InstallationIdClaim));

        return services.AddAuthentication(Scheme)
            .AddScheme<DeviceAccessAuthenticationOptions, DeviceAccessAuthenticationHandler>(
                Scheme,
                options => configure?.Invoke(options));
    }
}

public sealed class DeviceAccessAuthenticationOptions : AuthenticationSchemeOptions
{
    public PathString QueryTokenPath { get; set; }
    public bool AllowQueryToken { get; set; }
    public bool RequireHttpsForQueryToken { get; set; } = true;
    public int MaximumTokenLength { get; set; } = 4096;
}

public sealed class DeviceAccessAuthenticationHandler(
    IOptionsMonitor<DeviceAccessAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDeviceAccessTokenValidator tokenValidator,
    IDeviceAccessStateValidator accessState,
    PeerOnQMetrics metrics,
    TimeProvider timeProvider)
    : AuthenticationHandler<DeviceAccessAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractBearerToken(Request.Headers.Authorization);
        if (token is null && Options.AllowQueryToken && Request.Path.StartsWithSegments(Options.QueryTokenPath))
        {
            if (Options.RequireHttpsForQueryToken && !Request.IsHttps)
            {
                metrics.AuthenticationFailed("invalid");
                return AuthenticateResult.Fail("Query-string access tokens require HTTPS.");
            }

            token = Request.Query["access_token"].FirstOrDefault();
        }

        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();
        if (token.Length > Options.MaximumTokenLength)
        {
            metrics.AuthenticationFailed("invalid");
            return AuthenticateResult.Fail("The access token is invalid.");
        }

        try
        {
            var principal = await tokenValidator.ValidateAsync(token, Context.RequestAborted);
            if (principal is null || principal.ExpiresAtUtc <= timeProvider.GetUtcNow())
            {
                metrics.AuthenticationFailed("expired");
                return AuthenticateResult.Fail("The access token is invalid or expired.");
            }
            if (!await accessState.IsActiveAsync(principal, Context.RequestAborted))
            {
                metrics.AuthenticationFailed("revoked");
                return AuthenticateResult.Fail("The device access has been revoked or blocked.");
            }

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, principal.DeviceId.ToString("N")),
                new Claim(DeviceAccessAuthentication.DeviceIdClaim, principal.DeviceId.ToString("N")),
                new Claim(DeviceAccessAuthentication.InstallationIdClaim, principal.InstallationId.ToString("N")),
                new Claim("exp", principal.ExpiresAtUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ], Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return AuthenticateResult.Success(ticket);
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(
                "Device token validation failed with {EventName} and {ExceptionType}",
                "device.authentication.failed",
                exception.GetType().Name);
            metrics.AuthenticationFailed("invalid");
            return AuthenticateResult.Fail("The access token could not be validated.");
        }
    }

    private static string? ExtractBearerToken(string? authorization)
    {
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return authorization[prefix.Length..].Trim();
    }
}

public static class DevicePrincipalExtensions
{
    public static Guid RequireDeviceId(this ClaimsPrincipal principal) =>
        ParseRequiredGuid(principal, DeviceAccessAuthentication.DeviceIdClaim);

    public static Guid RequireInstallationId(this ClaimsPrincipal principal) =>
        ParseRequiredGuid(principal, DeviceAccessAuthentication.InstallationIdClaim);

    public static DeviceAccessPrincipal RequireDeviceAccessPrincipal(this ClaimsPrincipal principal)
    {
        if (!long.TryParse(principal.FindFirst("exp")?.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var expiresUnix))
            throw new UnauthorizedAccessException("The device access expiry claim is missing.");
        return new DeviceAccessPrincipal(
            principal.RequireDeviceId(),
            principal.RequireInstallationId(),
            DateTimeOffset.FromUnixTimeSeconds(expiresUnix));
    }

    private static Guid ParseRequiredGuid(ClaimsPrincipal principal, string claimType) =>
        Guid.TryParseExact(principal.FindFirst(claimType)?.Value, "N", out var value) && value != Guid.Empty
            ? value
            : throw new UnauthorizedAccessException("A required device access claim is missing.");
}
