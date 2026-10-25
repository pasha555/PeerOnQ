using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Foundation;
using PeerOnQ.Application.Abstractions;
using Security;

namespace PeerOnQ.App.Apple;

/// <summary>Stores private device material in the non-synchronizing Apple data-protection Keychain.</summary>
public sealed partial class AppleKeychainDeviceSecretStore : IDeviceSecretStore
{
    private const string Service = "io.peeronq.apple.device-secrets.v1";
    private const int MaximumSecretBytes = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var query = CreateQuery(name);
            using var value = SecKeyChain.QueryAsData(query, false, out var status);
            if (status == SecStatusCode.ItemNotFound) return null;
            EnsureSuccess(status, "read the protected secret");
            return value?.ToArray()
                   ?? throw new CryptographicException("Apple Keychain returned no protected secret data.");
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
        byte[]? copy = null;
        try
        {
            copy = secret.ToArray();
            using var data = NSData.FromArray(copy);
            using var record = CreateQuery(name);
            record.Accessible = SecAccessible.WhenUnlockedThisDeviceOnly;
            record.Synchronizable = false;
            record.ValueData = data;

            var status = SecKeyChain.Add(record);
            if (status == SecStatusCode.DuplicateItem)
            {
                using var query = CreateQuery(name);
                using var update = new SecRecord { ValueData = data };
                status = SecKeyChain.Update(query, update);
            }
            EnsureSuccess(status, "store the protected secret");
        }
        finally
        {
            if (copy is not null) CryptographicOperations.ZeroMemory(copy);
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var query = CreateQuery(name);
            var status = SecKeyChain.Remove(query);
            if (status != SecStatusCode.ItemNotFound)
                EnsureSuccess(status, "remove the protected secret");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SecRecord CreateQuery(string name) => new(SecKind.GenericPassword)
    {
        Service = Service,
        Account = name,
        UseDataProtectionKeychain = true,
        Synchronizable = false,
    };

    private static void EnsureSuccess(SecStatusCode status, string operation)
    {
        if (status != SecStatusCode.Success)
            throw new CryptographicException($"Apple Keychain could not {operation} (status {(int)status}).");
    }

    private static void ValidateName(string name)
    {
        if (!SecretNamePattern().IsMatch(name))
            throw new ArgumentException("Secret names must be 1-64 lowercase letters, digits or hyphens.", nameof(name));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNamePattern();
}
