using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.App.Android;

/// <summary>
/// Encrypts application secrets with a non-exportable AES-GCM key held by Android Keystore.
/// Only versioned ciphertext and the random nonce are persisted in private app preferences.
/// </summary>
public sealed partial class AndroidKeyStoreDeviceSecretStore : IDeviceSecretStore
{
    private const string KeyAlias = "peeronq.device-secrets.aes-gcm.v1";
    private const string KeyStoreProvider = "AndroidKeyStore";
    private const string CipherTransformation = "AES/GCM/NoPadding";
    private const int MaximumSecretBytes = 64 * 1024;
    private const byte BlobVersion = 1;

    private readonly ISharedPreferences _preferences;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AndroidKeyStoreDeviceSecretStore(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _preferences = context.GetSharedPreferences("peeronq-protected-secrets-v1", FileCreationMode.Private)
                       ?? throw new InvalidOperationException("Android private preferences are unavailable.");
    }

    public async Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var encoded = _preferences.GetString(StorageKey(name), null);
            if (encoded is null) return null;

            byte[] blob;
            try
            {
                blob = Convert.FromBase64String(encoded);
            }
            catch (FormatException exception)
            {
                throw new CryptographicException("Protected Android secret storage is malformed.", exception);
            }

            try
            {
                if (blob.Length < 1 + 12 + 16 || blob[0] != BlobVersion)
                    throw new CryptographicException("Protected Android secret storage has an unsupported format.");

                var nonce = blob.AsSpan(1, 12).ToArray();
                var ciphertext = blob.AsSpan(13).ToArray();
                try
                {
                    using var cipher = Cipher.GetInstance(CipherTransformation)
                                       ?? throw new CryptographicException("Android AES-GCM is unavailable.");
                    using var key = GetOrCreateKey();
                    cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, new GCMParameterSpec(128, nonce));
                    cipher.UpdateAAD(CreateAssociatedData(name));
                    return cipher.DoFinal(ciphertext)
                           ?? throw new CryptographicException("Android Keystore returned no plaintext.");
                }
                catch (Java.Lang.Exception exception)
                {
                    throw new CryptographicException("Android Keystore could not decrypt the protected secret.", exception);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);
                    CryptographicOperations.ZeroMemory(ciphertext);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string name, byte[] secret, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length is 0 or > MaximumSecretBytes)
            throw new ArgumentOutOfRangeException(nameof(secret), $"Secrets must be 1-{MaximumSecretBytes} bytes.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? nonce = null;
            byte[]? ciphertext = null;
            byte[]? blob = null;
            try
            {
                using var cipher = Cipher.GetInstance(CipherTransformation)
                                   ?? throw new CryptographicException("Android AES-GCM is unavailable.");
                using var key = GetOrCreateKey();
                cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
                cipher.UpdateAAD(CreateAssociatedData(name));
                ciphertext = cipher.DoFinal(secret)
                             ?? throw new CryptographicException("Android Keystore returned no ciphertext.");
                nonce = cipher.GetIV() ?? throw new CryptographicException("Android Keystore returned no AES-GCM nonce.");
                if (nonce.Length != 12)
                    throw new CryptographicException("Android Keystore returned an invalid AES-GCM nonce.");

                blob = new byte[1 + nonce.Length + ciphertext.Length];
                blob[0] = BlobVersion;
                nonce.CopyTo(blob, 1);
                ciphertext.CopyTo(blob, 1 + nonce.Length);
                using var editor = _preferences.Edit()
                                   ?? throw new IOException("Android private preferences are unavailable.");
                editor.PutString(StorageKey(name), Convert.ToBase64String(blob));
                if (!editor.Commit())
                    throw new IOException("Android could not durably persist the protected secret.");
            }
            catch (Java.Lang.Exception exception)
            {
                throw new CryptographicException("Android Keystore could not protect the secret.", exception);
            }
            finally
            {
                if (nonce is not null) CryptographicOperations.ZeroMemory(nonce);
                if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
                if (blob is not null) CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var editor = _preferences.Edit()
                               ?? throw new IOException("Android private preferences are unavailable.");
            editor.Remove(StorageKey(name));
            if (!editor.Commit())
                throw new IOException("Android could not remove the protected secret.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Java.Security.IKey GetOrCreateKey()
    {
        using var keyStore = KeyStore.GetInstance(KeyStoreProvider)
                             ?? throw new CryptographicException("Android Keystore is unavailable.");
        keyStore.Load(null);
        if (!keyStore.ContainsAlias(KeyAlias))
        {
            using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreProvider)
                                  ?? throw new CryptographicException("Android AES key generation is unavailable.");
            using var specification = new KeyGenParameterSpec.Builder(
                    KeyAlias,
                    KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetRandomizedEncryptionRequired(true)
                .Build();
            generator.Init(specification);
            using var generated = generator.GenerateKey();
        }

        return keyStore.GetKey(KeyAlias, null)
               ?? throw new CryptographicException("Android Keystore did not return the device key.");
    }

    private static byte[] CreateAssociatedData(string name) =>
        Encoding.UTF8.GetBytes($"PeerOnQ|Android|device-secret|v1|{name}");

    private static string StorageKey(string name) => $"secret.{name}";

    private static void ValidateName(string name)
    {
        if (!SecretNamePattern().IsMatch(name))
            throw new ArgumentException("Secret names must be 1-64 lowercase letters, digits or hyphens.", nameof(name));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNamePattern();
}
