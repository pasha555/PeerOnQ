using System.Security.Cryptography;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Identity;
using PeerOnQ.Domain.Identity;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace PeerOnQ.Application.Security;

public sealed record HybridIdentityPublic
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public required byte[] LegacyP256PublicKeySpki { get; init; }
    public required byte[] Ed25519PublicKey { get; init; }
    public required byte[] MLDsa65PublicKey { get; init; }
    public required byte[] LegacyBindingSignature { get; init; }

    public string Fingerprint => Convert.ToHexString(
        SHA256.HashData(HybridIdentityEncoding.EncodeUnsigned(this))).ToLowerInvariant();
}

public sealed record HybridIdentitySignature(
    byte[] Ed25519Signature,
    byte[] MLDsa65Signature);

public interface IHybridDeviceIdentityProvider
{
    Task<HybridIdentityPublic> GetPublicIdentityAsync(CancellationToken cancellationToken = default);

    Task<HybridIdentitySignature> SignTranscriptAsync(
        ReadOnlyMemory<byte> transcriptHash,
        ReadOnlyMemory<byte> context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the long-term hybrid device identity. Private key encodings only enter the process while
/// signing and are stored through the existing OS-protected device-secret boundary.
/// </summary>
public sealed class HybridDeviceIdentityService(
    IDeviceSecretStore secretStore,
    DeviceIdentity legacyIdentity) : IHybridDeviceIdentityProvider, IDisposable
{
    public const string Ed25519KeyName = "device-ed25519-signing-key-v1";
    public const string MLDsa65KeyName = "device-mldsa65-signing-key-v1";

    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private HybridIdentityPublic? _publicIdentity;
    private int _disposed;

    public async Task<HybridIdentityPublic> GetPublicIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_publicIdentity is { } cached) return cached;
        EnsurePostQuantumSupport();

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_publicIdentity is { } initialized) return initialized;

            var legacyPublicKey = DecodeLegacyPublicKey();
            var edPrivate = await GetOrCreateEd25519PrivateKeyAsync(cancellationToken);
            var mlPrivate = await GetOrCreateMLDsa65PrivateKeyAsync(cancellationToken);
            try
            {
                var edPublic = new Ed25519PrivateKeyParameters(edPrivate).GeneratePublicKey().GetEncoded();
                var mlPublic = PostQuantumCryptography.ExportMlDsa65PublicKey(mlPrivate);

                var unsigned = new HybridIdentityPublic
                {
                    LegacyP256PublicKeySpki = legacyPublicKey,
                    Ed25519PublicKey = edPublic,
                    MLDsa65PublicKey = mlPublic,
                    LegacyBindingSignature = [],
                };
                var binding = HybridIdentityEncoding.EncodeUnsigned(unsigned);
                var bindingSignature = await SignLegacyBindingAsync(binding, cancellationToken);
                _publicIdentity = unsigned with { LegacyBindingSignature = bindingSignature };
                return _publicIdentity;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(edPrivate);
                CryptographicOperations.ZeroMemory(mlPrivate);
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<HybridIdentitySignature> SignTranscriptAsync(
        ReadOnlyMemory<byte> transcriptHash,
        ReadOnlyMemory<byte> context,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EnsurePostQuantumSupport();
        if (transcriptHash.IsEmpty) throw new ArgumentException("A transcript hash is required.", nameof(transcriptHash));
        if (context.Length is 0 or > 255) throw new ArgumentOutOfRangeException(nameof(context));

        var edPrivate = await secretStore.TryGetAsync(Ed25519KeyName, cancellationToken)
                        ?? throw new CryptographicException("The Ed25519 device identity key is missing.");
        var mlPrivate = await secretStore.TryGetAsync(MLDsa65KeyName, cancellationToken)
                        ?? throw new CryptographicException("The ML-DSA-65 device identity key is missing.");
        try
        {
            var signedInput = HybridIdentityEncoding.EncodeSignatureInput(context.Span, transcriptHash.Span);
            try
            {
                var edSigner = new Ed25519Signer();
                edSigner.Init(true, new Ed25519PrivateKeyParameters(edPrivate));
                edSigner.BlockUpdate(signedInput, 0, signedInput.Length);
                var edSignature = edSigner.GenerateSignature();

                var mlSignature = PostQuantumCryptography.SignMlDsa65(
                    mlPrivate,
                    transcriptHash.Span,
                    context.Span);
                return new HybridIdentitySignature(edSignature, mlSignature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signedInput);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(edPrivate);
            CryptographicOperations.ZeroMemory(mlPrivate);
        }
    }

    private byte[] DecodeLegacyPublicKey()
    {
        if (string.IsNullOrWhiteSpace(legacyIdentity.PublicKey))
            throw new CryptographicException("The legacy device identity public key is missing.");
        try
        {
            return Convert.FromBase64String(legacyIdentity.PublicKey);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("The legacy device identity public key is malformed.", exception);
        }
    }

    private async Task<byte[]> GetOrCreateEd25519PrivateKeyAsync(CancellationToken cancellationToken)
    {
        var key = await secretStore.TryGetAsync(Ed25519KeyName, cancellationToken);
        if (key is not null)
        {
            if (key.Length != Ed25519PrivateKeyParameters.KeySize)
                throw new CryptographicException("The protected Ed25519 device identity key is malformed.");
            return key;
        }

        key = RandomNumberGenerator.GetBytes(Ed25519PrivateKeyParameters.KeySize);
        await secretStore.SetAsync(Ed25519KeyName, key, cancellationToken);
        return key;
    }

    private static void EnsurePostQuantumSupport()
    {
        if (!PostQuantumCryptography.IsSupported)
            throw new PlatformNotSupportedException(
                "Mandatory ML-DSA-65 support is unavailable; secure PeerOnQ sessions are disabled.");
    }

    private async Task<byte[]> GetOrCreateMLDsa65PrivateKeyAsync(CancellationToken cancellationToken)
    {
        var key = await secretStore.TryGetAsync(MLDsa65KeyName, cancellationToken);
        if (key is not null) return key;

        key = PostQuantumCryptography.GenerateMlDsa65PrivateKey();
        await secretStore.SetAsync(MLDsa65KeyName, key, cancellationToken);
        return key;
    }

    private async Task<byte[]> SignLegacyBindingAsync(
        byte[] binding,
        CancellationToken cancellationToken)
    {
        var key = await secretStore.TryGetAsync(DeviceProvisioningService.SigningKeyName, cancellationToken)
                  ?? throw new CryptographicException("The legacy device signing key is missing.");
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(key, out var bytesRead);
            if (bytesRead != key.Length)
                throw new CryptographicException("The legacy device signing key has trailing data.");
            return ecdsa.SignData(binding, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _initializationGate.Dispose();
    }
}

public static class HybridIdentityVerifier
{
    public static string VerifyAndFingerprint(
        HybridIdentityPublic identity,
        string expectedLegacyP256Fingerprint,
        ReadOnlySpan<byte> transcriptHash,
        ReadOnlySpan<byte> context,
        HybridIdentitySignature signature)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(signature);
        if (!PostQuantumCryptography.IsSupported)
            throw new PlatformNotSupportedException("Mandatory ML-DSA-65 support is unavailable.");
        var fingerprint = VerifyBinding(identity, expectedLegacyP256Fingerprint);

        var signedInput = HybridIdentityEncoding.EncodeSignatureInput(context, transcriptHash);
        try
        {
            var edVerifier = new Ed25519Signer();
            edVerifier.Init(false, new Ed25519PublicKeyParameters(identity.Ed25519PublicKey));
            edVerifier.BlockUpdate(signedInput, 0, signedInput.Length);
            if (!edVerifier.VerifySignature(signature.Ed25519Signature))
                throw new CryptographicException("The peer Ed25519 transcript signature is invalid.");

            if (!PostQuantumCryptography.VerifyMlDsa65(
                    identity.MLDsa65PublicKey,
                    transcriptHash,
                    signature.MLDsa65Signature,
                    context))
                throw new CryptographicException("The peer ML-DSA-65 transcript signature is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedInput);
        }

        return fingerprint;
    }

    public static string VerifyBinding(
        HybridIdentityPublic identity,
        string expectedLegacyP256Fingerprint)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!IsSha256Fingerprint(expectedLegacyP256Fingerprint))
            throw new CryptographicException("The expected peer identity fingerprint is invalid.");
        if (identity.Version != HybridIdentityPublic.CurrentVersion
            || identity.Ed25519PublicKey.Length != Ed25519PublicKeyParameters.KeySize
            || identity.MLDsa65PublicKey.Length != PostQuantumCryptography.MlDsa65PublicKeyBytes
            || identity.LegacyP256PublicKeySpki.Length is 0 or > 4096
            || identity.LegacyBindingSignature.Length is 0 or > 256)
        {
            throw new CryptographicException("The peer hybrid identity is malformed or unsupported.");
        }

        var actualLegacyFingerprint = Convert.ToHexString(
            SHA256.HashData(identity.LegacyP256PublicKeySpki)).ToLowerInvariant();
        var expected = Convert.FromHexString(expectedLegacyP256Fingerprint);
        var actual = Convert.FromHexString(actualLegacyFingerprint);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new CryptographicException("The peer device identity does not match signaling.");

        using (var legacy = ECDsa.Create())
        {
            legacy.ImportSubjectPublicKeyInfo(identity.LegacyP256PublicKeySpki, out var bytesRead);
            if (bytesRead != identity.LegacyP256PublicKeySpki.Length
                || !legacy.VerifyData(
                    HybridIdentityEncoding.EncodeUnsigned(identity),
                    identity.LegacyBindingSignature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new CryptographicException("The peer hybrid identity binding is invalid.");
            }
        }
        return identity.Fingerprint;
    }

    private static bool IsSha256Fingerprint(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}

internal static class HybridIdentityEncoding
{
    private static readonly byte[] BindingDomain = "PeerOnQ Hybrid Device Identity v1"u8.ToArray();

    public static byte[] EncodeUnsigned(HybridIdentityPublic identity)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteBytes(writer, BindingDomain);
        writer.Write(identity.Version);
        WriteBytes(writer, identity.LegacyP256PublicKeySpki);
        WriteBytes(writer, identity.Ed25519PublicKey);
        WriteBytes(writer, identity.MLDsa65PublicKey);
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] EncodeSignatureInput(ReadOnlySpan<byte> context, ReadOnlySpan<byte> transcriptHash)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteBytes(writer, context);
        WriteBytes(writer, transcriptHash);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }
}
