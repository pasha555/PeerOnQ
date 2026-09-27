using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerOnQ.Infrastructure.Updates;

public sealed class UpdateManifestVerifier(TimeProvider? timeProvider = null)
{
    private const int CurrentEnvelopeSchema = 1;
    private const int CurrentManifestSchema = 1;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
    };

    public VerifiedUpdate Verify(ReadOnlySpan<byte> envelopeBytes, UpdateClientOptions options)
    {
        ValidateOptions(options);
        if (envelopeBytes.IsEmpty || envelopeBytes.Length > options.MaximumManifestBytes)
            Reject(UpdateRejectionReason.InvalidEnvelope, "The signed update envelope has an invalid size.");

        SignedUpdateEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(envelopeBytes, JsonOptions)
                       ?? throw new JsonException("Empty update envelope.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new UpdateSecurityException(UpdateRejectionReason.InvalidEnvelope, "The signed update envelope is malformed.");
        }

        if (envelope.SchemaVersion != CurrentEnvelopeSchema
            || !FixedEquals(envelope.KeyId, options.KeyId)
            || string.IsNullOrWhiteSpace(envelope.Payload)
            || string.IsNullOrWhiteSpace(envelope.Signature))
        {
            Reject(UpdateRejectionReason.UntrustedKey, "The update envelope uses an unsupported schema or signing key.");
        }

        byte[] payload;
        byte[] signature;
        byte[] publicKey;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
            publicKey = Convert.FromBase64String(options.PublicKeySpkiBase64);
        }
        catch (FormatException)
        {
            throw new UpdateSecurityException(UpdateRejectionReason.InvalidEnvelope, "The update envelope contains invalid base64 data.");
        }

        if (payload.Length is 0 or > 64 * 1024 || signature.Length is 0 or > 256 || publicKey.Length is 0 or > 1024)
            Reject(UpdateRejectionReason.InvalidEnvelope, "The update envelope contains an invalid field size.");

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length
                || !ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                Reject(UpdateRejectionReason.InvalidSignature, "The update manifest signature is invalid.");
            }
        }
        catch (CryptographicException)
        {
            throw new UpdateSecurityException(UpdateRejectionReason.UntrustedKey, "The configured update trust key is invalid.");
        }

        UpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(payload, JsonOptions)
                       ?? throw new JsonException("Empty update manifest.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new UpdateSecurityException(UpdateRejectionReason.InvalidEnvelope, "The signed update manifest is malformed.");
        }

        ValidateManifest(manifest, options);

        if (!Version.TryParse(manifest.Version, out var candidate))
            Reject(UpdateRejectionReason.InvalidVersion, "The update manifest contains an invalid version.");
        if (!Version.TryParse(manifest.MinimumSupportedVersion, out var minimumSupported))
            Reject(UpdateRejectionReason.InvalidVersion, "The update manifest contains an invalid security-floor version.");

        if (candidate < minimumSupported)
            Reject(UpdateRejectionReason.BelowSecurityFloor, "The candidate version is below the manifest security floor.");
        if (candidate < options.CurrentVersion)
            Reject(UpdateRejectionReason.UnauthorizedDowngrade, "The update candidate is older than the installed version.");

        var package = manifest.Packages.SingleOrDefault(item =>
            string.Equals(item.Architecture, options.Architecture, StringComparison.Ordinal));
        if (package is null)
            Reject(UpdateRejectionReason.WrongArchitecture, "The update has no package for this process architecture.");

        ValidatePackage(package, options);
        return new VerifiedUpdate(manifest, package, candidate, minimumSupported);
    }

    public UpdateCheckStatus Classify(VerifiedUpdate update, UpdateClientOptions options)
    {
        if (update.Version == options.CurrentVersion)
            return UpdateCheckStatus.NoUpdate;

        if (options.CurrentVersion < update.MinimumSupportedVersion || update.Manifest.SecurityEmergency)
            return UpdateCheckStatus.Required;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{options.DeviceRolloutId}:{update.Manifest.RolloutSeed}"));
        var bucket = ((digest[0] << 8) | digest[1]) * 100 / 65536;
        return bucket < update.Manifest.RolloutPercentage
            ? UpdateCheckStatus.Available
            : UpdateCheckStatus.DeferredByRollout;
    }

    private void ValidateManifest(UpdateManifest manifest, UpdateClientOptions options)
    {
        if (manifest.SchemaVersion != CurrentManifestSchema)
            Reject(UpdateRejectionReason.InvalidEnvelope, "The update manifest schema is unsupported.");
        if (!FixedEquals(manifest.ProductId, options.ProductId)
            && !(FixedEquals(options.ProductId, UpdateClientOptions.DefaultProductId)
                 && FixedEquals(manifest.ProductId, UpdateClientOptions.LegacyProductId)))
            Reject(UpdateRejectionReason.WrongProduct, "The update manifest belongs to another product.");
        if (manifest.Channel != options.Channel)
            Reject(UpdateRejectionReason.WrongChannel, "The update manifest belongs to another release channel.");
        if (manifest.RolloutPercentage is < 0 or > 100
            || string.IsNullOrWhiteSpace(manifest.RolloutSeed)
            || manifest.RolloutSeed.Length > 128
            || manifest.Packages is null
            || manifest.Packages.Count is 0 or > 8)
        {
            Reject(UpdateRejectionReason.InvalidEnvelope, "The update rollout or package list is invalid.");
        }

        var now = _time.GetUtcNow();
        if (manifest.IssuedAt > now.AddMinutes(5))
            Reject(UpdateRejectionReason.ManifestNotYetValid, "The update manifest is not valid yet.");
        if (manifest.ExpiresAt <= now || manifest.ExpiresAt <= manifest.IssuedAt)
            Reject(UpdateRejectionReason.ExpiredManifest, "The update manifest has expired.");
        if (manifest.ExpiresAt - manifest.IssuedAt > TimeSpan.FromDays(30))
            Reject(UpdateRejectionReason.InvalidEnvelope, "The update manifest validity window is too long.");
    }

    private static void ValidatePackage(UpdatePackageDescriptor package, UpdateClientOptions options)
    {
        if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var packageUri))
            Reject(UpdateRejectionReason.InvalidPackage, "The update package URL is invalid.");

        if (!string.Equals(package.InstallerType, "msi", StringComparison.Ordinal)
            || package.SizeBytes <= 0
            || package.SizeBytes > options.MaximumPackageBytes
            || package.Sha256.Length != 64
            || package.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            Reject(UpdateRejectionReason.InvalidPackage, "The update package descriptor is invalid.");
        }

        if (packageUri.Scheme != Uri.UriSchemeHttps)
            Reject(UpdateRejectionReason.InsecureTransport, "Update packages must use HTTPS.");
        if (!string.Equals(packageUri.Host, options.ManifestUri.Host, StringComparison.OrdinalIgnoreCase))
            Reject(UpdateRejectionReason.UntrustedDownloadHost, "The update package host does not match the trusted manifest host.");
    }

    private static void ValidateOptions(UpdateClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ManifestUri.Scheme != Uri.UriSchemeHttps)
            Reject(UpdateRejectionReason.InsecureTransport, "The update manifest must use HTTPS.");
        if (string.IsNullOrWhiteSpace(options.PublicKeySpkiBase64)
            || string.IsNullOrWhiteSpace(options.KeyId)
            || options.KeyId.Length > 128
            || string.IsNullOrWhiteSpace(options.DeviceRolloutId)
            || options.AllowedPublisherCertificateSha256.Count == 0)
        {
            Reject(UpdateRejectionReason.UntrustedKey, "The update trust configuration is incomplete.");
        }
    }

    private static bool FixedEquals(string? left, string? right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    [DoesNotReturn]
    private static void Reject(UpdateRejectionReason reason, string message) =>
        throw new UpdateSecurityException(reason, message);
}
