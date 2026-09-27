namespace PeerOnQ.Cloud.Domain.Entities;

public sealed class AdminUser
{
    private AdminUser() { }
    public AdminUser(Guid id, string email, string passwordHash, DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Admin user ID is required.", nameof(id));
        Id = id;
        Email = NormalizeEmail(email);
        PasswordHash = Device.NormalizeRequired(passwordHash, 2048, nameof(passwordHash));
        CreatedAtUtc = now;
        ConcurrencyVersion = 1;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public bool MfaEnabled { get; private set; }
    public byte[]? MfaSecretCiphertext { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public DateTimeOffset? LastLoginAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public bool IsDisabled { get; private set; }
    public long ConcurrencyVersion { get; private set; }

    public bool IsLocked(DateTimeOffset now) => IsDisabled || LockedUntilUtc > now;

    public void EnableMfa(byte[] protectedSecret)
    {
        if (protectedSecret.Length < 16) throw new ArgumentException("Protected MFA material is invalid.", nameof(protectedSecret));
        MfaSecretCiphertext = protectedSecret.ToArray();
        MfaEnabled = true;
        ConcurrencyVersion++;
    }

    public void DisableMfa() { MfaEnabled = false; MfaSecretCiphertext = null; ConcurrencyVersion++; }
    public void RecordSuccessfulLogin(DateTimeOffset now) { FailedLoginCount = 0; LockedUntilUtc = null; LastLoginAtUtc = now; ConcurrencyVersion++; }
    public void RecordFailedLogin(DateTimeOffset now, int threshold, TimeSpan lockDuration)
    {
        if (threshold < 1 || lockDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(threshold));
        FailedLoginCount++;
        if (FailedLoginCount >= threshold) LockedUntilUtc = now + lockDuration;
        ConcurrencyVersion++;
    }
    public void ChangePasswordHash(string passwordHash) { PasswordHash = Device.NormalizeRequired(passwordHash, 2048, nameof(passwordHash)); ConcurrencyVersion++; }
    public void Disable() { IsDisabled = true; ConcurrencyVersion++; }

    public static string NormalizeEmail(string value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length < 3 || normalized.Length > 320 || !normalized.Contains('@', StringComparison.Ordinal))
            throw new ArgumentException("A valid email address is required.", nameof(value));
        return normalized;
    }
}

public sealed class AdminRole
{
    private AdminRole() { }
    public AdminRole(Guid id, AdminRoleKind name, bool requiresMfa)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Role ID is required.", nameof(id)) : id;
        Name = name;
        RequiresMfa = requiresMfa;
    }
    public Guid Id { get; private set; }
    public AdminRoleKind Name { get; private set; }
    public bool RequiresMfa { get; private set; }
}

public sealed class AdminUserRole
{
    private AdminUserRole() { }
    public AdminUserRole(Guid adminUserId, Guid adminRoleId, DateTimeOffset grantedAtUtc, Guid? grantedByUserId)
    {
        AdminUserId = adminUserId;
        AdminRoleId = adminRoleId;
        GrantedAtUtc = grantedAtUtc;
        GrantedByUserId = grantedByUserId;
    }
    public Guid AdminUserId { get; private set; }
    public Guid AdminRoleId { get; private set; }
    public DateTimeOffset GrantedAtUtc { get; private set; }
    public Guid? GrantedByUserId { get; private set; }
}

public sealed class AdminRecoveryCode
{
    private AdminRecoveryCode() { }
    public AdminRecoveryCode(Guid id, Guid adminUserId, byte[] codeHash, DateTimeOffset createdAtUtc)
    {
        if (codeHash is not { Length: 32 }) throw new ArgumentException("Recovery-code hash must contain 32 bytes.", nameof(codeHash));
        Id = id;
        AdminUserId = adminUserId;
        CodeHash = codeHash.ToArray();
        CreatedAtUtc = createdAtUtc;
    }
    public Guid Id { get; private set; }
    public Guid AdminUserId { get; private set; }
    public byte[] CodeHash { get; private set; } = [];
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UsedAtUtc { get; private set; }
    public void MarkUsed(DateTimeOffset now)
    {
        if (UsedAtUtc is not null) throw new InvalidOperationException("Recovery code was already used.");
        UsedAtUtc = now;
    }
}

public sealed class AdminSession
{
    private AdminSession() { }
    public AdminSession(Guid id, Guid adminUserId, byte[] refreshTokenHash, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc, string userAgentSummary)
    {
        if (refreshTokenHash is not { Length: 32 }) throw new ArgumentException("Refresh-token hash must contain 32 bytes.", nameof(refreshTokenHash));
        if (expiresAtUtc <= createdAtUtc) throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        Id = id;
        AdminUserId = adminUserId;
        RefreshTokenHash = refreshTokenHash.ToArray();
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        UserAgentSummary = Device.NormalizeRequired(userAgentSummary, 256, nameof(userAgentSummary));
        ConcurrencyVersion = 1;
    }
    public Guid Id { get; private set; }
    public Guid AdminUserId { get; private set; }
    public byte[] RefreshTokenHash { get; private set; } = [];
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public Guid? ReplacedBySessionId { get; private set; }
    public string UserAgentSummary { get; private set; } = string.Empty;
    public long ConcurrencyVersion { get; private set; }
    public bool IsActive(DateTimeOffset now) => RevokedAtUtc is null && now < ExpiresAtUtc;
    public void Revoke(DateTimeOffset now, Guid? replacedBySessionId = null)
    {
        if (RevokedAtUtc is not null) return;
        RevokedAtUtc = now;
        ReplacedBySessionId = replacedBySessionId;
        ConcurrencyVersion++;
    }
}

public sealed class AuditEvent
{
    private AuditEvent() { }
    public AuditEvent(Guid id, string actorId, string actorType, string action, string targetType, string? targetId, AuditResult result, DateTimeOffset timestampUtc, string? ipRiskMetadata, string? userAgentSummary, string correlationId, string? reason)
    {
        Id = id;
        ActorId = Device.NormalizeRequired(actorId, 128, nameof(actorId));
        ActorType = Device.NormalizeRequired(actorType, 64, nameof(actorType));
        Action = Device.NormalizeRequired(action, 128, nameof(action));
        TargetType = Device.NormalizeRequired(targetType, 64, nameof(targetType));
        TargetId = Normalize(targetId, 128);
        Result = result;
        TimestampUtc = timestampUtc;
        IpRiskMetadata = Normalize(ipRiskMetadata, 512);
        UserAgentSummary = Normalize(userAgentSummary, 256);
        CorrelationId = Device.NormalizeRequired(correlationId, 128, nameof(correlationId));
        Reason = Normalize(reason, 512);
    }
    public Guid Id { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public string ActorType { get; private set; } = string.Empty;
    public string Action { get; private set; } = string.Empty;
    public string TargetType { get; private set; } = string.Empty;
    public string? TargetId { get; private set; }
    public AuditResult Result { get; private set; }
    public DateTimeOffset TimestampUtc { get; private set; }
    public string? IpRiskMetadata { get; private set; }
    public string? UserAgentSummary { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string? Reason { get; private set; }
    private static string? Normalize(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}
