using System.Globalization;

namespace PeerOnQ.Cloud.Domain.Entities;

public sealed class Device
{
    private Device() { }

    private Device(
        Guid id,
        byte[] publicDeviceIdHash,
        string maskedPublicDeviceId,
        string displayName,
        string identityFingerprint,
        int publicDeviceIdCollisionCounter,
        int publicDeviceIdKeyVersion,
        DateTimeOffset now)
    {
        Id = id;
        PublicDeviceIdHash = publicDeviceIdHash.ToArray();
        MaskedPublicDeviceId = maskedPublicDeviceId;
        DisplayName = displayName;
        IdentityFingerprint = identityFingerprint;
        PublicDeviceIdCollisionCounter = publicDeviceIdCollisionCounter;
        PublicDeviceIdKeyVersion = publicDeviceIdKeyVersion;
        IdentityVersion = 1;
        CreatedAtUtc = now;
        LastSeenAtUtc = now;
        ConcurrencyVersion = 1;
    }

    public Guid Id { get; private set; }
    public byte[] PublicDeviceIdHash { get; private set; } = [];
    public string MaskedPublicDeviceId { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string IdentityFingerprint { get; private set; } = string.Empty;
    public int PublicDeviceIdCollisionCounter { get; private set; } = -1;
    public int PublicDeviceIdKeyVersion { get; private set; }
    public int IdentityVersion { get; private set; }
    public Guid? OwnerUserId { get; private set; }
    public Guid? OwnerOrganizationId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset LastSeenAtUtc { get; private set; }
    public bool IsRevoked { get; private set; }
    public DateTimeOffset? RevokedAtUtc { get; private set; }
    public string? RevocationReason { get; private set; }
    public long ConcurrencyVersion { get; private set; }

    public static Device Create(
        byte[] publicDeviceIdHash,
        string maskedPublicDeviceId,
        string displayName,
        string identityFingerprint,
        DateTimeOffset now)
        => Create(publicDeviceIdHash, maskedPublicDeviceId, displayName, identityFingerprint, -1, 0, now);

    public static Device Create(
        byte[] publicDeviceIdHash,
        string maskedPublicDeviceId,
        string displayName,
        string identityFingerprint,
        int publicDeviceIdCollisionCounter,
        int publicDeviceIdKeyVersion,
        DateTimeOffset now)
    {
        if (publicDeviceIdHash is not { Length: 32 })
            throw new ArgumentException("The keyed public device ID hash must contain 32 bytes.", nameof(publicDeviceIdHash));

        var normalizedName = NormalizeRequired(displayName, 128, nameof(displayName));
        var normalizedMaskedId = NormalizeRequired(maskedPublicDeviceId, 32, nameof(maskedPublicDeviceId));
        var normalizedFingerprint = NormalizeHex(identityFingerprint, 64, nameof(identityFingerprint));
        if (!((publicDeviceIdCollisionCounter == -1 && publicDeviceIdKeyVersion == 0) ||
              (publicDeviceIdCollisionCounter is >= 0 and <= 4095 && publicDeviceIdKeyVersion > 0)))
            throw new ArgumentException("Public alias key version and collision counter are inconsistent.");
        return new Device(Guid.NewGuid(), publicDeviceIdHash, normalizedMaskedId, normalizedName,
            normalizedFingerprint, publicDeviceIdCollisionCounter, publicDeviceIdKeyVersion, now);
    }

    public void AssignServerAlias(
        byte[] publicDeviceIdHash,
        string maskedPublicDeviceId,
        int collisionCounter,
        int keyVersion,
        DateTimeOffset now)
    {
        EnsureActive();
        if (PublicDeviceIdCollisionCounter >= 0)
            throw new InvalidOperationException("The device already has a server-assigned public alias.");
        if (publicDeviceIdHash is not { Length: 32 })
            throw new ArgumentException("The keyed public device ID hash must contain 32 bytes.", nameof(publicDeviceIdHash));
        if (collisionCounter is < 0 or > 4095)
            throw new ArgumentOutOfRangeException(nameof(collisionCounter));
        if (keyVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(keyVersion));

        PublicDeviceIdHash = publicDeviceIdHash.ToArray();
        MaskedPublicDeviceId = NormalizeRequired(maskedPublicDeviceId, 32, nameof(maskedPublicDeviceId));
        PublicDeviceIdCollisionCounter = collisionCounter;
        PublicDeviceIdKeyVersion = keyVersion;
        LastSeenAtUtc = now;
        ConcurrencyVersion++;
    }

    public bool MatchesFingerprint(string fingerprint) =>
        string.Equals(IdentityFingerprint, NormalizeHex(fingerprint, 64, nameof(fingerprint)), StringComparison.Ordinal);

    public void Touch(string displayName, DateTimeOffset now)
    {
        EnsureActive();
        DisplayName = NormalizeRequired(displayName, 128, nameof(displayName));
        LastSeenAtUtc = now;
        ConcurrencyVersion++;
    }

    public void RotateIdentity(string newFingerprint, DateTimeOffset now)
    {
        EnsureActive();
        IdentityFingerprint = NormalizeHex(newFingerprint, 64, nameof(newFingerprint));
        IdentityVersion++;
        LastSeenAtUtc = now;
        ConcurrencyVersion++;
    }

    public void Revoke(string reason, DateTimeOffset now)
    {
        if (IsRevoked) return;
        RevocationReason = NormalizeRequired(reason, 256, nameof(reason));
        IsRevoked = true;
        RevokedAtUtc = now;
        ConcurrencyVersion++;
    }

    public void AssignOwner(Guid ownerUserId, Guid? ownerOrganizationId, DateTimeOffset now)
    {
        EnsureActive();
        if (ownerUserId == Guid.Empty) throw new ArgumentException("Owner user ID is required.", nameof(ownerUserId));
        if (ownerOrganizationId == Guid.Empty) throw new ArgumentException("Owner organization ID is invalid.", nameof(ownerOrganizationId));
        OwnerUserId = ownerUserId;
        OwnerOrganizationId = ownerOrganizationId;
        LastSeenAtUtc = now;
        ConcurrencyVersion++;
    }

    private void EnsureActive()
    {
        if (IsRevoked) throw new InvalidOperationException("The device identity is revoked.");
    }

    internal static string NormalizeRequired(string value, int maxLength, string parameter)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maxLength)
            throw new ArgumentOutOfRangeException(parameter, $"Value must contain 1 to {maxLength.ToString(CultureInfo.InvariantCulture)} characters.");
        return normalized;
    }

    internal static string NormalizeHex(string value, int length, string parameter)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length != length || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException($"Value must be a {length}-character hexadecimal string.", parameter);
        return normalized;
    }
}
