using System.Security.Cryptography;
using PeerOnQ.Domain.Identity;
using Microsoft.Extensions.Options;
using PeerOnQ.Shared.Contracts.Security;

namespace PeerOnQ.Signaling.Server.Security;

/// <summary>
/// Validates the short-lived cloud signature that binds a server-assigned routing alias to the
/// exact ECDSA key used for the fresh signaling challenge. Public verification keys may overlap
/// during rotation; no cloud private key is ever present in the signaling service.
/// </summary>
public sealed class CloudSignalingAttestationValidator
{
    private const long MaximumPublicKeyFileBytes = 16 * 1024;

    private readonly SignalingAttestationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyDictionary<string, byte[]> _publicKeysById;

    public CloudSignalingAttestationValidator(
        IOptions<SignalingOptions> options,
        TimeProvider timeProvider)
    {
        _options = options.Value.Attestation;
        _timeProvider = timeProvider;
        _publicKeysById = _options.Required
            ? LoadPublicKeys(_options.PublicKeyFiles)
            : new Dictionary<string, byte[]>(StringComparer.Ordinal);
    }

    public bool TryValidate(
        string? token,
        PeerOnQId expectedAlias,
        string publicKeySpkiBase64,
        out SignalingAttestationValidationError error)
        => TryValidate(token, expectedAlias, publicKeySpkiBase64, out _, out error);

    public bool TryValidate(
        string? token,
        PeerOnQId expectedAlias,
        string publicKeySpkiBase64,
        out SignalingAttestationClaimsV1? validatedClaims,
        out SignalingAttestationValidationError error)
    {
        validatedClaims = null;
        error = SignalingAttestationValidationError.Malformed;
        if (!_options.Required)
            return false;

        if (!SignalingAttestationTokenV1.TryValidate(
                token,
                _publicKeysById,
                _options.Issuer,
                _options.Audience,
                _timeProvider.GetUtcNow(),
                _options.ClockSkew,
                _options.MaximumTokenLifetime,
                out var claims,
                out error)
            || claims is null)
        {
            return false;
        }

        if (!string.Equals(claims.PublicDeviceId, expectedAlias.Display, StringComparison.Ordinal)
            || !TryComputeSpkiFingerprint(publicKeySpkiBase64, out var fingerprint)
            || !FixedTimeHexEquals(claims.SpkiSha256, fingerprint))
        {
            error = SignalingAttestationValidationError.InvalidClaims;
            return false;
        }

        validatedClaims = claims;
        error = SignalingAttestationValidationError.None;
        return true;
    }

    public static bool CanLoadPublicKeys(IReadOnlyList<string> publicKeyFiles)
    {
        try
        {
            _ = LoadPublicKeys(publicKeyFiles);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or CryptographicException
                                   or ArgumentException
                                   or InvalidOperationException
                                   or NotSupportedException)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, byte[]> LoadPublicKeys(IReadOnlyList<string> publicKeyFiles)
    {
        if (publicKeyFiles.Count is < 1 or > 3)
            throw new InvalidOperationException("One to three signaling attestation public keys are required.");

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in publicKeyFiles)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumPublicKeyFileBytes)
                throw new InvalidOperationException("A signaling attestation public key file is missing or invalid.");

            var pem = File.ReadAllText(path).Trim();
            const string beginBoundary = "-----BEGIN PUBLIC KEY-----";
            const string endBoundary = "-----END PUBLIC KEY-----";
            if (!pem.StartsWith(beginBoundary, StringComparison.Ordinal)
                || !pem.EndsWith(endBoundary, StringComparison.Ordinal)
                || pem.IndexOf("-----BEGIN ", beginBoundary.Length, StringComparison.Ordinal) >= 0)
            {
                throw new CryptographicException(
                    "The signaling attestation verifier requires exactly one SPKI public-key PEM block.");
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            if (ecdsa.KeySize != 256)
                throw new CryptographicException("The signaling attestation public key must use P-256.");

            var spki = ecdsa.ExportSubjectPublicKeyInfo();
            var keyId = SignalingAttestationTokenV1.ComputeKeyId(spki);
            if (!keys.TryAdd(keyId, spki))
                throw new InvalidOperationException("Duplicate signaling attestation public keys are configured.");
        }

        return keys;
    }

    private static bool TryComputeSpkiFingerprint(string value, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            return false;

        try
        {
            var spki = Convert.FromBase64String(value);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var bytesRead);
            if (bytesRead != spki.Length || key.KeySize != 256)
                return false;

            fingerprint = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool FixedTimeHexEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
