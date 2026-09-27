using System.Security.Cryptography;
using System.Text;
using PeerOnQ.Cloud.Application.Abstractions;

namespace PeerOnQ.Cloud.Application.Security;

public sealed class EcdsaDeviceProofVerifier : IDeviceProofVerifier
{
    private const int MaximumEncodedKeyBytes = 512;
    private const int MaximumEncodedSignatureBytes = 256;

    public string? GetFingerprint(string publicKeySpkiBase64)
    {
        try
        {
            var publicKey = Convert.FromBase64String(publicKeySpkiBase64);
            if (publicKey.Length is 0 or > MaximumEncodedKeyBytes) return null;

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            return bytesRead == publicKey.Length && ecdsa.KeySize == 256
                ? Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant()
                : null;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    public DeviceProofResult Verify(string publicKeySpkiBase64, string canonicalPayload, string signatureBase64)
    {
        if (string.IsNullOrEmpty(canonicalPayload) || canonicalPayload.Length > 4096)
            return new DeviceProofResult(false, null);

        try
        {
            var publicKey = Convert.FromBase64String(publicKeySpkiBase64);
            var signature = Convert.FromBase64String(signatureBase64);
            if (publicKey.Length is 0 or > MaximumEncodedKeyBytes || signature.Length is 0 or > MaximumEncodedSignatureBytes)
                return new DeviceProofResult(false, null);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            if (bytesRead != publicKey.Length || ecdsa.KeySize != 256)
                return new DeviceProofResult(false, null);

            var valid = ecdsa.VerifyData(Encoding.UTF8.GetBytes(canonicalPayload), signature, HashAlgorithmName.SHA256);
            var fingerprint = GetFingerprint(publicKeySpkiBase64);
            return new DeviceProofResult(valid, valid ? fingerprint : null);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return new DeviceProofResult(false, null);
        }
    }
}
