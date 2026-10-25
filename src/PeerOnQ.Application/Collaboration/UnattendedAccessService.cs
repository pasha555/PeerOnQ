using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Collaboration;

public sealed record UnattendedSetupRequest
{
    public required bool LocalUserConfirmed { get; init; }
    public required bool ExplanationAcknowledged { get; init; }
    public string? StrongPassword { get; init; }
    public bool DeviceAuthenticationEnabled { get; init; }
    public SessionPermission AllowedPermissions { get; init; } = SessionPermission.ViewScreen;
}

public sealed record UnattendedSetupResult(IReadOnlyList<string> RecoveryCodes);

public sealed record UnattendedAccessStatus(
    bool Enabled,
    bool PasswordConfigured,
    bool TrustedDeviceAuthenticationEnabled,
    SessionPermission AllowedPermissions,
    DateTimeOffset? LockedUntil);

public enum UnattendedAuthenticationResult
{
    Succeeded = 0,
    Disabled = 1,
    InvalidCredential = 2,
    LockedOut = 3,
    PermissionDenied = 4,
    DeviceNotTrusted = 5,
}

public sealed class UnattendedPermissionMismatchException(
    SessionPermission requestedPermissions,
    SessionPermission allowedPermissions) : InvalidOperationException(
        "The selected unattended connection mode is not allowed by the remote computer.")
{
    public SessionPermission RequestedPermissions { get; } = requestedPermissions;
    public SessionPermission AllowedPermissions { get; } = allowedPermissions;
}

/// <summary>
/// Explicit unattended-access policy. It does not install services, bypass Windows consent/UAC,
/// suppress indicators or alter account privileges. Signaling device proof remains mandatory.
/// </summary>
public sealed class UnattendedAccessService(
    ICollaborationProfileStore store,
    TrustedDeviceService trustedDevices,
    ISecurityAuditLog? audit = null,
    TimeProvider? timeProvider = null)
{
    private const int MaximumFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, IssuedPasswordChallenge> _passwordChallenges = new();

    public event EventHandler<bool>? EnabledChanged;
    public event EventHandler<string>? SecurityNotification;

    public async Task<UnattendedChallenge> IssuePasswordChallengeAsync(
        UnattendedChallengeRequestNotification request,
        CancellationToken cancellationToken = default)
    {
        var settings = (await store.LoadAsync(cancellationToken)).UnattendedAccess;
        var now = _time.GetUtcNow();
        if (!settings.Enabled || (settings.Password is null && settings.RecoveryCodeHashes.Count == 0)
            || (settings.LockedUntil is { } locked && locked > now)
            || request.ExpiresAt <= now)
        {
            return new UnattendedChallenge(
                request.RequestId, false, null, null, null, 0, null, "credential_unavailable");
        }

        RemoveExpiredChallenges(now);
        if (_passwordChallenges.Count >= 128)
            return new UnattendedChallenge(request.RequestId, false, null, null, null, 0, null, "rate_limited");

        var challengeId = Guid.NewGuid();
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expiresAt = new[] { request.ExpiresAt, now.AddSeconds(30) }.Min();
        _passwordChallenges[challengeId] = new IssuedPasswordChallenge(
            request.RequesterId,
            request.RequesterFingerprint.ToLowerInvariant(),
            challenge,
            expiresAt);
        return new UnattendedChallenge(
            request.RequestId,
            true,
            challengeId,
            challenge,
            settings.Password?.Salt.ToArray(),
            settings.Password?.Iterations ?? 0,
            expiresAt,
            "ok",
            settings.AllowedPermissions);
    }

    public async Task<UnattendedAuthenticationResult> VerifyPasswordProofAsync(
        PeerOnQId requester,
        string requesterFingerprint,
        SessionPermission requestedPermissions,
        Guid challengeId,
        string suppliedProof,
        CancellationToken cancellationToken = default)
    {
        if (!_passwordChallenges.TryRemove(challengeId, out var issued)
            || issued.ExpiresAt <= _time.GetUtcNow()
            || issued.RequesterId != requester
            || !string.Equals(issued.RequesterFingerprint, requesterFingerprint, StringComparison.OrdinalIgnoreCase))
            return UnattendedAuthenticationResult.InvalidCredential;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            var settings = profile.UnattendedAccess;
            var now = _time.GetUtcNow();
            if (!settings.Enabled || (settings.Password is null && settings.RecoveryCodeHashes.Count == 0))
                return UnattendedAuthenticationResult.Disabled;
            if ((requestedPermissions & ~settings.AllowedPermissions) != 0)
                return UnattendedAuthenticationResult.PermissionDenied;
            if (settings.LockedUntil is { } locked && locked > now)
                return UnattendedAuthenticationResult.LockedOut;

            byte[] supplied;
            try { supplied = Convert.FromBase64String(suppliedProof); }
            catch (FormatException) { supplied = []; }
            var valid = false;
            var recoveryIndex = -1;
            if (settings.Password is { } password)
            {
                var expected = ComputeProof(
                    password.Hash,
                    challengeId,
                    issued.Challenge,
                    requester,
                    requesterFingerprint,
                    requestedPermissions);
                valid = supplied.Length == expected.Length
                        && CryptographicOperations.FixedTimeEquals(supplied, expected);
                CryptographicOperations.ZeroMemory(expected);
            }

            for (var index = 0; index < settings.RecoveryCodeHashes.Count; index++)
            {
                var expected = ComputeProof(
                    settings.RecoveryCodeHashes[index],
                    challengeId,
                    issued.Challenge,
                    requester,
                    requesterFingerprint,
                    requestedPermissions);
                var matches = supplied.Length == expected.Length
                              && CryptographicOperations.FixedTimeEquals(supplied, expected);
                if (matches && recoveryIndex < 0) recoveryIndex = index;
                CryptographicOperations.ZeroMemory(expected);
            }
            valid |= recoveryIndex >= 0;
            if (supplied.Length > 0) CryptographicOperations.ZeroMemory(supplied);

            if (!valid)
            {
                var failures = settings.FailedAttempts + 1;
                DateTimeOffset? lockout = failures >= MaximumFailures ? now + LockoutDuration : null;
                await store.SaveAsync(profile with
                {
                    UnattendedAccess = settings with { FailedAttempts = failures, LockedUntil = lockout },
                }, cancellationToken);
                await AuditAsync(lockout is null
                        ? SecurityAuditEventType.UnattendedAuthenticationFailed
                        : SecurityAuditEventType.UnattendedLockedOut,
                    requester,
                    lockout is null ? "invalid_password_proof" : "locked_out",
                    cancellationToken);
                return lockout is null
                    ? UnattendedAuthenticationResult.InvalidCredential
                    : UnattendedAuthenticationResult.LockedOut;
            }

            var remainingRecoveryCodes = settings.RecoveryCodeHashes.ToList();
            if (recoveryIndex >= 0) remainingRecoveryCodes.RemoveAt(recoveryIndex);
            await store.SaveAsync(profile with
            {
                UnattendedAccess = settings with
                {
                    FailedAttempts = 0,
                    LockedUntil = null,
                    RecoveryCodeHashes = remainingRecoveryCodes,
                },
            }, cancellationToken);
            if (recoveryIndex >= 0)
                CryptographicOperations.ZeroMemory(settings.RecoveryCodeHashes[recoveryIndex]);
            SecurityNotification?.Invoke(this, $"Unattended session authenticated for {requester.MaskedDisplay}.");
            await AuditAsync(
                SecurityAuditEventType.UnattendedAuthenticationSucceeded,
                requester,
                recoveryIndex >= 0 ? "recovery_proof" : "password_proof",
                cancellationToken);
            return UnattendedAuthenticationResult.Succeeded;
        }
        finally { _gate.Release(); }
    }

    public static string CreatePasswordProof(
        string credential,
        UnattendedChallenge challenge,
        PeerOnQId requester,
        string requesterFingerprint,
        SessionPermission requestedPermissions)
    {
        if (!challenge.Available || challenge.ChallengeId is null || challenge.Challenge is null
            || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The unattended password challenge is unavailable or invalid.");

        byte[] key;
        if (LooksLikeRecoveryCode(credential))
        {
            key = HashRecoveryCode(credential);
        }
        else
        {
            if (challenge.Salt is null || challenge.Iterations < 600_000)
                throw new InvalidOperationException("Password authentication is unavailable for this challenge.");
            var passwordBytes = Encoding.UTF8.GetBytes(credential);
            try
            {
                key = Rfc2898DeriveBytes.Pbkdf2(
                    passwordBytes,
                    challenge.Salt,
                    challenge.Iterations,
                    HashAlgorithmName.SHA512,
                    64);
            }
            finally { CryptographicOperations.ZeroMemory(passwordBytes); }
        }

        try
        {
            var proof = ComputeProof(
                key,
                challenge.ChallengeId.Value,
                challenge.Challenge,
                requester,
                requesterFingerprint,
                requestedPermissions);
            try
            {
                return Convert.ToBase64String(proof);
            }
            finally { CryptographicOperations.ZeroMemory(proof); }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        (await store.LoadAsync(cancellationToken)).UnattendedAccess.Enabled;

    public async Task<UnattendedAccessStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = (await store.LoadAsync(cancellationToken)).UnattendedAccess;
        return new UnattendedAccessStatus(
            settings.Enabled,
            settings.Password is not null,
            settings.DeviceAuthenticationEnabled,
            settings.AllowedPermissions,
            settings.LockedUntil);
    }

    public async Task<UnattendedSetupResult> EnableAsync(
        UnattendedSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.LocalUserConfirmed || !request.ExplanationAcknowledged)
            throw new InvalidOperationException("Unattended access requires an explicit local setup action and acknowledged explanation.");
        if (!request.DeviceAuthenticationEnabled && string.IsNullOrEmpty(request.StrongPassword))
            throw new InvalidOperationException("Configure a strong password or trusted-device authentication.");
        if (!string.IsNullOrEmpty(request.StrongPassword) && !IsStrongPassword(request.StrongPassword))
            throw new ArgumentException("The unattended password must be at least 14 characters with upper, lower, number and symbol.");
        if (!Phase1SessionScope.HasInteractiveMediaPermissions(request.AllowedPermissions))
            throw new ArgumentOutOfRangeException(nameof(request.AllowedPermissions));

        var codes = GenerateRecoveryCodes();
        var codeHashes = codes.Select(HashRecoveryCode).ToArray();
        var password = string.IsNullOrEmpty(request.StrongPassword)
            ? null
            : ProtectedCollaborationProfileStore.DerivePassword(request.StrongPassword);
        var now = _time.GetUtcNow();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            await store.SaveAsync(profile with
            {
                UnattendedAccess = new UnattendedAccessSettings
                {
                    Enabled = true,
                    DeviceAuthenticationEnabled = request.DeviceAuthenticationEnabled,
                    AllowedPermissions = request.AllowedPermissions,
                    Password = password,
                    RecoveryCodeHashes = codeHashes,
                    EnabledAt = now,
                    CredentialsRotatedAt = now,
                },
            }, cancellationToken);
            ZeroSettingsSecrets(profile.UnattendedAccess);
            _passwordChallenges.Clear();
        }
        finally { _gate.Release(); }

        EnabledChanged?.Invoke(this, true);
        SecurityNotification?.Invoke(this, "Unattended access is enabled. PeerOnQ will show an indicator for every unattended session.");
        await AuditAsync(SecurityAuditEventType.UnattendedEnabled, null, "enabled", cancellationToken);
        return new UnattendedSetupResult(codes);
    }

    public async Task DisableAsync(bool localUserConfirmed, CancellationToken cancellationToken = default)
    {
        if (!localUserConfirmed) throw new InvalidOperationException("Disabling unattended access requires a local user action.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            await store.SaveAsync(profile with { UnattendedAccess = new UnattendedAccessSettings() }, cancellationToken);
            ZeroSettingsSecrets(profile.UnattendedAccess);
            _passwordChallenges.Clear();
        }
        finally { _gate.Release(); }
        EnabledChanged?.Invoke(this, false);
        SecurityNotification?.Invoke(this, "Unattended access was disabled and its credentials were revoked.");
        await AuditAsync(SecurityAuditEventType.UnattendedDisabled, null, "disabled", cancellationToken);
    }

    public async Task<UnattendedAuthenticationResult> AuthenticatePasswordAsync(
        PeerOnQId requester,
        string passwordOrRecoveryCode,
        SessionPermission requestedPermissions,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            var settings = profile.UnattendedAccess;
            var now = _time.GetUtcNow();
            if (!settings.Enabled) return UnattendedAuthenticationResult.Disabled;
            if ((requestedPermissions & ~settings.AllowedPermissions) != 0)
                return UnattendedAuthenticationResult.PermissionDenied;
            if (settings.LockedUntil is { } lockedUntil && lockedUntil > now)
            {
                await AuditAsync(SecurityAuditEventType.UnattendedLockedOut, requester, "locked_out", cancellationToken);
                return UnattendedAuthenticationResult.LockedOut;
            }

            var passwordValid = settings.Password is not null
                                && ProtectedCollaborationProfileStore.VerifyPassword(passwordOrRecoveryCode, settings.Password);
            var suppliedRecoveryHash = HashRecoveryCode(passwordOrRecoveryCode);
            var recoveryIndex = FindRecoveryCode(settings.RecoveryCodeHashes, suppliedRecoveryHash);
            CryptographicOperations.ZeroMemory(suppliedRecoveryHash);
            if (!passwordValid && recoveryIndex < 0)
            {
                var failures = settings.FailedAttempts + 1;
                DateTimeOffset? lockout = failures >= MaximumFailures ? now + LockoutDuration : null;
                await store.SaveAsync(profile with
                {
                    UnattendedAccess = settings with { FailedAttempts = failures, LockedUntil = lockout },
                }, cancellationToken);
                SecurityNotification?.Invoke(this, lockout is null
                    ? "A failed unattended authentication attempt occurred."
                    : "Unattended authentication is temporarily locked after repeated failures.");
                await AuditAsync(lockout is null
                        ? SecurityAuditEventType.UnattendedAuthenticationFailed
                        : SecurityAuditEventType.UnattendedLockedOut,
                    requester, lockout is null ? "invalid_credential" : "locked_out", cancellationToken);
                return lockout is null
                    ? UnattendedAuthenticationResult.InvalidCredential
                    : UnattendedAuthenticationResult.LockedOut;
            }

            var remainingCodes = settings.RecoveryCodeHashes.ToList();
            if (recoveryIndex >= 0) remainingCodes.RemoveAt(recoveryIndex); // Recovery codes are single-use.
            await store.SaveAsync(profile with
            {
                UnattendedAccess = settings with
                {
                    FailedAttempts = 0,
                    LockedUntil = null,
                    RecoveryCodeHashes = remainingCodes,
                },
            }, cancellationToken);
            SecurityNotification?.Invoke(this, $"Unattended session authenticated for {requester.MaskedDisplay}.");
            await AuditAsync(SecurityAuditEventType.UnattendedAuthenticationSucceeded, requester, "password_or_recovery", cancellationToken);
            return UnattendedAuthenticationResult.Succeeded;
        }
        finally { _gate.Release(); }
    }

    public async Task<UnattendedAuthenticationResult> AuthenticateTrustedDeviceAsync(
        PeerOnQId requester,
        string publicKeyFingerprint,
        SessionPermission requestedPermissions,
        CancellationToken cancellationToken = default)
    {
        var settings = (await store.LoadAsync(cancellationToken)).UnattendedAccess;
        if (!settings.Enabled) return UnattendedAuthenticationResult.Disabled;
        if (!settings.DeviceAuthenticationEnabled) return UnattendedAuthenticationResult.DeviceNotTrusted;
        if ((requestedPermissions & ~settings.AllowedPermissions) != 0)
            return UnattendedAuthenticationResult.PermissionDenied;
        var trusted = await trustedDevices.ValidateAsync(requester, publicKeyFingerprint, requestedPermissions, cancellationToken);
        if (trusted is null)
        {
            SecurityNotification?.Invoke(this, "An untrusted or changed device attempted unattended access.");
            await AuditAsync(SecurityAuditEventType.UnattendedAuthenticationFailed, requester, "device_not_trusted", cancellationToken);
            return UnattendedAuthenticationResult.DeviceNotTrusted;
        }

        SecurityNotification?.Invoke(this, $"Unattended session authenticated for {requester.MaskedDisplay}.");
        await AuditAsync(SecurityAuditEventType.UnattendedAuthenticationSucceeded, requester, "trusted_device", cancellationToken);
        return UnattendedAuthenticationResult.Succeeded;
    }

    public Task<UnattendedSetupResult> RotateCredentialsAsync(
        UnattendedSetupRequest request,
        CancellationToken cancellationToken = default) => EnableAsync(request, cancellationToken);

    public static bool IsStrongPassword(string password) =>
        password.Length >= 14
        && password.Any(char.IsUpper)
        && password.Any(char.IsLower)
        && password.Any(char.IsDigit)
        && password.Any(character => !char.IsLetterOrDigit(character));

    private static IReadOnlyList<string> GenerateRecoveryCodes() => Enumerable.Range(0, 10)
        .Select(_ =>
        {
            var text = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            return $"{text[..4]}-{text[4..8]}-{text[8..12]}-{text[12..16]}";
        })
        .ToArray();

    private static byte[] HashRecoveryCode(string code)
    {
        var bytes = Encoding.UTF8.GetBytes(code.Replace("-", string.Empty).Trim().ToUpperInvariant());
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool LooksLikeRecoveryCode(string value)
    {
        var normalized = value.Replace("-", string.Empty).Trim();
        return normalized.Length == 16 && normalized.All(Uri.IsHexDigit);
    }

    private static void ZeroSettingsSecrets(UnattendedAccessSettings settings)
    {
        if (settings.Password is { } password)
        {
            CryptographicOperations.ZeroMemory(password.Salt);
            CryptographicOperations.ZeroMemory(password.Hash);
        }
        foreach (var hash in settings.RecoveryCodeHashes)
            CryptographicOperations.ZeroMemory(hash);
    }

    private static byte[] ComputeProof(
        byte[] key,
        Guid challengeId,
        string challenge,
        PeerOnQId requester,
        string requesterFingerprint,
        SessionPermission permissions)
    {
        var canonical = Encoding.UTF8.GetBytes(
            $"{challengeId:N}|{challenge}|{requester.Value}|{requesterFingerprint.ToLowerInvariant()}|{(int)permissions}");
        try { return HMACSHA512.HashData(key, canonical); }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }

    private void RemoveExpiredChallenges(DateTimeOffset now)
    {
        foreach (var pair in _passwordChallenges)
            if (pair.Value.ExpiresAt <= now) _passwordChallenges.TryRemove(pair.Key, out _);
    }

    private static int FindRecoveryCode(IReadOnlyList<byte[]> hashes, byte[] candidate)
    {
        for (var index = 0; index < hashes.Count; index++)
            if (hashes[index].Length == candidate.Length && CryptographicOperations.FixedTimeEquals(hashes[index], candidate))
                return index;
        return -1;
    }

    private Task AuditAsync(
        SecurityAuditEventType type,
        PeerOnQId? requester,
        string outcome,
        CancellationToken cancellationToken) =>
        audit?.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = type,
            OccurredAt = _time.GetUtcNow(),
            PeerMaskedId = requester?.Masked,
            Outcome = outcome,
        }, cancellationToken) ?? Task.CompletedTask;

    private sealed record IssuedPasswordChallenge(
        PeerOnQId RequesterId,
        string RequesterFingerprint,
        string Challenge,
        DateTimeOffset ExpiresAt);
}
