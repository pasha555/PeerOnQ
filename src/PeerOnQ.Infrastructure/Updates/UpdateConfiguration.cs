using System.Reflection;
using System.Security.Cryptography;

namespace PeerOnQ.Infrastructure.Updates;

public static class UpdateConfiguration
{
    public static UpdateClientOptions? TryCreate(
        Assembly applicationAssembly,
        string updateDirectory,
        string deviceRolloutId,
        bool allowDevelopmentEnvironmentOverrides)
    {
        ArgumentNullException.ThrowIfNull(applicationAssembly);
        var metadata = applicationAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(item => item.Key, item => item.Value ?? string.Empty, StringComparer.Ordinal);

        string Read(string metadataKey, string environmentKey)
        {
            if (metadata.TryGetValue(metadataKey, out var compiled) && !string.IsNullOrWhiteSpace(compiled))
                return compiled;
            if (allowDevelopmentEnvironmentOverrides
                && Environment.GetEnvironmentVariable(environmentKey) is { Length: > 0 } configured)
                return configured;
            return string.Empty;
        }

        var manifestUrl = Read("PeerOnQUpdateManifestUrl", "PEERONQ_UPDATE_MANIFEST_URL");
        var publicKey = Read("PeerOnQUpdatePublicKeySpki", "PEERONQ_UPDATE_PUBLIC_KEY_SPKI");
        var keyId = Read("PeerOnQUpdateKeyId", "PEERONQ_UPDATE_KEY_ID");
        var publishers = Read("PeerOnQPublisherCertificateSha256", "PEERONQ_PUBLISHER_CERTIFICATE_SHA256");
        var channelText = Read("PeerOnQReleaseChannel", "PEERONQ_UPDATE_CHANNEL");

        var supplied = new[] { manifestUrl, publicKey, keyId, publishers }.Count(value => !string.IsNullOrWhiteSpace(value));
        if (supplied == 0) return null;
        if (supplied != 4)
            throw new InvalidOperationException("The update trust configuration is incomplete; automatic updates are disabled fail-closed.");
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri)
            || manifestUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(manifestUri.UserInfo)
            || !string.IsNullOrEmpty(manifestUri.Query)
            || !string.IsNullOrEmpty(manifestUri.Fragment))
        {
            throw new InvalidOperationException("The update manifest URL must be an absolute HTTPS URL without credentials, a query string, or a fragment.");
        }
        if (keyId.Length > 128)
            throw new InvalidOperationException("The update signing key ID must contain at most 128 characters.");

        try
        {
            var encodedKey = Convert.FromBase64String(publicKey);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(encodedKey, out var bytesRead);
            if (bytesRead != encodedKey.Length || verifier.KeySize != 256)
                throw new CryptographicException("The key is not an ECDSA P-256 SPKI value.");
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("The update manifest public key must be a valid ECDSA P-256 SPKI value.", exception);
        }

        var publisherSet = publishers
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeFingerprint)
            .ToHashSet(StringComparer.Ordinal);
        if (publisherSet.Count == 0 || publisherSet.Any(item => item.Length != 64))
            throw new InvalidOperationException("Every configured publisher certificate fingerprint must be SHA-256.");

        var channel = string.Equals(channelText, "stable", StringComparison.OrdinalIgnoreCase)
            ? UpdateChannel.Stable
            : UpdateChannel.Beta;

        return new UpdateClientOptions
        {
            ManifestUri = manifestUri,
            PublicKeySpkiBase64 = publicKey,
            KeyId = keyId,
            AllowedPublisherCertificateSha256 = publisherSet,
            UpdateDirectory = updateDirectory,
            DeviceRolloutId = deviceRolloutId,
            CurrentVersion = applicationAssembly.GetName().Version ?? new Version(0, 0, 0, 0),
            Channel = channel,
        };
    }

    private static string NormalizeFingerprint(string value) =>
        new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
}
