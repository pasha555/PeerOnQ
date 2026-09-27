using System.Net.Mail;

namespace PeerOnQ.Cloud.Domain.Entities;

public sealed class CustomerAccount
{
    private CustomerAccount() { }

    public CustomerAccount(Guid id, string email, string displayName, string passwordHash, DateTimeOffset now)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Account ID is required.", nameof(id)) : id;
        Email = NormalizeEmail(email);
        DisplayName = Device.NormalizeRequired(displayName, 128, nameof(displayName));
        PasswordHash = Device.NormalizeRequired(passwordHash, 2048, nameof(passwordHash));
        Status = CustomerAccountStatus.Active;
        CreatedAtUtc = now;
        ConcurrencyVersion = 1;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public CustomerAccountStatus Status { get; private set; }
    public bool EmailVerified { get; private set; }
    public bool MfaEnabled { get; private set; }
    public byte[]? MfaSecretCiphertext { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public DateTimeOffset? LastLoginAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DeletionRequestedAtUtc { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public long ConcurrencyVersion { get; private set; }

    public bool IsLoginAllowed(DateTimeOffset now, bool emailVerificationRequired) =>
        Status == CustomerAccountStatus.Active &&
        (LockedUntilUtc is null || LockedUntilUtc <= now) &&
        (!emailVerificationRequired || EmailVerified);

    public void VerifyEmail()
    {
        EnsureMutable();
        if (EmailVerified) return;
        EmailVerified = true;
        ConcurrencyVersion++;
    }

    public void UpdateProfile(string displayName)
    {
        EnsureMutable();
        DisplayName = Device.NormalizeRequired(displayName, 128, nameof(displayName));
        ConcurrencyVersion++;
    }

    public void ChangePasswordHash(string passwordHash)
    {
        EnsureMutable();
        PasswordHash = Device.NormalizeRequired(passwordHash, 2048, nameof(passwordHash));
        FailedLoginCount = 0;
        LockedUntilUtc = null;
        ConcurrencyVersion++;
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        EnsureMutable();
        FailedLoginCount = 0;
        LockedUntilUtc = null;
        LastLoginAtUtc = now;
        ConcurrencyVersion++;
    }

    public void RecordFailedLogin(DateTimeOffset now, int threshold, TimeSpan lockDuration)
    {
        EnsureMutable();
        if (threshold < 1 || lockDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(threshold));
        FailedLoginCount++;
        if (FailedLoginCount >= threshold) LockedUntilUtc = now + lockDuration;
        ConcurrencyVersion++;
    }

    public void EnableMfa(byte[] protectedSecret)
    {
        EnsureMutable();
        if (protectedSecret is not { Length: >= 16 }) throw new ArgumentException("Protected MFA material is invalid.", nameof(protectedSecret));
        MfaSecretCiphertext = protectedSecret.ToArray();
        MfaEnabled = true;
        ConcurrencyVersion++;
    }

    public void DisableMfa()
    {
        EnsureMutable();
        MfaSecretCiphertext = null;
        MfaEnabled = false;
        ConcurrencyVersion++;
    }

    public void Disable()
    {
        if (Status is CustomerAccountStatus.Deleted) return;
        Status = CustomerAccountStatus.Disabled;
        ConcurrencyVersion++;
    }

    public void RequestDeletion(DateTimeOffset now)
    {
        EnsureMutable();
        Status = CustomerAccountStatus.DeletionPending;
        DeletionRequestedAtUtc = now;
        ConcurrencyVersion++;
    }

    public void CompleteDeletion(DateTimeOffset now)
    {
        Status = CustomerAccountStatus.Deleted;
        DeletedAtUtc = now;
        DisplayName = "Deleted account";
        Email = $"deleted-{Id:N}@invalid.peeronq";
        PasswordHash = "deleted";
        MfaEnabled = false;
        MfaSecretCiphertext = null;
        ConcurrencyVersion++;
    }

    public static string NormalizeEmail(string value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length is < 3 or > 320) throw new ArgumentException("A valid email address is required.", nameof(value));
        try { _ = new MailAddress(normalized); }
        catch (FormatException) { throw new ArgumentException("A valid email address is required.", nameof(value)); }
        return normalized;
    }

    private void EnsureMutable()
    {
        if (Status is CustomerAccountStatus.Deleted) throw new InvalidOperationException("The account is deleted.");
    }
}

public sealed class CustomerSession
{
    private CustomerSession() { }
    public CustomerSession(Guid id, Guid accountId, Guid familyId, byte[] refreshTokenHash, DateTimeOffset now, DateTimeOffset expiresAtUtc, string userAgentSummary)
    {
        if (id == Guid.Empty || accountId == Guid.Empty || familyId == Guid.Empty) throw new ArgumentException("Session identifiers are required.");
        if (refreshTokenHash is not { Length: 32 }) throw new ArgumentException("Refresh-token hash must contain 32 bytes.", nameof(refreshTokenHash));
        if (expiresAtUtc <= now) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        Id = id;
        AccountId = accountId;
        FamilyId = familyId;
        RefreshTokenHash = refreshTokenHash.ToArray();
        CreatedAtUtc = now;
        ExpiresAtUtc = expiresAtUtc;
        UserAgentSummary = Device.NormalizeRequired(userAgentSummary, 256, nameof(userAgentSummary));
        ConcurrencyVersion = 1;
    }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid FamilyId { get; private set; }
    public byte[] RefreshTokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public Guid? ReplacedBySessionId { get; private set; }
    public string UserAgentSummary { get; private set; } = string.Empty;
    public long ConcurrencyVersion { get; private set; }
    public bool IsActive(DateTimeOffset now) => RevokedAtUtc is null && now < ExpiresAtUtc;
    public void Revoke(DateTimeOffset now, Guid? replacement = null)
    {
        if (RevokedAtUtc is not null) return;
        RevokedAtUtc = now;
        ReplacedBySessionId = replacement;
        ConcurrencyVersion++;
    }
}

public sealed class CustomerAccountToken
{
    private CustomerAccountToken() { }
    public CustomerAccountToken(Guid id, Guid accountId, CustomerTokenPurpose purpose, byte[] tokenHash, DateTimeOffset now, DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty || accountId == Guid.Empty) throw new ArgumentException("Token identifiers are required.");
        if (tokenHash is not { Length: 32 }) throw new ArgumentException("Token hash must contain 32 bytes.", nameof(tokenHash));
        if (expiresAtUtc <= now) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        Id = id; AccountId = accountId; Purpose = purpose; TokenHash = tokenHash.ToArray(); CreatedAtUtc = now; ExpiresAtUtc = expiresAtUtc;
    }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public CustomerTokenPurpose Purpose { get; private set; }
    public byte[] TokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? UsedAtUtc { get; private set; }
    public bool CanConsume(byte[] presentedHash, DateTimeOffset now) => UsedAtUtc is null && now < ExpiresAtUtc && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(TokenHash, presentedHash);
    public void Consume(byte[] presentedHash, DateTimeOffset now)
    {
        if (!CanConsume(presentedHash, now)) throw new InvalidOperationException("The account token is invalid, expired, or already used.");
        UsedAtUtc = now;
    }
}

public sealed class CustomerRecoveryCode
{
    private CustomerRecoveryCode() { }
    public CustomerRecoveryCode(Guid id, Guid accountId, byte[] codeHash, DateTimeOffset now)
    {
        if (codeHash is not { Length: 32 }) throw new ArgumentException("Recovery-code hash must contain 32 bytes.", nameof(codeHash));
        Id = id; AccountId = accountId; CodeHash = codeHash.ToArray(); CreatedAtUtc = now;
    }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public byte[] CodeHash { get; private set; } = [];
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UsedAtUtc { get; private set; }
    public void Consume(byte[] presentedHash, DateTimeOffset now)
    {
        if (UsedAtUtc is not null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(CodeHash, presentedHash))
            throw new InvalidOperationException("The recovery code is invalid or already used.");
        UsedAtUtc = now;
    }
}

public sealed class Organization
{
    private Organization() { }
    public Organization(Guid id, string name, Guid ownerAccountId, DateTimeOffset now)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Organization ID is required.", nameof(id)) : id;
        OwnerAccountId = ownerAccountId == Guid.Empty ? throw new ArgumentException("Owner account ID is required.", nameof(ownerAccountId)) : ownerAccountId;
        Name = Device.NormalizeRequired(name, 128, nameof(name));
        CreatedAtUtc = now;
        ConcurrencyVersion = 1;
    }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Guid OwnerAccountId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DeletionRequestedAtUtc { get; private set; }
    public long ConcurrencyVersion { get; private set; }
    public void TransferOwnership(Guid currentOwnerId, Guid newOwnerId)
    {
        if (currentOwnerId != OwnerAccountId) throw new UnauthorizedAccessException("Only the current owner can transfer ownership.");
        if (newOwnerId == Guid.Empty || newOwnerId == currentOwnerId) throw new ArgumentException("A different owner is required.", nameof(newOwnerId));
        OwnerAccountId = newOwnerId;
        ConcurrencyVersion++;
    }
    public void RequestDeletion(Guid actorId, int activeMemberCount, DateTimeOffset now)
    {
        if (actorId != OwnerAccountId) throw new UnauthorizedAccessException("Only the owner can request organization deletion.");
        if (activeMemberCount > 1) throw new InvalidOperationException("Transfer or remove all other active members before deleting the organization.");
        DeletionRequestedAtUtc = now;
        ConcurrencyVersion++;
    }
}

public sealed class OrganizationMembership
{
    private OrganizationMembership() { }
    public OrganizationMembership(Guid organizationId, Guid accountId, CustomerRoleKind role, DateTimeOffset now)
    {
        if (organizationId == Guid.Empty || accountId == Guid.Empty) throw new ArgumentException("Membership identifiers are required.");
        OrganizationId = organizationId; AccountId = accountId; Role = role; JoinedAtUtc = now; ConcurrencyVersion = 1;
    }
    public Guid OrganizationId { get; private set; }
    public Guid AccountId { get; private set; }
    public CustomerRoleKind Role { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public long ConcurrencyVersion { get; private set; }
    public bool IsActive => RevokedAtUtc is null;
    public void ChangeRole(CustomerRoleKind role)
    {
        if (!IsActive) throw new InvalidOperationException("Membership is revoked.");
        Role = role; ConcurrencyVersion++;
    }
    public void Revoke(DateTimeOffset now) { if (RevokedAtUtc is null) { RevokedAtUtc = now; ConcurrencyVersion++; } }
}

public sealed class Team
{
    private Team() { }
    public Team(Guid id, Guid organizationId, string name, DateTimeOffset now)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Team ID is required.", nameof(id)) : id;
        OrganizationId = organizationId == Guid.Empty ? throw new ArgumentException("Organization ID is required.", nameof(organizationId)) : organizationId;
        Name = Device.NormalizeRequired(name, 128, nameof(name)); CreatedAtUtc = now; ConcurrencyVersion = 1;
    }
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public long ConcurrencyVersion { get; private set; }
}

public sealed class TeamMembership
{
    private TeamMembership() { }
    public TeamMembership(Guid teamId, Guid accountId, DateTimeOffset now) { TeamId = teamId; AccountId = accountId; JoinedAtUtc = now; }
    public Guid TeamId { get; private set; }
    public Guid AccountId { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; private set; }
}

public sealed class OrganizationInvitation
{
    private OrganizationInvitation() { }
    public OrganizationInvitation(Guid id, Guid organizationId, string email, CustomerRoleKind role, byte[] tokenHash, Guid invitedByAccountId, DateTimeOffset now, DateTimeOffset expiresAtUtc)
    {
        if (id == Guid.Empty || organizationId == Guid.Empty || invitedByAccountId == Guid.Empty) throw new ArgumentException("Invitation identifiers are required.");
        if (role == CustomerRoleKind.Owner) throw new ArgumentException("Ownership must be transferred after membership is established.", nameof(role));
        if (tokenHash is not { Length: 32 }) throw new ArgumentException("Invitation-token hash must contain 32 bytes.", nameof(tokenHash));
        if (expiresAtUtc <= now) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        Id = id; OrganizationId = organizationId; Email = CustomerAccount.NormalizeEmail(email); Role = role;
        TokenHash = tokenHash.ToArray(); InvitedByAccountId = invitedByAccountId; CreatedAtUtc = now; ExpiresAtUtc = expiresAtUtc; ConcurrencyVersion = 1;
    }
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public CustomerRoleKind Role { get; private set; }
    public byte[] TokenHash { get; private set; } = [];
    public Guid InvitedByAccountId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? AcceptedAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public long ConcurrencyVersion { get; private set; }
    public bool CanAccept(string accountEmail, byte[] presentedHash, DateTimeOffset now) =>
        AcceptedAtUtc is null && RevokedAtUtc is null && now < ExpiresAtUtc &&
        Email == CustomerAccount.NormalizeEmail(accountEmail) &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(TokenHash, presentedHash);
    public void Accept(string accountEmail, byte[] presentedHash, DateTimeOffset now)
    {
        if (!CanAccept(accountEmail, presentedHash, now)) throw new InvalidOperationException("The invitation is invalid, expired, revoked, already used, or belongs to another account.");
        AcceptedAtUtc = now; ConcurrencyVersion++;
    }
    public void Revoke(DateTimeOffset now) { if (AcceptedAtUtc is not null) throw new InvalidOperationException("An accepted invitation cannot be revoked."); RevokedAtUtc ??= now; ConcurrencyVersion++; }
}

public sealed class OrganizationPolicy
{
    private OrganizationPolicy() { }
    public OrganizationPolicy(Guid organizationId)
    {
        OrganizationId = organizationId == Guid.Empty ? throw new ArgumentException("Organization ID is required.", nameof(organizationId)) : organizationId;
        ViewOnlyAllowed = true; FullControlAllowed = true; FileTransferAllowed = true; ClipboardAllowed = true;
        TrustedDeviceLifetimeDays = 30; AuditRetentionDays = 90; ConcurrencyVersion = 1;
    }
    public Guid OrganizationId { get; private set; }
    public bool ViewOnlyAllowed { get; private set; }
    public bool FullControlAllowed { get; private set; }
    public bool FileTransferAllowed { get; private set; }
    public bool ClipboardAllowed { get; private set; }
    public bool UnattendedAccessAllowed { get; private set; }
    public bool MfaRequired { get; private set; }
    public bool HybridSecurityRequired { get; private set; }
    public int TrustedDeviceLifetimeDays { get; private set; }
    public int AuditRetentionDays { get; private set; }
    public string ApprovedRelayRegionsCsv { get; private set; } = string.Empty;
    public string MinimumClientVersion { get; private set; } = string.Empty;
    public long ConcurrencyVersion { get; private set; }
    public bool Allows(PermissionMode mode) => mode switch
    {
        PermissionMode.ViewOnly => ViewOnlyAllowed,
        PermissionMode.FullControl => FullControlAllowed,
        PermissionMode.FileTransfer => FileTransferAllowed,
        _ => false,
    };
    public void Update(bool viewOnly, bool fullControl, bool fileTransfer, bool clipboard, bool unattended, bool mfaRequired,
        int trustedDeviceLifetimeDays, int auditRetentionDays, string? approvedRelayRegionsCsv, string? minimumClientVersion, bool hybridSecurityRequired)
    {
        if (trustedDeviceLifetimeDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(trustedDeviceLifetimeDays));
        if (auditRetentionDays is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(auditRetentionDays));
        ViewOnlyAllowed = viewOnly; FullControlAllowed = fullControl; FileTransferAllowed = fileTransfer; ClipboardAllowed = clipboard;
        UnattendedAccessAllowed = unattended; MfaRequired = mfaRequired; TrustedDeviceLifetimeDays = trustedDeviceLifetimeDays;
        AuditRetentionDays = auditRetentionDays; ApprovedRelayRegionsCsv = NormalizeRelayRegions(approvedRelayRegionsCsv);
        MinimumClientVersion = NormalizeOptional(minimumClientVersion, 64); HybridSecurityRequired = hybridSecurityRequired; ConcurrencyVersion++;
    }
    private static string NormalizeOptional(string? value, int max) { var normalized = value?.Trim() ?? string.Empty; return normalized.Length <= max ? normalized : throw new ArgumentOutOfRangeException(nameof(value)); }
    private static string NormalizeRelayRegions(string? value)
    {
        var regions = (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (regions.Any(region => region.Length > 64 || region.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')))
            throw new ArgumentException("Relay region names are invalid.", nameof(value));
        return NormalizeOptional(string.Join(',', regions.Distinct(StringComparer.OrdinalIgnoreCase)), 512);
    }
}

public sealed class CustomerTrustedDevice
{
    private CustomerTrustedDevice() { }
    public CustomerTrustedDevice(Guid id, Guid accountId, byte[] deviceKeyHash, string name, DateTimeOffset now, DateTimeOffset expiresAtUtc)
    {
        if (deviceKeyHash is not { Length: 32 }) throw new ArgumentException("Trusted-device key hash must contain 32 bytes.", nameof(deviceKeyHash));
        if (expiresAtUtc <= now) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        Id = id; AccountId = accountId; DeviceKeyHash = deviceKeyHash.ToArray(); Name = Device.NormalizeRequired(name, 128, nameof(name)); CreatedAtUtc = now; ExpiresAtUtc = expiresAtUtc;
    }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public byte[] DeviceKeyHash { get; private set; } = [];
    public string Name { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public void Revoke(DateTimeOffset now) => RevokedAtUtc ??= now;
}

public sealed class CustomerSecurityEvent
{
    private CustomerSecurityEvent() { }
    public CustomerSecurityEvent(Guid id, Guid accountId, Guid? organizationId, string action, AuditResult result, DateTimeOffset now, string correlationId, string? safeMetadata)
    {
        Id = id; AccountId = accountId; OrganizationId = organizationId; Action = Device.NormalizeRequired(action, 128, nameof(action));
        Result = result; TimestampUtc = now; CorrelationId = Device.NormalizeRequired(correlationId, 128, nameof(correlationId));
        SafeMetadata = string.IsNullOrWhiteSpace(safeMetadata) ? null : safeMetadata.Trim()[..Math.Min(safeMetadata.Trim().Length, 512)];
    }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public AuditResult Result { get; private set; }
    public DateTimeOffset TimestampUtc { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string? SafeMetadata { get; private set; }
}

public sealed class AccountDataRequest
{
    private AccountDataRequest() { }
    public AccountDataRequest(Guid id, Guid accountId, AccountDataRequestKind kind, DateTimeOffset now)
    { Id = id; AccountId = accountId; Kind = kind; Status = AccountDataRequestStatus.Pending; RequestedAtUtc = now; }
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public AccountDataRequestKind Kind { get; private set; }
    public AccountDataRequestStatus Status { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public void Complete(DateTimeOffset now) { if (Status != AccountDataRequestStatus.Pending) throw new InvalidOperationException("The data request is already finalized."); Status = AccountDataRequestStatus.Completed; CompletedAtUtc = now; }
}
