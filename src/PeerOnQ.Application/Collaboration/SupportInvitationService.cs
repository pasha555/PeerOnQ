using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Collaboration;

public sealed record SupportInvitationRequest
{
    public required SessionMode Mode { get; init; }
    public required SessionPermission Permissions { get; init; }
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(1);
    public int MaximumUses { get; init; } = 1;
    public string? Password { get; init; }
    public PeerOnQId? AllowedRequesterDevice { get; init; }
    public string? AllowedTechnicianFingerprint { get; init; }
    public string? SupportNote { get; init; }
}

public sealed record IssuedSupportInvitation(
    Guid InvitationId,
    string Token,
    string OpenUri,
    DateTimeOffset ExpiresAt,
    int MaximumUses,
    SessionMode Mode,
    SessionPermission Permissions,
    string? SupportNote);

public sealed record SupportInvitationLink(
    PeerOnQId TargetDevice,
    string Token,
    SessionMode Mode,
    SessionPermission Permissions)
{
    public static bool TryParse(string? value, out SupportInvitationLink link)
    {
        link = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "peeronq", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "support", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) return false;
            var key = Uri.UnescapeDataString(pair[..separator]);
            var item = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (!parameters.TryAdd(key, item)) return false;
        }

        if (!parameters.TryGetValue("target", out var targetValue)
            || !PeerOnQId.TryParse(targetValue, out var target)
            || !parameters.TryGetValue("token", out var token)
            || !IsCanonicalToken(token)
            || !parameters.TryGetValue("mode", out var modeValue)
            || !Enum.TryParse<SessionMode>(modeValue, out var mode)
            || !parameters.TryGetValue("permissions", out var permissionsValue)
            || !int.TryParse(permissionsValue, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var rawPermissions))
        {
            return false;
        }

        var permissions = (SessionPermission)rawPermissions;
        if (!Phase1SessionScope.IsAllowed(mode, permissions, SessionAccessKind.SupportInvitation))
            return false;

        link = new SupportInvitationLink(target, token, mode, permissions);
        return true;
    }

    private static bool IsCanonicalToken(string token)
    {
        if (token.Length != 43 || token.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return false;
        try
        {
            return Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=").Length == TokenBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private const int TokenBytes = 32;
}

public sealed record SupportInvitationSummary(
    Guid InvitationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int MaximumUses,
    int UseCount,
    SessionMode Mode,
    SessionPermission Permissions,
    bool PasswordRequired,
    bool IsRevoked,
    string? SupportNote);

public enum SupportInvitationValidationCode
{
    Valid = 0,
    Malformed = 1,
    NotFound = 2,
    Expired = 3,
    Revoked = 4,
    Exhausted = 5,
    PermissionMismatch = 6,
    RequesterRestricted = 7,
    TechnicianRestricted = 8,
    PasswordRequired = 9,
    PasswordInvalid = 10,
    RateLimited = 11,
}

public sealed record SupportInvitationAuthorization(
    Guid InvitationId,
    SessionMode Mode,
    SessionPermission Permissions,
    string? SupportNote,
    DateTimeOffset ExpiresAt);

public sealed record SupportInvitationValidationResult(
    SupportInvitationValidationCode Code,
    SupportInvitationAuthorization? Authorization = null)
{
    public bool IsValid => Code == SupportInvitationValidationCode.Valid && Authorization is not null;
}

public sealed record SupportInvitationRecord
{
    public required Guid InvitationId { get; init; }
    public required byte[] TokenHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required int MaximumUses { get; init; }
    public required SessionMode Mode { get; init; }
    public required SessionPermission Permissions { get; init; }
    public int UseCount { get; init; }
    public PasswordCredential? Password { get; init; }
    public string? AllowedRequesterDevice { get; init; }
    public string? AllowedTechnicianFingerprint { get; init; }
    public string? SupportNote { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
}

public interface ISupportInvitationStore
{
    Task<IReadOnlyList<SupportInvitationRecord>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<SupportInvitationRecord> invitations, CancellationToken cancellationToken = default);
}

/// <summary>DPAPI-backed in the Windows composition; raw invitation tokens are never persisted.</summary>
public sealed class ProtectedSupportInvitationStore(IDeviceSecretStore secretStore) : ISupportInvitationStore
{
    private const string SecretName = "support-invitations-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SupportInvitationRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var bytes = await secretStore.TryGetAsync(SecretName, cancellationToken);
        if (bytes is null) return [];
        try
        {
            return JsonSerializer.Deserialize<SupportInvitationRecord[]>(bytes, JsonOptions)
                   ?? throw new InvalidDataException("The protected support invitation store is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<SupportInvitationRecord> invitations,
        CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(invitations, JsonOptions);
        try { await secretStore.SetAsync(SecretName, bytes, cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

public sealed class SupportInvitationService
{
    private const int TokenBytes = 32;
    private const int MaximumStoredInvitations = 256;
    private const int MaximumAttemptsPerMinute = 10;
    private const int MaximumTrackedRequesters = 4096;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(1);

    private readonly ISupportInvitationStore _store;
    private readonly ISecurityAuditLog? _audit;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, AttemptWindow> _attempts = new(StringComparer.Ordinal);

    public SupportInvitationService(
        ISupportInvitationStore store,
        ISecurityAuditLog? audit = null,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _audit = audit;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IssuedSupportInvitation> IssueAsync(
        PeerOnQId targetDevice,
        SupportInvitationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Phase1SessionScope.IsAllowed(request.Mode, request.Permissions, SessionAccessKind.SupportInvitation))
            throw new ArgumentException("The support invitation permission profile is invalid.", nameof(request));
        if (request.Lifetime < MinimumLifetime || request.Lifetime > MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(request), "Invitation lifetime must be between one minute and seven days.");
        if (request.MaximumUses is < 1 or > 25)
            throw new ArgumentOutOfRangeException(nameof(request), "Invitation use count must be between 1 and 25.");
        if (request.Password is { Length: > 0 and < 10 } or { Length: > 128 })
            throw new ArgumentException("An optional invitation password must be 10-128 characters.", nameof(request));

        var fingerprint = NormalizeFingerprint(request.AllowedTechnicianFingerprint);
        var note = NormalizeNote(request.SupportNote);
        var tokenBytes = RandomNumberGenerator.GetBytes(TokenBytes);
        var token = Base64UrlEncode(tokenBytes);
        var tokenHash = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);

        var now = _time.GetUtcNow();
        var record = new SupportInvitationRecord
        {
            InvitationId = Guid.NewGuid(),
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + request.Lifetime,
            MaximumUses = request.MaximumUses,
            Mode = request.Mode,
            Permissions = request.Permissions,
            Password = string.IsNullOrEmpty(request.Password)
                ? null
                : ProtectedCollaborationProfileStore.DerivePassword(request.Password),
            AllowedRequesterDevice = request.AllowedRequesterDevice?.Value,
            AllowedTechnicianFingerprint = fingerprint,
            SupportNote = note,
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var retained = (await _store.LoadAsync(cancellationToken))
                .Where(item => item.ExpiresAt > now - TimeSpan.FromDays(90))
                .OrderByDescending(item => item.CreatedAt)
                .Take(MaximumStoredInvitations - 1)
                .ToList();
            retained.Insert(0, record);
            await _store.SaveAsync(retained, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        await AuditAsync(SecurityAuditEventType.SupportInvitationIssued, record, "issued", cancellationToken);
        var openUri = $"peeronq://support?target={Uri.EscapeDataString(targetDevice.Value)}" +
                      $"&token={Uri.EscapeDataString(token)}" +
                      $"&mode={Uri.EscapeDataString(record.Mode.ToString())}" +
                      $"&permissions={(int)record.Permissions}";
        return new IssuedSupportInvitation(
            record.InvitationId,
            token,
            openUri,
            record.ExpiresAt,
            record.MaximumUses,
            record.Mode,
            record.Permissions,
            record.SupportNote);
    }

    public async Task<IReadOnlyList<SupportInvitationSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await _store.LoadAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .Select(ToSummary)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RevokeAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        SupportInvitationRecord? revoked = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var invitations = (await _store.LoadAsync(cancellationToken)).ToList();
            var index = invitations.FindIndex(item => item.InvitationId == invitationId);
            if (index < 0 || invitations[index].RevokedAt is not null) return false;
            revoked = invitations[index] with { RevokedAt = _time.GetUtcNow() };
            invitations[index] = revoked;
            await _store.SaveAsync(invitations, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        await AuditAsync(SecurityAuditEventType.SupportInvitationRevoked, revoked!, "revoked", cancellationToken);
        return true;
    }

    public async Task<SupportInvitationValidationResult> ValidateAsync(
        string token,
        string? password,
        PeerOnQId requesterDevice,
        string? requesterFingerprint,
        SessionMode requestedMode,
        SessionPermission requestedPermissions,
        CancellationToken cancellationToken = default)
    {
        if (!TryRegisterAttempt(requesterDevice.Value))
            return await RejectAsync(SupportInvitationValidationCode.RateLimited, requesterDevice, null, cancellationToken);

        if (!TryDecodeToken(token, out var tokenBytes))
            return await RejectAsync(SupportInvitationValidationCode.Malformed, requesterDevice, null, cancellationToken);

        byte[] tokenHash;
        try { tokenHash = SHA256.HashData(tokenBytes); }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }

        SupportInvitationRecord? record;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            record = (await _store.LoadAsync(cancellationToken)).FirstOrDefault(item =>
                item.TokenHash.Length == tokenHash.Length
                && CryptographicOperations.FixedTimeEquals(item.TokenHash, tokenHash));
        }
        finally
        {
            _gate.Release();
            CryptographicOperations.ZeroMemory(tokenHash);
        }

        if (record is null)
            return await RejectAsync(SupportInvitationValidationCode.NotFound, requesterDevice, null, cancellationToken);

        var now = _time.GetUtcNow();
        if (record.RevokedAt is not null)
            return await RejectAsync(SupportInvitationValidationCode.Revoked, requesterDevice, record, cancellationToken);
        if (record.ExpiresAt <= now)
            return await RejectAsync(SupportInvitationValidationCode.Expired, requesterDevice, record, cancellationToken);
        if (record.UseCount >= record.MaximumUses)
            return await RejectAsync(SupportInvitationValidationCode.Exhausted, requesterDevice, record, cancellationToken);
        if (record.Mode != requestedMode || record.Permissions != requestedPermissions)
            return await RejectAsync(SupportInvitationValidationCode.PermissionMismatch, requesterDevice, record, cancellationToken);
        if (record.AllowedRequesterDevice is { } allowedDevice
            && !string.Equals(allowedDevice, requesterDevice.Value, StringComparison.Ordinal))
            return await RejectAsync(SupportInvitationValidationCode.RequesterRestricted, requesterDevice, record, cancellationToken);
        if (record.AllowedTechnicianFingerprint is { } allowedFingerprint
            && (!TryNormalizeFingerprint(requesterFingerprint, out var normalizedRequesterFingerprint)
                || !string.Equals(allowedFingerprint, normalizedRequesterFingerprint, StringComparison.Ordinal)))
            return await RejectAsync(SupportInvitationValidationCode.TechnicianRestricted, requesterDevice, record, cancellationToken);
        if (record.Password is not null && string.IsNullOrEmpty(password))
            return await RejectAsync(SupportInvitationValidationCode.PasswordRequired, requesterDevice, record, cancellationToken);
        if (record.Password is not null
            && !ProtectedCollaborationProfileStore.VerifyPassword(password!, record.Password))
            return await RejectAsync(SupportInvitationValidationCode.PasswordInvalid, requesterDevice, record, cancellationToken);

        return new SupportInvitationValidationResult(
            SupportInvitationValidationCode.Valid,
            new SupportInvitationAuthorization(
                record.InvitationId,
                record.Mode,
                record.Permissions,
                record.SupportNote,
                record.ExpiresAt));
    }

    /// <summary>Called only after the local user explicitly approves the permission dialog.</summary>
    public async Task<bool> TryConsumeApprovedAsync(
        SupportInvitationAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        SupportInvitationRecord? consumed = null;
        var now = _time.GetUtcNow();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var invitations = (await _store.LoadAsync(cancellationToken)).ToList();
            var index = invitations.FindIndex(item => item.InvitationId == authorization.InvitationId);
            if (index < 0) return false;
            var record = invitations[index];
            if (record.RevokedAt is not null || record.ExpiresAt <= now || record.UseCount >= record.MaximumUses)
                return false;
            if (record.Mode != authorization.Mode
                || record.Permissions != authorization.Permissions
                || record.ExpiresAt != authorization.ExpiresAt)
                return false;
            consumed = record with { UseCount = record.UseCount + 1 };
            invitations[index] = consumed;
            await _store.SaveAsync(invitations, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        await AuditAsync(SecurityAuditEventType.SupportInvitationUsed, consumed!, "approved", cancellationToken);
        return true;
    }

    private bool TryRegisterAttempt(string requester)
    {
        var now = _time.GetUtcNow();
        if (!_attempts.ContainsKey(requester) && _attempts.Count >= MaximumTrackedRequesters)
        {
            foreach (var candidate in _attempts)
            {
                lock (candidate.Value.Gate)
                {
                    while (candidate.Value.Attempts.Count > 0
                           && now - candidate.Value.Attempts.Peek() >= TimeSpan.FromMinutes(1))
                    {
                        candidate.Value.Attempts.Dequeue();
                    }
                    if (candidate.Value.Attempts.Count == 0)
                        _attempts.TryRemove(candidate);
                }
            }
            if (_attempts.Count >= MaximumTrackedRequesters) return false;
        }

        var window = _attempts.GetOrAdd(requester, _ => new AttemptWindow());
        lock (window.Gate)
        {
            while (window.Attempts.Count > 0 && now - window.Attempts.Peek() >= TimeSpan.FromMinutes(1))
                window.Attempts.Dequeue();
            if (window.Attempts.Count >= MaximumAttemptsPerMinute) return false;
            window.Attempts.Enqueue(now);
            return true;
        }
    }

    private async Task<SupportInvitationValidationResult> RejectAsync(
        SupportInvitationValidationCode code,
        PeerOnQId requester,
        SupportInvitationRecord? invitation,
        CancellationToken cancellationToken)
    {
        if (_audit is not null)
        {
            await _audit.AppendAsync(new SecurityAuditEvent
            {
                EventId = Guid.NewGuid(),
                EventType = SecurityAuditEventType.SupportInvitationRejected,
                OccurredAt = _time.GetUtcNow(),
                PeerMaskedId = requester.Masked,
                PermissionSet = invitation?.Permissions,
                Outcome = "rejected",
                FailureCategory = code.ToString(),
                SafeMetadata = invitation is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["invitationId"] = invitation.InvitationId.ToString("D") },
            }, cancellationToken);
        }
        return new SupportInvitationValidationResult(code);
    }

    private Task AuditAsync(
        SecurityAuditEventType eventType,
        SupportInvitationRecord invitation,
        string outcome,
        CancellationToken cancellationToken) =>
        _audit?.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            OccurredAt = _time.GetUtcNow(),
            PermissionSet = invitation.Permissions,
            Outcome = outcome,
            SafeMetadata = new Dictionary<string, string>
            {
                ["invitationId"] = invitation.InvitationId.ToString("D"),
                ["maximumUses"] = invitation.MaximumUses.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["useCount"] = invitation.UseCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        }, cancellationToken) ?? Task.CompletedTask;

    private static SupportInvitationSummary ToSummary(SupportInvitationRecord item) => new(
        item.InvitationId,
        item.CreatedAt,
        item.ExpiresAt,
        item.MaximumUses,
        item.UseCount,
        item.Mode,
        item.Permissions,
        item.Password is not null,
        item.RevokedAt is not null,
        item.SupportNote);

    private static string? NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        var normalized = fingerprint.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit))
            throw new ArgumentException("A technician fingerprint must be 64 hexadecimal characters.", nameof(fingerprint));
        return normalized;
    }

    private static bool TryNormalizeFingerprint(string? fingerprint, out string? normalized)
    {
        try
        {
            normalized = NormalizeFingerprint(fingerprint);
            return normalized is not null;
        }
        catch (ArgumentException)
        {
            normalized = null;
            return false;
        }
    }

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var normalized = note.Trim();
        if (normalized.Length > 512) throw new ArgumentException("A support note cannot exceed 512 characters.", nameof(note));
        return normalized;
    }

    private static bool TryDecodeToken(string token, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 40 or > 64) return false;
        try
        {
            var normalized = token.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
            bytes = Convert.FromBase64String(normalized);
            if (bytes.Length == TokenBytes) return true;
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class AttemptWindow
    {
        public Lock Gate { get; } = new();
        public Queue<DateTimeOffset> Attempts { get; } = new();
    }
}
