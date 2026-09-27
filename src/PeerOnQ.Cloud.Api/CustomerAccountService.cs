using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Observability;

namespace PeerOnQ.Cloud.Api;

public sealed class CustomerAccountService(
    CloudDbContext db,
    CustomerPasswordService passwords,
    CustomerTokenService tokens,
    IDataProtectionProvider dataProtection,
    ICustomerMailSender mail,
    IOptions<CustomerPortalOptions> options,
    IHttpContextAccessor httpContextAccessor)
{
    private readonly IDataProtector _mfaProtector = dataProtection.CreateProtector("PeerOnQ.CustomerPortal.Mfa.v1");
    private readonly ITimeLimitedDataProtector _mfaSetupProtector = dataProtection
        .CreateProtector("PeerOnQ.CustomerPortal.MfaSetup.v1")
        .ToTimeLimitedDataProtector();

    public CustomerAuthCapabilities GetCapabilities() => new(
        options.Value.RegistrationMode.ToString(),
        options.Value.RegistrationMode != CustomerRegistrationMode.Closed,
        options.Value.RequireEmailVerification,
        mail.IsEnabled,
        options.Value.EnableMfa,
        CustomerPasswordService.Rules);

    public async Task<RegistrationResult> RegisterAsync(RegisterCustomerRequest request, CancellationToken cancellationToken)
    {
        if (options.Value.RegistrationMode == CustomerRegistrationMode.Closed)
            throw Problem(StatusCodes.Status403Forbidden, "registration_disabled", "Account registration is not available.");
        if (options.Value.RequireEmailVerification) mail.EnsureEnabled();
        var now = DateTimeOffset.UtcNow;
        var email = CustomerAccount.NormalizeEmail(request.Email);
        CustomerPasswordService.ValidatePassword(request.Password);
        if (await db.CustomerAccounts.AnyAsync(value => value.Email == email, cancellationToken))
            throw Problem(StatusCodes.Status409Conflict, "account_exists", "An account with this email already exists.");

        OrganizationInvitation? invitation = null;
        byte[]? invitationHash = null;
        if (!string.IsNullOrWhiteSpace(request.InvitationToken))
        {
            invitationHash = CustomerTokenService.HashOpaqueToken(request.InvitationToken);
            invitation = await db.OrganizationInvitations.SingleOrDefaultAsync(value => value.TokenHash.SequenceEqual(invitationHash), cancellationToken);
        }

        if (options.Value.RegistrationMode == CustomerRegistrationMode.InvitationOnly && invitation is null)
            throw Problem(StatusCodes.Status403Forbidden, "registration_disabled", "Account registration is not available.");
        if ((!string.IsNullOrWhiteSpace(request.InvitationToken) && invitation is null) ||
            (invitation is not null && !invitation.CanAccept(email, invitationHash!, now)))
            throw Problem(StatusCodes.Status400BadRequest, "invitation_invalid", "The invitation is invalid, expired, revoked, already used, or belongs to another account.");

        var account = new CustomerAccount(Guid.NewGuid(), email, request.DisplayName, "pending-password-hash", now);
        account.ChangePasswordHash(passwords.Hash(account, request.Password));
        db.CustomerAccounts.Add(account);

        Guid organizationId;
        if (invitation is not null)
        {
            invitation.Accept(email, invitationHash!, now);
            organizationId = invitation.OrganizationId;
            db.OrganizationMemberships.Add(new OrganizationMembership(organizationId, account.Id, invitation.Role, now));
        }
        else
        {
            var organization = new Organization(Guid.NewGuid(), $"{account.DisplayName}'s organization", account.Id, now);
            organizationId = organization.Id;
            db.Organizations.Add(organization);
            db.OrganizationMemberships.Add(new OrganizationMembership(organization.Id, account.Id, CustomerRoleKind.Owner, now));
            db.OrganizationPolicies.Add(new OrganizationPolicy(organization.Id));
        }

        string? verificationToken = null;
        if (options.Value.RequireEmailVerification)
        {
            verificationToken = CustomerTokenService.IssueOpaqueToken();
            db.CustomerAccountTokens.Add(new CustomerAccountToken(Guid.NewGuid(), account.Id, CustomerTokenPurpose.EmailVerification,
                CustomerTokenService.HashOpaqueToken(verificationToken), now, now.AddMinutes(options.Value.EmailTokenMinutes)));
        }
        else
        {
            account.VerifyEmail();
        }

        AddEvent(account.Id, organizationId, "account.registered", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
        if (verificationToken is not null)
        {
            var link = $"{options.Value.PortalBaseUrl.TrimEnd('/')}/verify-email?token={Uri.EscapeDataString(verificationToken)}";
            await mail.SendAsync(account.Email, "Verify your PeerOnQ account", $"Verify your account: {link}", cancellationToken);
        }
        return new RegistrationResult(account.Id, options.Value.RequireEmailVerification);
    }

    public async Task VerifyEmailAsync(string rawToken, CancellationToken cancellationToken)
    {
        var hash = CustomerTokenService.HashOpaqueToken(rawToken);
        var token = await db.CustomerAccountTokens.SingleOrDefaultAsync(value =>
            value.Purpose == CustomerTokenPurpose.EmailVerification && value.TokenHash.SequenceEqual(hash), cancellationToken)
            ?? throw Problem(StatusCodes.Status400BadRequest, "token_invalid", "The verification token is invalid or expired.");
        if (!token.CanConsume(hash, DateTimeOffset.UtcNow)) throw Problem(StatusCodes.Status400BadRequest, "token_invalid", "The verification token is invalid or expired.");
        var account = await db.CustomerAccounts.SingleAsync(value => value.Id == token.AccountId, cancellationToken);
        token.Consume(hash, DateTimeOffset.UtcNow);
        account.VerifyEmail();
        AddEvent(account.Id, null, "account.email_verified", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LoginSessionResult> LoginAsync(LoginCustomerRequest request, string userAgent, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        CustomerAccount? account;
        try { account = await db.CustomerAccounts.SingleOrDefaultAsync(value => value.Email == CustomerAccount.NormalizeEmail(request.Email), cancellationToken); }
        catch (ArgumentException) { account = null; }
        if (account is null || passwords.Verify(account, request.Password) == PasswordVerificationResult.Failed)
        {
            if (account is not null)
            {
                account.RecordFailedLogin(now, 8, TimeSpan.FromMinutes(15));
                AddEvent(account.Id, null, "account.login_failed", AuditResult.Failed);
                await db.SaveChangesAsync(cancellationToken);
            }
            throw InvalidCredentials();
        }
        if (!account.IsLoginAllowed(now, options.Value.RequireEmailVerification)) throw InvalidCredentials();
        if (options.Value.EnableMfa && account.MfaEnabled && !await VerifyMfaOrRecoveryAsync(account, request.MfaCode, now, cancellationToken))
            throw Problem(StatusCodes.Status401Unauthorized, "mfa_required", "A valid MFA or recovery code is required.");

        account.RecordSuccessfulLogin(now);
        var result = CreateSession(account, Guid.NewGuid(), userAgent, now);
        AddEvent(account.Id, null, "account.login_succeeded", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<LoginSessionResult> RefreshAsync(string rawRefreshToken, string userAgent, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionIdText = rawRefreshToken?.Split('.', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!Guid.TryParse(sessionIdText, out var sessionId)) throw InvalidRefresh();
        var hash = CustomerTokenService.HashOpaqueToken(rawRefreshToken ?? string.Empty);
        var session = await db.CustomerSessions.SingleOrDefaultAsync(value => value.Id == sessionId, cancellationToken) ?? throw InvalidRefresh();
        if (!CryptographicOperations.FixedTimeEquals(session.RefreshTokenHash, hash)) throw InvalidRefresh();
        if (!session.IsActive(now))
        {
            var family = await db.CustomerSessions.Where(value => value.FamilyId == session.FamilyId && value.RevokedAtUtc == null).ToListAsync(cancellationToken);
            foreach (var member in family) member.Revoke(now);
            AddEvent(session.AccountId, null, "account.refresh_replay", AuditResult.Denied);
            await db.SaveChangesAsync(cancellationToken);
            throw InvalidRefresh();
        }

        var account = await db.CustomerAccounts.SingleAsync(value => value.Id == session.AccountId, cancellationToken);
        if (!account.IsLoginAllowed(now, options.Value.RequireEmailVerification)) throw InvalidRefresh();
        account.RecordSecurityTokenActivity();
        var replacement = CreateSession(account, session.FamilyId, userAgent, now);
        session.Revoke(now, replacement.SessionId);
        AddEvent(account.Id, null, "account.session_refreshed", AuditResult.Succeeded);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent use of the same single-use refresh token is a replay.
            // Revoke the complete family, including any replacement committed by
            // the winning request, before returning a generic authentication error.
            db.ChangeTracker.Clear();
            await db.CustomerSessions
                .Where(value => value.FamilyId == session.FamilyId && value.RevokedAtUtc == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.RevokedAtUtc, now)
                    .SetProperty(value => value.ConcurrencyVersion, value => value.ConcurrencyVersion + 1), cancellationToken);
            AddEvent(account.Id, null, "account.refresh_replay", AuditResult.Denied);
            await db.SaveChangesAsync(cancellationToken);
            throw InvalidRefresh();
        }
        return replacement;
    }

    public async Task LogoutAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.CustomerSessions.SingleOrDefaultAsync(value => value.Id == sessionId, cancellationToken);
        if (session is null) return;
        session.Revoke(DateTimeOffset.UtcNow);
        AddEvent(session.AccountId, null, "account.logout", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken)
        => await SendAccountTokenAsync(email, CustomerTokenPurpose.PasswordReset, cancellationToken);

    public async Task ResendVerificationAsync(string email, CancellationToken cancellationToken)
        => await SendAccountTokenAsync(email, CustomerTokenPurpose.EmailVerification, cancellationToken);

    private async Task SendAccountTokenAsync(string email, CustomerTokenPurpose purpose, CancellationToken cancellationToken)
    {
        mail.EnsureEnabled();
        CustomerAccount? account;
        try { account = await db.CustomerAccounts.SingleOrDefaultAsync(value => value.Email == CustomerAccount.NormalizeEmail(email), cancellationToken); }
        catch (ArgumentException) { account = null; }
        if (account is null || account.Status != CustomerAccountStatus.Active) return;
        if (purpose == CustomerTokenPurpose.EmailVerification && account.EmailVerified) return;
        var now = DateTimeOffset.UtcNow;
        var previous = await db.CustomerAccountTokens.Where(value => value.AccountId == account.Id &&
            value.Purpose == purpose && value.UsedAtUtc == null && value.ExpiresAtUtc > now).ToListAsync(cancellationToken);
        if (previous.Any(value => value.CreatedAtUtc > now.AddMinutes(-1))) return;
        foreach (var token in previous) token.Invalidate(now);
        account.RecordSecurityTokenActivity();
        var rawToken = CustomerTokenService.IssueOpaqueToken();
        db.CustomerAccountTokens.Add(new CustomerAccountToken(Guid.NewGuid(), account.Id, purpose,
            CustomerTokenService.HashOpaqueToken(rawToken), now, now.AddMinutes(options.Value.EmailTokenMinutes)));
        var verification = purpose == CustomerTokenPurpose.EmailVerification;
        AddEvent(account.Id, null, verification ? "account.verification_requested" : "account.password_reset_requested", AuditResult.Succeeded);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return; }
        var path = verification ? "verify-email" : "reset-password";
        var link = $"{options.Value.PortalBaseUrl.TrimEnd('/')}/{path}?token={Uri.EscapeDataString(rawToken)}";
        try
        {
            await mail.SendAsync(account.Email, verification ? "Verify your PeerOnQ account" : "Reset your PeerOnQ password",
                $"{(verification ? "Verify your account" : "Reset your password")}: {link}", cancellationToken);
        }
        catch (ApiProblemException exception) when (exception.ErrorCode == "customer_mail_unavailable")
        {
            // Delivery failures must not distinguish known accounts from unknown addresses.
            AddEvent(account.Id, null, "account.mail_delivery_failed", AuditResult.Failed);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task ChangePasswordAsync(Guid accountId, Guid currentSessionId, ChangeCustomerPasswordRequest request, CancellationToken cancellationToken)
    {
        var account = await RequireActiveAccountAsync(accountId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (!account.IsLoginAllowed(now, options.Value.RequireEmailVerification) ||
            passwords.Verify(account, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            account.RecordFailedLogin(now, 8, TimeSpan.FromMinutes(15));
            AddEvent(account.Id, null, "account.password_change_failed", AuditResult.Failed);
            await db.SaveChangesAsync(cancellationToken);
            throw Problem(StatusCodes.Status400BadRequest, "password_change_rejected", "The current password could not be verified.");
        }
        account.ChangePasswordHash(passwords.Hash(account, request.NewPassword));
        var sessions = await db.CustomerSessions.Where(value => value.AccountId == accountId &&
            value.Id != currentSessionId && value.RevokedAtUtc == null).ToListAsync(cancellationToken);
        foreach (var session in sessions) session.Revoke(now);
        var resets = await db.CustomerAccountTokens.Where(value => value.AccountId == accountId &&
            value.Purpose == CustomerTokenPurpose.PasswordReset && value.UsedAtUtc == null).ToListAsync(cancellationToken);
        foreach (var reset in resets) reset.Invalidate(now);
        AddEvent(accountId, null, "account.password_changed", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetPasswordAsync(string rawToken, string newPassword, CancellationToken cancellationToken)
    {
        CustomerPasswordService.ValidatePassword(newPassword);
        var hash = CustomerTokenService.HashOpaqueToken(rawToken);
        var token = await db.CustomerAccountTokens.SingleOrDefaultAsync(value =>
            value.Purpose == CustomerTokenPurpose.PasswordReset && value.TokenHash.SequenceEqual(hash), cancellationToken)
            ?? throw Problem(StatusCodes.Status400BadRequest, "token_invalid", "The password-reset token is invalid or expired.");
        var now = DateTimeOffset.UtcNow;
        if (!token.CanConsume(hash, now)) throw Problem(StatusCodes.Status400BadRequest, "token_invalid", "The password-reset token is invalid or expired.");
        var account = await db.CustomerAccounts.SingleAsync(value => value.Id == token.AccountId, cancellationToken);
        token.Consume(hash, now);
        account.ChangePasswordHash(passwords.Hash(account, newPassword));
        var sessions = await db.CustomerSessions.Where(value => value.AccountId == account.Id && value.RevokedAtUtc == null).ToListAsync(cancellationToken);
        foreach (var session in sessions) session.Revoke(now);
        AddEvent(account.Id, null, "account.password_reset_completed", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MfaSetupResult> BeginMfaSetupAsync(Guid accountId, CancellationToken cancellationToken)
    {
        RequireMfaAvailable();
        var account = await RequireActiveAccountAsync(accountId, cancellationToken);
        var secret = RandomNumberGenerator.GetBytes(20);
        var base32 = CustomerTotp.ToBase32(secret);
        var protectedSetup = _mfaSetupProtector.Protect(Convert.ToBase64String(secret), TimeSpan.FromMinutes(10));
        var issuer = Uri.EscapeDataString("PeerOnQ");
        var label = Uri.EscapeDataString($"PeerOnQ:{account.Email}");
        return new MfaSetupResult(base32, protectedSetup, $"otpauth://totp/{label}?secret={base32}&issuer={issuer}&digits=6&period=30");
    }

    public async Task<IReadOnlyList<string>> ConfirmMfaAsync(Guid accountId, string protectedSetup, string code, CancellationToken cancellationToken)
    {
        RequireMfaAvailable();
        var account = await RequireActiveAccountAsync(accountId, cancellationToken);
        byte[] secret;
        try { secret = Convert.FromBase64String(_mfaSetupProtector.Unprotect(protectedSetup, out _)); }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        { throw Problem(StatusCodes.Status400BadRequest, "mfa_setup_invalid", "The MFA setup challenge is invalid or expired."); }
        if (!CustomerTotp.Verify(secret, code, DateTimeOffset.UtcNow)) throw Problem(StatusCodes.Status400BadRequest, "mfa_code_invalid", "The MFA code is invalid.");
        account.EnableMfa(_mfaProtector.Protect(secret));
        var existing = await db.CustomerRecoveryCodes.Where(value => value.AccountId == accountId).ToListAsync(cancellationToken);
        db.CustomerRecoveryCodes.RemoveRange(existing);
        var rawCodes = Enumerable.Range(0, 10).Select(_ => $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}").ToArray();
        foreach (var rawCode in rawCodes)
            db.CustomerRecoveryCodes.Add(new CustomerRecoveryCode(Guid.NewGuid(), accountId, HashRecoveryCode(rawCode), DateTimeOffset.UtcNow));
        AddEvent(account.Id, null, "account.mfa_enabled", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
        return rawCodes;
    }

    public async Task DisableMfaAsync(Guid accountId, string password, string code, CancellationToken cancellationToken)
    {
        RequireMfaAvailable();
        var account = await RequireActiveAccountAsync(accountId, cancellationToken);
        if (passwords.Verify(account, password) == PasswordVerificationResult.Failed ||
            !await VerifyMfaOrRecoveryAsync(account, code, DateTimeOffset.UtcNow, cancellationToken))
            throw InvalidCredentials();
        account.DisableMfa();
        var recoveryCodes = await db.CustomerRecoveryCodes.Where(value => value.AccountId == accountId).ToListAsync(cancellationToken);
        db.CustomerRecoveryCodes.RemoveRange(recoveryCodes);
        AddEvent(account.Id, null, "account.mfa_disabled", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CustomerProfileResult> GetProfileAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await RequireActiveAccountAsync(accountId, cancellationToken);
        return new CustomerProfileResult(account.Id, account.Email, account.DisplayName, account.EmailVerified, account.MfaEnabled, account.CreatedAtUtc);
    }

    public async Task UpdateProfileAsync(Guid accountId, string displayName, CancellationToken cancellationToken)
    {
        var account = await RequireActiveAccountAsync(accountId, cancellationToken);
        account.UpdateProfile(displayName);
        AddEvent(accountId, null, "account.profile_updated", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerSessionResult>> GetSessionsAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.CustomerSessions.AsNoTracking().Where(value => value.AccountId == accountId)
            .OrderByDescending(value => value.CreatedAtUtc)
            .Select(value => new CustomerSessionResult(value.Id, value.UserAgentSummary, value.CreatedAtUtc, value.ExpiresAtUtc, value.RevokedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task RevokeSessionAsync(Guid accountId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.CustomerSessions.SingleOrDefaultAsync(value => value.Id == sessionId && value.AccountId == accountId, cancellationToken)
            ?? throw new KeyNotFoundException();
        session.Revoke(DateTimeOffset.UtcNow);
        AddEvent(accountId, null, "account.session_revoked", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TrustedDeviceResult>> GetTrustedDevicesAsync(Guid accountId, CancellationToken cancellationToken) =>
        await db.CustomerTrustedDevices.AsNoTracking().Where(value => value.AccountId == accountId)
            .OrderByDescending(value => value.CreatedAtUtc)
            .Select(value => new TrustedDeviceResult(value.Id, value.Name, value.CreatedAtUtc, value.ExpiresAtUtc, value.RevokedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task RevokeTrustedDeviceAsync(Guid accountId, Guid trustedDeviceId, CancellationToken cancellationToken)
    {
        var trusted = await db.CustomerTrustedDevices.SingleOrDefaultAsync(value => value.Id == trustedDeviceId && value.AccountId == accountId, cancellationToken)
            ?? throw new KeyNotFoundException();
        trusted.Revoke(DateTimeOffset.UtcNow);
        AddEvent(accountId, null, "account.trusted_device_revoked", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> RequestDataAsync(Guid accountId, AccountDataRequestKind kind, CancellationToken cancellationToken)
    {
        _ = await RequireActiveAccountAsync(accountId, cancellationToken);
        if (await db.AccountDataRequests.AnyAsync(value => value.AccountId == accountId && value.Kind == kind && value.Status == AccountDataRequestStatus.Pending, cancellationToken))
            throw Problem(StatusCodes.Status409Conflict, "request_pending", "An equivalent data request is already pending.");
        var request = new AccountDataRequest(Guid.NewGuid(), accountId, kind, DateTimeOffset.UtcNow);
        db.AccountDataRequests.Add(request);
        AddEvent(accountId, null, kind == AccountDataRequestKind.Export ? "account.export_requested" : "account.deletion_requested", AuditResult.Succeeded);
        if (kind == AccountDataRequestKind.Delete)
        {
            var ownedOrganizations = await db.Organizations.CountAsync(value => value.OwnerAccountId == accountId && value.DeletionRequestedAtUtc == null, cancellationToken);
            if (ownedOrganizations > 0) throw Problem(StatusCodes.Status409Conflict, "owner_transfer_required", "Transfer or delete owned organizations before deleting the account.");
            var account = await db.CustomerAccounts.SingleAsync(value => value.Id == accountId, cancellationToken);
            account.RequestDeletion(DateTimeOffset.UtcNow);
            var sessions = await db.CustomerSessions.Where(value => value.AccountId == accountId && value.RevokedAtUtc == null).ToListAsync(cancellationToken);
            foreach (var session in sessions) session.Revoke(DateTimeOffset.UtcNow);
        }
        await db.SaveChangesAsync(cancellationToken);
        return request.Id;
    }

    private LoginSessionResult CreateSession(CustomerAccount account, Guid familyId, string userAgent, DateTimeOffset now)
    {
        var sessionId = Guid.NewGuid();
        var rawRefresh = $"{sessionId:D}.{CustomerTokenService.IssueOpaqueToken()}";
        var refreshExpires = now.AddDays(options.Value.RefreshTokenDays);
        var session = new CustomerSession(sessionId, account.Id, familyId, CustomerTokenService.HashOpaqueToken(rawRefresh), now, refreshExpires, NormalizeUserAgent(userAgent));
        db.CustomerSessions.Add(session);
        var access = tokens.IssueAccessToken(account, session, now);
        return new LoginSessionResult(account.Id, session.Id, access.Token, access.ExpiresAtUtc, rawRefresh, refreshExpires);
    }

    private async Task<CustomerAccount> RequireActiveAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.CustomerAccounts.SingleOrDefaultAsync(value => value.Id == accountId, cancellationToken) ?? throw new KeyNotFoundException();
        if (account.Status != CustomerAccountStatus.Active) throw new UnauthorizedAccessException();
        return account;
    }

    private async Task<bool> VerifyMfaOrRecoveryAsync(CustomerAccount account, string? code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        if (account.MfaSecretCiphertext is not null)
        {
            try
            {
                var secret = _mfaProtector.Unprotect(account.MfaSecretCiphertext);
                if (CustomerTotp.Verify(secret, code.Trim(), now)) return true;
            }
            catch (CryptographicException) { return false; }
        }
        var hash = HashRecoveryCode(code);
        var recoveryCodes = await db.CustomerRecoveryCodes.Where(value => value.AccountId == account.Id && value.UsedAtUtc == null).ToListAsync(cancellationToken);
        var recovery = recoveryCodes.FirstOrDefault(value => CryptographicOperations.FixedTimeEquals(value.CodeHash, hash));
        if (recovery is null) return false;
        recovery.Consume(hash, now);
        return true;
    }

    private void AddEvent(Guid accountId, Guid? organizationId, string action, AuditResult result) =>
        db.CustomerSecurityEvents.Add(new CustomerSecurityEvent(Guid.NewGuid(), accountId, organizationId, action, result,
            DateTimeOffset.UtcNow, httpContextAccessor.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N"), null));

    private static byte[] HashRecoveryCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant()));
    private void RequireMfaAvailable()
    {
        if (!options.Value.EnableMfa)
            throw Problem(StatusCodes.Status403Forbidden, "customer_mfa_disabled", "Customer MFA is not available.");
    }
    private static string NormalizeUserAgent(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown client" : value.Trim()[..Math.Min(value.Trim().Length, 256)];
    private static ApiProblemException InvalidCredentials() => Problem(StatusCodes.Status401Unauthorized, "invalid_credentials", "The credentials are invalid.");
    private static ApiProblemException InvalidRefresh() => Problem(StatusCodes.Status401Unauthorized, "session_invalid", "The session is invalid or expired.");
    private static ApiProblemException Problem(int status, string code, string title) => new(status, code, title);
}

public sealed record RegisterCustomerRequest(string Email, string DisplayName, string Password, string? InvitationToken);
public sealed record RegistrationResult(Guid AccountId, bool EmailVerificationRequired);
public sealed record VerifyEmailRequest(string Token);
public sealed record LoginCustomerRequest(string Email, string Password, string? MfaCode);
public sealed record RefreshCustomerRequest(string? RefreshToken);
public sealed record PasswordResetRequest(string Email);
public sealed record ChangeCustomerPasswordRequest(string CurrentPassword, string NewPassword);
public sealed record CustomerPasswordRules(int MinLength, int MaxLength, bool RequireUppercase, bool RequireLowercase, bool RequireDigit);
public sealed record CustomerAuthCapabilities(string RegistrationMode, bool RegistrationAvailable,
    bool RequireEmailVerification, bool PasswordResetAvailable, bool MfaAvailable, CustomerPasswordRules PasswordRules);
public sealed record PasswordResetCompleteRequest(string Token, string NewPassword);
public sealed record LoginSessionResult(Guid AccountId, Guid SessionId, string AccessToken, DateTimeOffset AccessExpiresAtUtc, string RefreshToken, DateTimeOffset RefreshExpiresAtUtc);
public sealed record CustomerProfileResult(Guid Id, string Email, string DisplayName, bool EmailVerified, bool MfaEnabled, DateTimeOffset CreatedAtUtc);
public sealed record UpdateCustomerProfileRequest(string DisplayName);
public sealed record MfaSetupResult(string Secret, string SetupToken, string OtpAuthUri);
public sealed record MfaConfirmRequest(string SetupToken, string Code);
public sealed record MfaConfirmResult(IReadOnlyList<string> RecoveryCodes);
public sealed record MfaDisableRequest(string Password, string Code);
public sealed record CustomerSessionResult(Guid Id, string UserAgentSummary, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RevokedAtUtc);
public sealed record TrustedDeviceResult(Guid Id, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RevokedAtUtc);
public sealed record AccountDataRequestDto(AccountDataRequestKind Kind);
