using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Observability;

namespace PeerOnQ.Admin.Api;

public sealed class AdminAuthService(
    IAdminIdentityRepository identities,
    ICloudUnitOfWork unitOfWork,
    CloudDbContext db,
    AdminPasswordService passwords,
    AdminTokenService tokens,
    IAdminMfaChallengeStore challenges,
    IPrivacyHasher privacyHasher,
    IDataProtectionProvider dataProtection,
    AdminRequestContext requestContext,
    AdminAuditWriter audit,
    IOptions<AdminAuthenticationOptions> options,
    IHostEnvironment environment,
    TimeProvider timeProvider)
{
    public const string RefreshCookie = "__Host-peeronq_refresh";
    public const string CsrfCookie = "__Host-peeronq_csrf";
    public const string CsrfHeader = "X-CSRF-Token";
    private readonly IDataProtector _mfaProtector = dataProtection.CreateProtector("PeerOnQ.Admin.Mfa.v1");

    public async Task<AdminAuthResponse> LoginAsync(AdminLoginRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        if (request.Password is null
            || request.Password.Length is 0 or > 1024
            || string.IsNullOrWhiteSpace(request.Email)
            || request.Email.Length > 320)
            throw InvalidCredentials();

        AdminUser? user = null;
        try { user = await identities.FindUserByEmailAsync(AdminUser.NormalizeEmail(request.Email), cancellationToken); }
        catch (ArgumentException) { }

        var verification = passwords.Verify(user, request.Password);
        var now = timeProvider.GetUtcNow();
        if (user is null || verification == PasswordVerificationResult.Failed)
        {
            if (user is not null)
            {
                user.RecordFailedLogin(now, options.Value.MaxFailedAttempts, TimeSpan.FromMinutes(options.Value.LockoutMinutes));
            }
            audit.Add(context, user?.Id.ToString("N") ?? AnonymousActor(request.Email), "admin.login", "AdminSession", null, AuditResult.Failed, "invalid_credentials", requestContext, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials();
        }

        if (user.IsLocked(now))
        {
            audit.Add(context, user.Id.ToString("N"), "admin.login", "AdminSession", null, AuditResult.Denied, "account_locked", requestContext, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.ChangePasswordHash(passwords.Hash(user, request.Password));
        }

        var roles = await identities.GetRolesAsync(user.Id, cancellationToken);
        if (roles.Count == 0)
        {
            audit.Add(context, user.Id.ToString("N"), "admin.login", "AdminSession", null, AuditResult.Denied, "no_roles", requestContext, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "admin_role_required", "The admin account has no assigned role.");
        }

        var requiresMfa = roles.Any(role => role != AdminRoleKind.ReadOnlyAnalyst);
        if (requiresMfa && !user.MfaEnabled)
        {
            audit.Add(context, user.Id.ToString("N"), "admin.login", "AdminSession", null, AuditResult.Denied, "mfa_enrollment_required", requestContext, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "mfa_enrollment_required", "MFA enrollment is required for this role.");
        }

        var bypassMfa = AdminAuthenticationConfiguration.IsMfaBypassEnabled(environment, options.Value);
        if (user.MfaEnabled && !bypassMfa)
        {
            var challenge = await challenges.CreateAsync(user.Id, requestContext.ChallengeContext(context), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return new AdminAuthResponse("mfa_required", challenge, null, null);
        }

        if (bypassMfa)
        {
            audit.Add(
                context,
                user.Id.ToString("N"),
                "admin.mfa.development_bypass",
                "AdminSession",
                null,
                AuditResult.Succeeded,
                "explicit_development_opt_in",
                requestContext,
                now);
        }

        return await CreateSessionAsync(user, roles, mfaSatisfied: bypassMfa, context, cancellationToken);
    }

    public async Task<AdminAuthResponse> VerifyMfaAsync(AdminMfaVerifyRequest request, HttpContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64)
            throw InvalidCredentials();
        var challenge = await challenges.ConsumeAsync(request.MfaChallengeId, requestContext.ChallengeContext(context), cancellationToken)
            ?? throw InvalidCredentials();
        var user = await identities.FindUserByIdAsync(challenge.UserId, cancellationToken)
            ?? throw InvalidCredentials();
        var now = timeProvider.GetUtcNow();
        if (user.IsLocked(now) || !user.MfaEnabled || user.MfaSecretCiphertext is null)
            throw InvalidCredentials();

        var verification = await VerifyMfaCodeAsync(user, request.Code, now, cancellationToken);
        if (!verification.Valid)
        {
            user.RecordFailedLogin(now, options.Value.MaxFailedAttempts, TimeSpan.FromMinutes(options.Value.LockoutMinutes));
            audit.Add(context, user.Id.ToString("N"), "admin.mfa.verify", "AdminSession", null, AuditResult.Failed, "invalid_mfa", requestContext, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials();
        }

        if (verification.RecoveryCode is not null) verification.RecoveryCode.MarkUsed(now);
        var roles = await identities.GetRolesAsync(user.Id, cancellationToken);
        return await CreateSessionAsync(user, roles, mfaSatisfied: true, context, cancellationToken);
    }

    public async Task<AdminRefreshResponse> RefreshAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ValidateCsrf(context);
        var rawToken = context.Request.Cookies[RefreshCookie];
        if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length > 512) throw InvalidCredentials();
        var session = await identities.FindSessionByRefreshTokenHashAsync(tokens.HashRefreshToken(rawToken), cancellationToken)
            ?? throw InvalidCredentials();
        var now = timeProvider.GetUtcNow();
        if (!session.IsActive(now))
        {
            if (session.ReplacedBySessionId is not null)
            {
                var activeFamily = await db.AdminSessions.Where(value => value.AdminUserId == session.AdminUserId && value.RevokedAtUtc == null)
                    .ToListAsync(cancellationToken);
                foreach (var active in activeFamily) active.Revoke(now);
                audit.Add(context, session.AdminUserId.ToString("N"), "admin.refresh.replay", "AdminSession", session.Id.ToString("N"), AuditResult.Denied, "refresh_replay", requestContext, now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            throw InvalidCredentials();
        }

        var user = await identities.FindUserByIdAsync(session.AdminUserId, cancellationToken)
            ?? throw InvalidCredentials();
        if (user.IsLocked(now))
        {
            session.Revoke(now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            throw InvalidCredentials();
        }
        var roles = await identities.GetRolesAsync(user.Id, cancellationToken);
        var refreshToken = tokens.CreateRefreshToken();
        var replacement = new AdminSession(
            Guid.NewGuid(), user.Id, tokens.HashRefreshToken(refreshToken), now,
            now.AddHours(options.Value.RefreshTokenHours), AdminRequestContext.UserAgentSummary(context));
        identities.AddSession(replacement);
        session.Revoke(now, replacement.Id);
        audit.Add(context, user.Id.ToString("N"), "admin.session.rotate", "AdminSession", replacement.Id.ToString("N"), AuditResult.Succeeded, null, requestContext, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        SetSessionCookies(context, refreshToken, replacement.ExpiresAtUtc);
        var access = tokens.IssueAccessToken(user.Id, replacement.Id, roles, user.MfaEnabled);
        return new AdminRefreshResponse(access.Token, access.ExpiresAtUtc);
    }

    public async Task LogoutAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ValidateCsrf(context);
        var rawToken = context.Request.Cookies[RefreshCookie];
        if (!string.IsNullOrWhiteSpace(rawToken) && rawToken.Length <= 512)
        {
            var session = await identities.FindSessionByRefreshTokenHashAsync(tokens.HashRefreshToken(rawToken), cancellationToken);
            if (session is not null && session.IsActive(timeProvider.GetUtcNow()))
            {
                var now = timeProvider.GetUtcNow();
                session.Revoke(now);
                audit.Add(context, session.AdminUserId.ToString("N"), "admin.logout", "AdminSession", session.Id.ToString("N"), AuditResult.Succeeded, null, requestContext, now);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        DeleteSessionCookies(context);
    }

    private async Task<AdminAuthResponse> CreateSessionAsync(
        AdminUser user,
        IReadOnlySet<AdminRoleKind> roles,
        bool mfaSatisfied,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var refreshToken = tokens.CreateRefreshToken();
        var session = new AdminSession(
            Guid.NewGuid(), user.Id, tokens.HashRefreshToken(refreshToken), now,
            now.AddHours(options.Value.RefreshTokenHours), AdminRequestContext.UserAgentSummary(context));
        identities.AddSession(session);
        user.RecordSuccessfulLogin(now);
        audit.Add(context, user.Id.ToString("N"), "admin.login", "AdminSession", session.Id.ToString("N"), AuditResult.Succeeded, null, requestContext, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        SetSessionCookies(context, refreshToken, session.ExpiresAtUtc);
        var access = tokens.IssueAccessToken(user.Id, session.Id, roles, mfaSatisfied);
        return new AdminAuthResponse("authenticated", null, access.Token, access.ExpiresAtUtc);
    }

    private async Task<(bool Valid, AdminRecoveryCode? RecoveryCode)> VerifyMfaCodeAsync(
        AdminUser user,
        string code,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        byte[] secret;
        try { secret = _mfaProtector.Unprotect(user.MfaSecretCiphertext!); }
        catch (CryptographicException)
        {
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "mfa_key_unavailable", "MFA verification is temporarily unavailable.");
        }
        try
        {
            if (TotpVerifier.Verify(secret, code.Trim(), now)) return (true, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        var recoveryHash = privacyHasher.ComputeHash("admin-recovery", NormalizeRecoveryCode(code));
        var recoveryCode = await identities.FindUnusedRecoveryCodeAsync(user.Id, recoveryHash, cancellationToken);
        return (recoveryCode is not null, recoveryCode);
    }

    private void SetSessionCookies(HttpContext context, string refreshToken, DateTimeOffset expiresAtUtc)
    {
        var common = new CookieOptions
        {
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAtUtc,
            IsEssential = true,
        };
        context.Response.Cookies.Append(RefreshCookie, refreshToken, new CookieOptions
        {
            Secure = common.Secure,
            SameSite = common.SameSite,
            Path = common.Path,
            Expires = common.Expires,
            IsEssential = true,
            HttpOnly = true,
        });
        context.Response.Cookies.Append(CsrfCookie, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), new CookieOptions
        {
            Secure = common.Secure,
            SameSite = common.SameSite,
            Path = common.Path,
            Expires = common.Expires,
            IsEssential = true,
            HttpOnly = false,
        });
        context.Response.Headers.CacheControl = "no-store";
    }

    private static void DeleteSessionCookies(HttpContext context)
    {
        var options = new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" };
        context.Response.Cookies.Delete(RefreshCookie, options);
        context.Response.Cookies.Delete(CsrfCookie, options);
        context.Response.Headers.CacheControl = "no-store";
    }

    internal static void ValidateCsrf(HttpContext context)
    {
        var cookie = context.Request.Cookies[CsrfCookie];
        var header = context.Request.Headers[CsrfHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(header)) throw new UnauthorizedAccessException("CSRF validation failed.");
        var left = System.Text.Encoding.UTF8.GetBytes(cookie);
        var right = System.Text.Encoding.UTF8.GetBytes(header);
        if (left.Length != right.Length || !CryptographicOperations.FixedTimeEquals(left, right))
            throw new UnauthorizedAccessException("CSRF validation failed.");
    }

    private string AnonymousActor(string? email)
    {
        var hash = privacyHasher.ComputeHash("admin-login-email", email?.Trim().ToLowerInvariant() ?? "invalid");
        return $"anonymous:{Convert.ToHexString(hash)[..16]}";
    }

    private static string NormalizeRecoveryCode(string code) => code.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private static ApiProblemException InvalidCredentials() =>
        new(StatusCodes.Status401Unauthorized, "invalid_admin_credentials", "The admin credentials are invalid.");
}
