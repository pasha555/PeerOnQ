using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Collaboration;

public sealed class TrustedDeviceService(
    ICollaborationProfileStore store,
    ISecurityAuditLog? audit = null,
    TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<TrustedDevice>> ListAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        return (await store.LoadAsync(cancellationToken)).TrustedDevices
            .Select(device => device.TrustState == DeviceTrustState.Trusted
                              && device.ExpiresAt is { } expiry
                              && expiry <= now
                ? device with { TrustState = DeviceTrustState.Expired }
                : device)
            .ToArray();
    }

    public async Task<TrustedDevice> ApproveAsync(
        PeerOnQId deviceId,
        string displayName,
        string publicKeyFingerprint,
        SessionPermission allowedPermissions,
        DateTimeOffset? expiresAt,
        bool localUserConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!localUserConfirmed) throw new InvalidOperationException("Trust approval requires a local user action.");
        ValidateFingerprint(publicKeyFingerprint);
        if (!Phase1SessionScope.HasInteractiveMediaPermissions(allowedPermissions))
            throw new ArgumentOutOfRangeException(nameof(allowedPermissions));
        var now = _time.GetUtcNow();
        if (expiresAt is not null && expiresAt <= now) throw new ArgumentOutOfRangeException(nameof(expiresAt));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            var devices = profile.TrustedDevices.ToList();
            var existingIndex = devices.FindIndex(item => item.DeviceId == deviceId);
            var trusted = new TrustedDevice
            {
                RecordId = existingIndex >= 0 ? devices[existingIndex].RecordId : Guid.NewGuid(),
                DeviceId = deviceId,
                DisplayName = SanitizeDisplayName(displayName),
                PublicKeyFingerprint = publicKeyFingerprint.ToLowerInvariant(),
                AllowedPermissions = allowedPermissions,
                ApprovedAt = now,
                ExpiresAt = expiresAt,
                TrustState = DeviceTrustState.Trusted,
            };
            if (existingIndex >= 0) devices[existingIndex] = trusted; else devices.Add(trusted);
            await store.SaveAsync(profile with { TrustedDevices = devices }, cancellationToken);
            await AuditAsync(existingIndex >= 0 ? SecurityAuditEventType.TrustedDeviceChanged : SecurityAuditEventType.TrustedDeviceAdded,
                deviceId, "approved", cancellationToken);
            return trusted;
        }
        finally { _gate.Release(); }
    }

    public async Task<TrustedDevice?> ValidateAsync(
        PeerOnQId deviceId,
        string presentedFingerprint,
        SessionPermission requestedPermissions,
        CancellationToken cancellationToken = default)
    {
        if (!Phase1SessionScope.HasInteractiveMediaPermissions(requestedPermissions)) return null;
        ValidateFingerprint(presentedFingerprint);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            var devices = profile.TrustedDevices.ToList();
            var index = devices.FindIndex(item => item.DeviceId == deviceId);
            if (index < 0) return null;
            var trusted = devices[index];
            var now = _time.GetUtcNow();

            if (!string.Equals(trusted.PublicKeyFingerprint, presentedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                trusted = trusted with { TrustState = DeviceTrustState.FingerprintChanged, RevokedAt = now };
                devices[index] = trusted;
                await store.SaveAsync(profile with { TrustedDevices = devices }, cancellationToken);
                await AuditAsync(SecurityAuditEventType.TrustedDeviceRevoked, deviceId, "fingerprint_changed", cancellationToken);
                return null;
            }

            if (trusted.TrustState == DeviceTrustState.Trusted
                && trusted.ExpiresAt is { } expiry
                && expiry <= now)
            {
                trusted = trusted with { TrustState = DeviceTrustState.Expired };
                devices[index] = trusted;
                await store.SaveAsync(profile with { TrustedDevices = devices }, cancellationToken);
                await AuditAsync(SecurityAuditEventType.TrustedDeviceChanged, deviceId, "expired", cancellationToken);
                return null;
            }

            if ((requestedPermissions & ~trusted.AllowedPermissions) != 0)
            {
                await AuditAsync(
                    SecurityAuditEventType.TrustedDeviceChanged,
                    deviceId,
                    "scope_expansion_rejected",
                    cancellationToken);
                return null;
            }

            if (!trusted.IsUsableAt(now))
                return null;

            trusted = trusted with { LastUsedAt = now };
            devices[index] = trusted;
            await store.SaveAsync(profile with { TrustedDevices = devices }, cancellationToken);
            return trusted;
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeAsync(PeerOnQId deviceId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = await store.LoadAsync(cancellationToken);
            var devices = profile.TrustedDevices.ToList();
            var index = devices.FindIndex(item => item.DeviceId == deviceId);
            if (index < 0) return;
            devices[index] = devices[index] with
            {
                TrustState = DeviceTrustState.Revoked,
                RevokedAt = _time.GetUtcNow(),
                AllowedPermissions = SessionPermission.None,
            };
            await store.SaveAsync(profile with { TrustedDevices = devices }, cancellationToken);
            await AuditAsync(SecurityAuditEventType.TrustedDeviceRevoked, deviceId, "revoked", cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private Task AuditAsync(SecurityAuditEventType type, PeerOnQId id, string outcome, CancellationToken token) =>
        audit?.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = type,
            OccurredAt = _time.GetUtcNow(),
            PeerMaskedId = id.Masked,
            Outcome = outcome,
        }, token) ?? Task.CompletedTask;

    private static void ValidateFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA-256 public-key fingerprint is required.", nameof(fingerprint));
    }

    private static string SanitizeDisplayName(string value)
    {
        var clean = new string(value.Where(character => !char.IsControl(character)).Take(80).ToArray()).Trim();
        return clean.Length == 0 ? "Trusted device" : clean;
    }
}
