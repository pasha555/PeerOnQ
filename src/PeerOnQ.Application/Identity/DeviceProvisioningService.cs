using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace PeerOnQ.Application.Identity;

/// <summary>
/// Creates the device identity on first run and reuses it forever after.
/// The public half goes to SQLite; the device secret and the signing key stay in
/// OS-protected storage and never touch the database or the logs.
/// </summary>
public sealed class DeviceProvisioningService(
    IDeviceIdentityRepository repository,
    IDeviceSecretStore secretStore,
    ILogger<DeviceProvisioningService> logger,
    TimeProvider? timeProvider = null) : IRegistrationProofProvider
{
    public const string DeviceSecretName = "device-secret";
    public const string SigningKeyName = "device-signing-key";
    public const int DeviceSecretBytes = 32;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<DeviceIdentity> GetOrCreateAsync(
        string defaultDisplayName,
        CancellationToken cancellationToken = default)
    {
        var existing = await repository.LoadAsync(cancellationToken);
        if (existing is not null)
        {
            var derivedPublicKey = await EnsureSecretsAsync(cancellationToken);
            if (existing.PublicKey is null)
            {
                existing = existing with { PublicKey = derivedPublicKey };
                await repository.SaveAsync(existing, cancellationToken);
                logger.LogInformation("Migrated legacy device identity to its protected signing key");
            }
            else if (!PublicKeysMatch(existing.PublicKey, derivedPublicKey))
            {
                throw new InvalidOperationException(
                    "Stored device public key does not match the protected signing key; identity recovery is required.");
            }
            logger.LogInformation("Loaded existing device identity {Device}", existing.ToLogString());
            return existing;
        }

        var identity = DeviceIdentity.Create(defaultDisplayName, _timeProvider);
        var publicKey = await EnsureSecretsAsync(cancellationToken);
        identity = identity with { PublicKey = publicKey };

        await repository.SaveAsync(identity, cancellationToken);
        logger.LogInformation("Provisioned new device identity {Device}", identity.ToLogString());
        return identity;
    }

    public async Task<DeviceIdentity> RenameAsync(
        DeviceIdentity identity,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        var renamed = identity with { DisplayName = displayName.Trim() };
        await repository.SaveAsync(renamed, cancellationToken);
        return renamed;
    }

    public async Task<DeviceIdentity> AssignServerPublicIdAsync(
        DeviceIdentity identity,
        string publicDeviceId,
        CancellationToken cancellationToken = default)
    {
        var assigned = PeerOnQId.Parse(publicDeviceId);
        if (identity.PublicIdServerAssigned && identity.PublicId != assigned)
            throw new InvalidOperationException("The cloud returned a different alias for an already enrolled signing identity.");

        if (identity.PublicIdServerAssigned) return identity;
        var updated = identity with
        {
            PublicId = assigned,
            PublicIdServerAssigned = true,
        };
        await repository.SaveAsync(updated, cancellationToken);
        logger.LogInformation("Persisted server-assigned device alias {MaskedDeviceId}", updated.PublicId.Masked);
        return updated;
    }

    /// <summary>
    /// Signs the signaling challenge with the device private key. The server verifies the
    /// signature against the public key it pinned for this PeerOnQ ID, so a stolen ID alone
    /// cannot impersonate the device and the server never learns any private material.
    /// </summary>
    public async Task<string> ComputeRegistrationProofAsync(
        string challenge,
        CancellationToken cancellationToken = default)
    {
        var keyMaterial = await secretStore.TryGetAsync(SigningKeyName, cancellationToken)
                          ?? throw new InvalidOperationException("Signing key is missing; re-provision the device.");

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(keyMaterial, out _);

        var signature = ecdsa.SignData(
            System.Text.Encoding.UTF8.GetBytes(challenge),
            HashAlgorithmName.SHA256);

        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Keyed MAC over a session request, used as replay/authenticity evidence on the wire.
    /// Kept separate from the signing key so the two never share material.
    /// </summary>
    public async Task<string> ComputeSessionMacAsync(string payload, CancellationToken cancellationToken = default)
    {
        var secret = await secretStore.TryGetAsync(DeviceSecretName, cancellationToken)
                     ?? throw new InvalidOperationException("Device secret is missing; re-provision the device.");

        return Convert.ToBase64String(HMACSHA256.HashData(secret, System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    private async Task<string> EnsureSecretsAsync(CancellationToken cancellationToken)
    {
        var secret = await secretStore.TryGetAsync(DeviceSecretName, cancellationToken);
        if (secret is null)
        {
            secret = RandomNumberGenerator.GetBytes(DeviceSecretBytes);
            await secretStore.SetAsync(DeviceSecretName, secret, cancellationToken);
            logger.LogInformation("Created a new device secret in protected storage");
        }

        var keyMaterial = await secretStore.TryGetAsync(SigningKeyName, cancellationToken);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (keyMaterial is null)
        {
            keyMaterial = ecdsa.ExportPkcs8PrivateKey();
            await secretStore.SetAsync(SigningKeyName, keyMaterial, cancellationToken);
            logger.LogInformation("Created a new device signing key in protected storage");
        }
        else
        {
            ecdsa.ImportPkcs8PrivateKey(keyMaterial, out _);
        }

        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static bool PublicKeysMatch(string storedPublicKey, string derivedPublicKey)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(storedPublicKey),
                Convert.FromBase64String(derivedPublicKey));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
