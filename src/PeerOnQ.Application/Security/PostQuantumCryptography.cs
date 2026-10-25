using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;

namespace PeerOnQ.Application.Security;

public interface IMlKem768PrivateKey : IDisposable
{
    byte[] ExportEncapsulationKey();
    byte[] Decapsulate(ReadOnlySpan<byte> ciphertext);
}

public sealed record MlKem768Encapsulation(byte[] Ciphertext, byte[] SharedSecret);

/// <summary>
/// Managed FIPS 203/204 primitives backed by the existing Bouncy Castle package. Keeping the
/// provider here makes secure sessions independent of optional Windows Insider CNG algorithms
/// without changing the protocol suites or allowing a classical-only downgrade.
/// </summary>
public static class PostQuantumCryptography
{
    public const int MlKem768EncapsulationKeyBytes = 1184;
    public const int MlKem768CiphertextBytes = 1088;
    public const int MlKem768SharedSecretBytes = 32;
    public const int MlDsa65PublicKeyBytes = 1952;
    public const int MlDsa65SignatureBytes = 3309;

    public static bool IsSupported => true;

    public static byte[] GenerateMlDsa65PrivateKey()
    {
        try
        {
            var generator = new MLDsaKeyPairGenerator();
            generator.Init(new MLDsaKeyGenerationParameters(
                new SecureRandom(),
                MLDsaParameters.ml_dsa_65));
            var privateKey = (MLDsaPrivateKeyParameters)generator.GenerateKeyPair().Private;
            return PrivateKeyInfoFactory.CreatePrivateKeyInfo(privateKey).GetEncoded();
        }
        catch (Exception exception) when (exception is not CryptographicException)
        {
            throw new CryptographicException("ML-DSA-65 key generation failed.", exception);
        }
    }

    public static byte[] ExportMlDsa65PublicKey(ReadOnlySpan<byte> privateKeyPkcs8) =>
        ImportMlDsa65PrivateKey(privateKeyPkcs8).GetPublicKeyEncoded();

    public static byte[] SignMlDsa65(
        ReadOnlySpan<byte> privateKeyPkcs8,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> context)
    {
        ValidateContext(context);
        var privateKey = ImportMlDsa65PrivateKey(privateKeyPkcs8);
        try
        {
            var signer = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: false);
            signer.Init(
                forSigning: true,
                new ParametersWithContext(
                    new ParametersWithRandom(privateKey, new SecureRandom()),
                    context));
            signer.BlockUpdate(data);
            var signature = signer.GenerateSignature();
            if (signature.Length != MlDsa65SignatureBytes)
            {
                CryptographicOperations.ZeroMemory(signature);
                throw new CryptographicException("ML-DSA-65 produced an invalid signature length.");
            }
            return signature;
        }
        catch (Exception exception) when (exception is not CryptographicException)
        {
            throw new CryptographicException("ML-DSA-65 signing failed.", exception);
        }
    }

    public static bool VerifyMlDsa65(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> context)
    {
        ValidateContext(context);
        if (publicKey.Length != MlDsa65PublicKeyBytes
            || signature.Length != MlDsa65SignatureBytes)
        {
            return false;
        }

        try
        {
            var verifier = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: false);
            verifier.Init(
                forSigning: false,
                new ParametersWithContext(
                    MLDsaPublicKeyParameters.FromEncoding(
                        MLDsaParameters.ml_dsa_65,
                        publicKey.ToArray()),
                    context));
            verifier.BlockUpdate(data);
            return verifier.VerifySignature(signature.ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public static IMlKem768PrivateKey CreateMlKem768PrivateKey()
    {
        try
        {
            var generator = new MLKemKeyPairGenerator();
            generator.Init(new MLKemKeyGenerationParameters(
                new SecureRandom(),
                MLKemParameters.ml_kem_768));
            var pair = generator.GenerateKeyPair();
            var privateKey = (MLKemPrivateKeyParameters)pair.Private;
            var publicKey = (MLKemPublicKeyParameters)pair.Public;
            return new MlKem768PrivateKey(privateKey.GetEncoded(), publicKey.GetEncoded());
        }
        catch (Exception exception) when (exception is not CryptographicException)
        {
            throw new CryptographicException("ML-KEM-768 key generation failed.", exception);
        }
    }

    public static MlKem768Encapsulation EncapsulateMlKem768(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != MlKem768EncapsulationKeyBytes)
            throw new CryptographicException("The ML-KEM-768 encapsulation key has an invalid length.");

        var ciphertext = new byte[MlKem768CiphertextBytes];
        var sharedSecret = new byte[MlKem768SharedSecretBytes];
        try
        {
            var encapsulator = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
            encapsulator.Init(new ParametersWithRandom(
                MLKemPublicKeyParameters.FromEncoding(
                    MLKemParameters.ml_kem_768,
                    publicKey.ToArray()),
                new SecureRandom()));
            encapsulator.Encapsulate(ciphertext, sharedSecret);
            return new MlKem768Encapsulation(ciphertext, sharedSecret);
        }
        catch (Exception exception)
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            if (exception is CryptographicException) throw;
            throw new CryptographicException("ML-KEM-768 encapsulation failed.", exception);
        }
    }

    private static MLDsaPrivateKeyParameters ImportMlDsa65PrivateKey(
        ReadOnlySpan<byte> privateKeyPkcs8)
    {
        if (privateKeyPkcs8.IsEmpty)
            throw new CryptographicException("The ML-DSA-65 private key is empty.");
        var encodedPrivateKey = privateKeyPkcs8.ToArray();
        try
        {
            if (PrivateKeyFactory.CreateKey(encodedPrivateKey)
                is not MLDsaPrivateKeyParameters privateKey
                || !string.Equals(
                    privateKey.Parameters.Name,
                    MLDsaParameters.ml_dsa_65.Name,
                    StringComparison.Ordinal))
            {
                throw new CryptographicException("The protected post-quantum identity key is not ML-DSA-65.");
            }
            return privateKey;
        }
        catch (Exception exception) when (exception is not CryptographicException)
        {
            throw new CryptographicException("The protected ML-DSA-65 private key is malformed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedPrivateKey);
        }
    }

    private static void ValidateContext(ReadOnlySpan<byte> context)
    {
        if (context.Length is 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(context));
    }

    private sealed class MlKem768PrivateKey : IMlKem768PrivateKey
    {
        private readonly object _gate = new();
        private readonly byte[] _publicKey;
        private byte[]? _privateKey;

        public MlKem768PrivateKey(byte[] privateKey, byte[] publicKey)
        {
            if (privateKey.Length == 0
                || publicKey.Length != MlKem768EncapsulationKeyBytes)
            {
                CryptographicOperations.ZeroMemory(privateKey);
                throw new CryptographicException("The generated ML-KEM-768 key pair is malformed.");
            }
            _privateKey = privateKey;
            _publicKey = publicKey;
        }

        public byte[] ExportEncapsulationKey() => _publicKey.ToArray();

        public byte[] Decapsulate(ReadOnlySpan<byte> ciphertext)
        {
            if (ciphertext.Length != MlKem768CiphertextBytes)
                throw new CryptographicException("The ML-KEM-768 ciphertext has an invalid length.");

            byte[] encodedPrivateKey;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_privateKey is null, this);
                encodedPrivateKey = _privateKey.ToArray();
            }

            var sharedSecret = new byte[MlKem768SharedSecretBytes];
            try
            {
                var decapsulator = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
                decapsulator.Init(MLKemPrivateKeyParameters.FromEncoding(
                    MLKemParameters.ml_kem_768,
                    encodedPrivateKey));
                decapsulator.Decapsulate(ciphertext, sharedSecret);
                return sharedSecret;
            }
            catch (Exception exception)
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
                if (exception is CryptographicException) throw;
                throw new CryptographicException("ML-KEM-768 decapsulation failed.", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encodedPrivateKey);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_privateKey is null) return;
                CryptographicOperations.ZeroMemory(_privateKey);
                _privateKey = null;
            }
        }
    }
}
