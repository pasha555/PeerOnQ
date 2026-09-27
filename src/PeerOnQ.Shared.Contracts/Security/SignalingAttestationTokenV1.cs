using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PeerOnQ.Shared.Contracts.Security;

public sealed record SignalingAttestationClaimsV1(
    string Issuer,
    string Audience,
    string PublicDeviceId,
    Guid DeviceId,
    Guid InstallationId,
    string SpkiSha256,
    string KeyId,
    long IssuedAtUnixSeconds,
    long ExpiresAtUnixSeconds,
    string TokenId,
    Guid? OrganizationId = null,
    int OrganizationPolicyFlags = SignalingOrganizationPolicyFlags.Unmanaged,
    string? MinimumClientVersion = null,
    string? ApprovedRelayRegionsCsv = null);

public static class SignalingOrganizationPolicyFlags
{
    public const int Unmanaged = -1;
    public const int ViewOnly = 1 << 0;
    public const int FullControl = 1 << 1;
    public const int FileTransfer = 1 << 2;
    public const int Unattended = 1 << 3;
    public const int Clipboard = 1 << 4;
    public const int HybridRequired = 1 << 5;
    public const int Known = ViewOnly | FullControl | FileTransfer | Unattended | Clipboard | HybridRequired;
}

public enum SignalingAttestationValidationError
{
    None = 0,
    Malformed = 1,
    UnsupportedVersion = 2,
    InvalidClaims = 3,
    UnknownKey = 4,
    InvalidSignature = 5,
    InvalidIssuer = 6,
    InvalidAudience = 7,
    NotYetValid = 8,
    Expired = 9,
    LifetimeExceeded = 10,
}

/// <summary>
/// Compact, versioned ECDSA P-256 attestation used only to bind a cloud-assigned routing alias
/// to the device key that already proved possession to the cloud. This is intentionally not a
/// general JWT implementation; every field, size and algorithm is fixed by this protocol.
/// </summary>
public static class SignalingAttestationTokenV1
{
    public const string Prefix = "pqsa1";
    public const int MaximumTokenCharacters = 4096;
    public const int MaximumPayloadBytes = 2048;

    private const int Version1ClaimCount = 11;
    private const int Version2ClaimCount = 15;

    public static string Issue(SignalingAttestationClaimsV1 claims, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        if (!ClaimsAreStructurallyValid(claims) || privateKey.KeySize != 256)
            throw new ArgumentException("The signaling attestation claims or signing key are invalid.", nameof(claims));

        var payload = WritePayload(claims);
        var encodedPayload = Base64UrlEncode(payload);
        var signingInput = Encoding.ASCII.GetBytes($"{Prefix}.{encodedPayload}");
        byte[] signature;
        lock (privateKey)
        {
            var actualKeyId = ComputeKeyId(privateKey.ExportSubjectPublicKeyInfo());
            if (!FixedTimeHexEquals(actualKeyId, claims.KeyId))
                throw new ArgumentException("The signaling attestation key identifier does not match the signing key.", nameof(claims));
            signature = privateKey.SignData(
                signingInput,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        return $"{Prefix}.{encodedPayload}.{Base64UrlEncode(signature)}";
    }

    public static bool TryValidate(
        string? token,
        IReadOnlyDictionary<string, byte[]> publicKeysById,
        string expectedIssuer,
        string expectedAudience,
        DateTimeOffset nowUtc,
        TimeSpan clockSkew,
        TimeSpan maximumLifetime,
        out SignalingAttestationClaimsV1? claims,
        out SignalingAttestationValidationError error)
    {
        claims = null;
        error = SignalingAttestationValidationError.Malformed;
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenCharacters
            || clockSkew < TimeSpan.Zero || clockSkew > TimeSpan.FromMinutes(2)
            || maximumLifetime <= TimeSpan.Zero || maximumLifetime > TimeSpan.FromMinutes(15))
        {
            return false;
        }

        var firstDot = token.IndexOf('.');
        var secondDot = firstDot < 0 ? -1 : token.IndexOf('.', firstDot + 1);
        if (firstDot != Prefix.Length || secondDot <= firstDot + 1
            || token.IndexOf('.', secondDot + 1) >= 0
            || !token.AsSpan(0, firstDot).SequenceEqual(Prefix))
        {
            error = firstDot > 0 && !token.AsSpan(0, firstDot).SequenceEqual(Prefix)
                ? SignalingAttestationValidationError.UnsupportedVersion
                : SignalingAttestationValidationError.Malformed;
            return false;
        }

        if (!TryBase64UrlDecode(token.AsSpan(firstDot + 1, secondDot - firstDot - 1), MaximumPayloadBytes, out var payload)
            || !TryBase64UrlDecode(token.AsSpan(secondDot + 1), 128, out var signature)
            || signature.Length != 64
            || !TryReadPayload(payload, out claims)
            || claims is null)
        {
            return false;
        }

        if (!ClaimsAreStructurallyValid(claims))
        {
            error = SignalingAttestationValidationError.InvalidClaims;
            claims = null;
            return false;
        }

        if (!publicKeysById.TryGetValue(claims.KeyId, out var publicKey))
        {
            error = SignalingAttestationValidationError.UnknownKey;
            claims = null;
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || ecdsa.KeySize != 256
                || !ecdsa.VerifyData(
                    Encoding.ASCII.GetBytes(token[..secondDot]),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                error = SignalingAttestationValidationError.InvalidSignature;
                claims = null;
                return false;
            }
        }
        catch (CryptographicException)
        {
            error = SignalingAttestationValidationError.InvalidSignature;
            claims = null;
            return false;
        }

        if (!string.Equals(claims.Issuer, expectedIssuer, StringComparison.Ordinal))
        {
            error = SignalingAttestationValidationError.InvalidIssuer;
            claims = null;
            return false;
        }
        if (!string.Equals(claims.Audience, expectedAudience, StringComparison.Ordinal))
        {
            error = SignalingAttestationValidationError.InvalidAudience;
            claims = null;
            return false;
        }

        DateTimeOffset issuedAt;
        DateTimeOffset expiresAt;
        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(claims.IssuedAtUnixSeconds);
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(claims.ExpiresAtUnixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            error = SignalingAttestationValidationError.InvalidClaims;
            claims = null;
            return false;
        }

        if (expiresAt - issuedAt > maximumLifetime)
        {
            error = SignalingAttestationValidationError.LifetimeExceeded;
            claims = null;
            return false;
        }
        if (issuedAt > nowUtc + clockSkew)
        {
            error = SignalingAttestationValidationError.NotYetValid;
            claims = null;
            return false;
        }
        if (expiresAt <= nowUtc - clockSkew)
        {
            error = SignalingAttestationValidationError.Expired;
            claims = null;
            return false;
        }

        error = SignalingAttestationValidationError.None;
        return true;
    }

    public static string ComputeKeyId(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo)).ToLowerInvariant();

    public static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] WritePayload(SignalingAttestationClaimsV1 claims)
    {
        var output = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            var managed = claims.OrganizationId is not null;
            writer.WriteNumber("v", managed ? 2 : 1);
            writer.WriteString("iss", claims.Issuer);
            writer.WriteString("aud", claims.Audience);
            writer.WriteString("alias", claims.PublicDeviceId);
            writer.WriteString("did", claims.DeviceId.ToString("N"));
            writer.WriteString("iid", claims.InstallationId.ToString("N"));
            writer.WriteString("spki", claims.SpkiSha256);
            writer.WriteString("kid", claims.KeyId);
            writer.WriteNumber("iat", claims.IssuedAtUnixSeconds);
            writer.WriteNumber("exp", claims.ExpiresAtUnixSeconds);
            writer.WriteString("jti", claims.TokenId);
            if (managed)
            {
                writer.WriteString("oid", claims.OrganizationId!.Value.ToString("N"));
                writer.WriteNumber("policy", claims.OrganizationPolicyFlags);
                writer.WriteString("min", claims.MinimumClientVersion ?? string.Empty);
                writer.WriteString("regions", claims.ApprovedRelayRegionsCsv ?? string.Empty);
            }
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }

    private static bool TryReadPayload(ReadOnlySpan<byte> payload, out SignalingAttestationClaimsV1? claims)
    {
        claims = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? version = null;
        string? issuer = null, audience = null, alias = null, device = null, installation = null;
        string? spki = null, keyId = null, tokenId = null, organization = null, minimumVersion = null, relayRegions = null;
        long? issuedAt = null, expiresAt = null;
        int? policyFlags = null;

        try
        {
            var reader = new Utf8JsonReader(payload, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2,
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) return false;
                var name = reader.GetString();
                if (name is null || !seen.Add(name) || !reader.Read()) return false;
                switch (name)
                {
                    case "v" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v): version = v; break;
                    case "iss" when reader.TokenType == JsonTokenType.String: issuer = reader.GetString(); break;
                    case "aud" when reader.TokenType == JsonTokenType.String: audience = reader.GetString(); break;
                    case "alias" when reader.TokenType == JsonTokenType.String: alias = reader.GetString(); break;
                    case "did" when reader.TokenType == JsonTokenType.String: device = reader.GetString(); break;
                    case "iid" when reader.TokenType == JsonTokenType.String: installation = reader.GetString(); break;
                    case "spki" when reader.TokenType == JsonTokenType.String: spki = reader.GetString(); break;
                    case "kid" when reader.TokenType == JsonTokenType.String: keyId = reader.GetString(); break;
                    case "iat" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var iat): issuedAt = iat; break;
                    case "exp" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var exp): expiresAt = exp; break;
                    case "jti" when reader.TokenType == JsonTokenType.String: tokenId = reader.GetString(); break;
                    case "oid" when reader.TokenType == JsonTokenType.String: organization = reader.GetString(); break;
                    case "policy" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var policy): policyFlags = policy; break;
                    case "min" when reader.TokenType == JsonTokenType.String: minimumVersion = reader.GetString(); break;
                    case "regions" when reader.TokenType == JsonTokenType.String: relayRegions = reader.GetString(); break;
                    default: return false;
                }
            }

            var version1 = version == 1 && seen.Count == Version1ClaimCount;
            var version2 = version == 2 && seen.Count == Version2ClaimCount;
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || (!version1 && !version2)
                || !Guid.TryParseExact(device, "N", out var deviceId)
                || !Guid.TryParseExact(installation, "N", out var installationId)
                || issuer is null || audience is null || alias is null || spki is null || keyId is null
                || issuedAt is null || expiresAt is null || tokenId is null)
            {
                return false;
            }

            Guid? organizationId = null;
            if (version2)
            {
                if (!Guid.TryParseExact(organization, "N", out var parsedOrganizationId) || policyFlags is null || minimumVersion is null || relayRegions is null)
                    return false;
                organizationId = parsedOrganizationId;
            }

            claims = new SignalingAttestationClaimsV1(
                issuer, audience, alias, deviceId, installationId, spki, keyId,
                issuedAt.Value, expiresAt.Value, tokenId, organizationId,
                policyFlags ?? SignalingOrganizationPolicyFlags.Unmanaged, minimumVersion, relayRegions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ClaimsAreStructurallyValid(SignalingAttestationClaimsV1 claims)
    {
        if (!Uri.TryCreate(claims.Issuer, UriKind.Absolute, out var issuer)
            || issuer.Scheme != Uri.UriSchemeHttps || claims.Issuer.Length > 256
            || !string.IsNullOrEmpty(issuer.UserInfo) || !string.IsNullOrEmpty(issuer.Fragment)
            || !IsBoundedAsciiIdentifier(claims.Audience, 128)
            || !IsPublicDeviceId(claims.PublicDeviceId)
            || claims.DeviceId == Guid.Empty || claims.InstallationId == Guid.Empty
            || !IsLowerHexDigest(claims.SpkiSha256) || !IsLowerHexDigest(claims.KeyId)
            || claims.IssuedAtUnixSeconds < 0 || claims.ExpiresAtUnixSeconds <= claims.IssuedAtUnixSeconds
            || !TryBase64UrlDecode(claims.TokenId.AsSpan(), 32, out var tokenIdBytes)
            || tokenIdBytes.Length is < 16 or > 32
            || (claims.OrganizationId is null && (claims.OrganizationPolicyFlags != SignalingOrganizationPolicyFlags.Unmanaged
                || claims.MinimumClientVersion is not null || claims.ApprovedRelayRegionsCsv is not null))
            || (claims.OrganizationId is not null && (claims.OrganizationId == Guid.Empty
                || claims.OrganizationPolicyFlags < 0
                || (claims.OrganizationPolicyFlags & ~SignalingOrganizationPolicyFlags.Known) != 0
                || !IsBoundedOptionalAscii(claims.MinimumClientVersion, 64, allowComma: false)
                || !IsBoundedOptionalAscii(claims.ApprovedRelayRegionsCsv, 512, allowComma: true))))
        {
            return false;
        }
        return true;
    }

    private static bool IsBoundedAsciiIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '/' or '-');

    private static bool IsBoundedOptionalAscii(string? value, int maximumLength, bool allowComma) =>
        value is not null && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' || (allowComma && character == ','));

    private static bool IsPublicDeviceId(string value) =>
        value.Length == 15
        && value.Select((character, index) => index is 3 or 7 or 11 ? character == '-' : char.IsAsciiDigit(character)).All(valid => valid);

    private static bool IsLowerHexDigest(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedTimeHexEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryBase64UrlDecode(ReadOnlySpan<char> value, int maximumBytes, out byte[] decoded)
    {
        decoded = [];
        if (value.IsEmpty || value.Length > ((maximumBytes + 2) / 3 * 4)
            || value.Contains('=')
            || value.ToArray().Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        try
        {
            var normalized = value.ToString().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            decoded = Convert.FromBase64String(normalized);
            return decoded.Length <= maximumBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
