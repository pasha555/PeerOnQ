using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using System.Globalization;
using PeerOnQ.Cloud.Application.Abstractions;

namespace PeerOnQ.Cloud.Application.Security;

public sealed class PublicDeviceIdService : IPublicDeviceIdService, IPrivacyHasher
{
    private readonly IReadOnlyDictionary<int, byte[]> _keys;

    public PublicDeviceIdService(ReadOnlySpan<byte> key)
        : this(1, key, null)
    {
    }

    public PublicDeviceIdService(
        int activeKeyVersion,
        ReadOnlySpan<byte> activeKey,
        IReadOnlyDictionary<int, byte[]>? previousKeys)
    {
        if (activeKeyVersion <= 0) throw new ArgumentOutOfRangeException(nameof(activeKeyVersion));
        if (activeKey.Length < 32)
            throw new ArgumentException("The keyed-hash secret must contain at least 32 bytes.", nameof(activeKey));
        if (previousKeys?.Count > 3)
            throw new ArgumentException("At most three previous public-ID keys may be retained.", nameof(previousKeys));

        var keys = new Dictionary<int, byte[]> { [activeKeyVersion] = activeKey.ToArray() };
        if (previousKeys is not null)
        {
            foreach (var (version, value) in previousKeys)
            {
                if (version <= 0 || version == activeKeyVersion || value is not { Length: >= 32 } ||
                    !keys.TryAdd(version, value.ToArray()))
                {
                    throw new ArgumentException("Previous public-ID key configuration is invalid.", nameof(previousKeys));
                }
            }
        }

        ActiveKeyVersion = activeKeyVersion;
        _keys = keys;
    }

    public int ActiveKeyVersion { get; }

    public string Normalize(string publicDeviceId)
    {
        var input = publicDeviceId?.Trim() ?? string.Empty;
        if (input.Length is 0 or > 32 || input.Any(character => !char.IsAsciiDigit(character) && character != '-'))
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Public device ID has an invalid format.");

        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 12)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Public device ID must contain exactly 12 digits.");

        return string.Create(15, digits, static (span, value) =>
        {
            value.AsSpan(0, 3).CopyTo(span);
            span[3] = '-';
            value.AsSpan(3, 3).CopyTo(span[4..]);
            span[7] = '-';
            value.AsSpan(6, 3).CopyTo(span[8..]);
            span[11] = '-';
            value.AsSpan(9, 3).CopyTo(span[12..]);
        });
    }

    public byte[] ComputeLookupHash(string publicDeviceId) =>
        ComputeHash(ActiveKeyVersion, "public-device-id", Normalize(publicDeviceId));

    public IReadOnlyList<byte[]> ComputeLookupHashes(string publicDeviceId)
    {
        var normalized = Normalize(publicDeviceId);
        return _keys.OrderByDescending(value => value.Key == ActiveKeyVersion)
            .ThenByDescending(value => value.Key)
            .Select(value => ComputeHash(value.Key, "public-device-id", normalized))
            .ToArray();
    }

    public string Mask(string publicDeviceId)
    {
        var normalized = Normalize(publicDeviceId);
        return $"{normalized[..3]}-***-***-{normalized[^3..]}";
    }

    public ServerAssignedPublicDeviceId DeriveFromFingerprint(
        string identityFingerprint,
        int collisionCounter,
        int? keyVersion = null)
    {
        var fingerprint = identityFingerprint?.Trim().ToLowerInvariant() ?? string.Empty;
        if (fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Identity fingerprint must be a 64-character hexadecimal SHA-256 digest.", nameof(identityFingerprint));
        if (collisionCounter is < 0 or > 4095)
            throw new ArgumentOutOfRangeException(nameof(collisionCounter), "Alias collision counter must be between 0 and 4095.");

        var selectedKeyVersion = keyVersion ?? ActiveKeyVersion;
        if (!_keys.ContainsKey(selectedKeyVersion))
            throw new InvalidOperationException($"Public device ID key version {selectedKeyVersion} is not configured.");
        var digest = ComputeHash(selectedKeyVersion, "public-device-alias",
            $"{fingerprint}:{collisionCounter.ToString(CultureInfo.InvariantCulture)}");
        var numeric = BinaryPrimitives.ReadUInt64BigEndian(digest) % 1_000_000_000_000UL;
        var value = Normalize(numeric.ToString("D12", CultureInfo.InvariantCulture));
        return new ServerAssignedPublicDeviceId(
            value,
            ComputeHash(selectedKeyVersion, "public-device-id", value),
            Mask(value),
            collisionCounter,
            selectedKeyVersion);
    }

    public byte[] ComputeHash(string purpose, string value)
        => ComputeHash(ActiveKeyVersion, purpose, value);

    private byte[] ComputeHash(int keyVersion, string purpose, string value)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 64)
            throw new ArgumentException("Hash purpose is required and must be at most 64 characters.", nameof(purpose));
        if (string.IsNullOrEmpty(value) || value.Length > 4096)
            throw new ArgumentException("Hash input is required and must be at most 4096 characters.", nameof(value));

        var bytes = Encoding.UTF8.GetBytes($"peeronq:{purpose}:{value}");
        return HMACSHA256.HashData(_keys[keyVersion], bytes);
    }
}
