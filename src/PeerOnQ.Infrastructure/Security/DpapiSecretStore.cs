using System.Runtime.Versioning;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Infrastructure.Compatibility;

namespace PeerOnQ.Infrastructure.Security;

/// <summary>
/// Device secrets protected by Windows DPAPI under the current user account, with an extra
/// entropy value bound to the secret name. Nothing here is ever written to SQLite or logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string secretsDirectory) : IDeviceSecretStore
{
    private static readonly byte[] EntropyPrefix = "PeerOnQ.DeviceSecret.v1"u8.ToArray();
    private static readonly byte[] LegacyEntropyPrefix =
        System.Text.Encoding.UTF8.GetBytes(LegacyBrandCompatibility.DeviceSecretEntropyV1);

    public async Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(path);
        try
        {
            var plain = ProtectedData.Unprotect(protectedBytes, EntropyFor(name), DataProtectionScope.CurrentUser);
            return plain;
        }
        catch (CryptographicException)
        {
            // A 0.5.0 secret may still use the legacy entropy. Successful reads are immediately
            // rewritten with PeerOnQ entropy; a failed rewrite leaves the legacy file intact so
            // the identity is still available and migration can retry on the next launch.
            try
            {
                var plain = ProtectedData.Unprotect(
                    protectedBytes,
                    EntropyFor(name, LegacyEntropyPrefix),
                    DataProtectionScope.CurrentUser);
                try
                {
                    await SetAsync(name, plain, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    // Keep serving the successfully decrypted legacy secret without deleting it.
                }

                return plain;
            }
            catch (CryptographicException)
            {
                // Written by a different user or machine, or corrupt: unusable, treat as absent.
                return null;
            }
        }
    }

    public Task SetAsync(string name, byte[] secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Directory.CreateDirectory(secretsDirectory);

        var protectedBytes = ProtectedData.Protect(secret, EntropyFor(name), DataProtectionScope.CurrentUser);
        var path = PathFor(name);
        var temporary = path + ".tmp";

        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, path, overwrite: true);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            if (name.Contains(invalid))
            {
                throw new ArgumentException($"Secret name contains an invalid character: {name}", nameof(name));
            }
        }

        return Path.Combine(secretsDirectory, name + ".dpapi");
    }

    private static byte[] EntropyFor(string name) => EntropyFor(name, EntropyPrefix);

    private static byte[] EntropyFor(string name, byte[] prefix) =>
        SHA256.HashData([.. prefix, .. System.Text.Encoding.UTF8.GetBytes(name)]);
}
