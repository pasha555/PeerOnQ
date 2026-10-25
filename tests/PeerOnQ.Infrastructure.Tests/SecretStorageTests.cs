using System.Text;
using System.Security.Cryptography;
using PeerOnQ.Application.Identity;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Infrastructure.Diagnostics;
using PeerOnQ.Infrastructure.Persistence;
using PeerOnQ.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

public class DpapiSecretStoreTests
{
    [Fact]
    public async Task Secrets_round_trip_through_protected_storage()
    {
        using var env = new TempEnvironment();
        var store = new DpapiSecretStore(env.Paths.SecretsDirectory);
        var secret = Encoding.UTF8.GetBytes("top-secret-device-key");

        await store.SetAsync("device-secret", secret);
        var loaded = await store.TryGetAsync("device-secret");

        Assert.NotNull(loaded);
        Assert.Equal(secret, loaded);
    }

    [Fact]
    public async Task Missing_secret_returns_null()
    {
        using var env = new TempEnvironment();
        var store = new DpapiSecretStore(env.Paths.SecretsDirectory);

        Assert.Null(await store.TryGetAsync("never-written"));
    }

    [Fact]
    public async Task Secret_bytes_are_not_stored_in_plaintext_on_disk()
    {
        using var env = new TempEnvironment();
        var store = new DpapiSecretStore(env.Paths.SecretsDirectory);
        var marker = Encoding.UTF8.GetBytes("PLAINTEXT-MARKER-8F2A");

        await store.SetAsync("device-secret", marker);

        var file = Directory.GetFiles(env.Paths.SecretsDirectory, "*.dpapi").Single();
        var raw = File.ReadAllBytes(file);

        Assert.DoesNotContain("PLAINTEXT-MARKER-8F2A", Encoding.UTF8.GetString(raw));
        Assert.True(raw.Length > marker.Length, "DPAPI output should be longer than the input.");
    }

    [Fact]
    public async Task Remove_deletes_the_secret()
    {
        using var env = new TempEnvironment();
        var store = new DpapiSecretStore(env.Paths.SecretsDirectory);
        await store.SetAsync("device-secret", [1, 2, 3]);

        await store.RemoveAsync("device-secret");

        Assert.Null(await store.TryGetAsync("device-secret"));
    }

    [Fact]
    public async Task Secret_names_cannot_escape_the_secrets_directory()
    {
        using var env = new TempEnvironment();
        var store = new DpapiSecretStore(env.Paths.SecretsDirectory);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync("../escape", [1]));
    }

    [Fact]
    public async Task Legacy_entropy_is_decrypted_and_rewritten_with_peeronq_entropy()
    {
        using var env = new TempEnvironment();
        const string name = "legacy-device-secret";
        var secret = Encoding.UTF8.GetBytes("identity-must-survive-rebrand");
        var legacyEntropy = SHA256.HashData([
            .. Encoding.UTF8.GetBytes("Link" + "ora.DeviceSecret.v1"),
            .. Encoding.UTF8.GetBytes(name)]);
        var path = Path.Combine(env.Paths.SecretsDirectory, name + ".dpapi");
        File.WriteAllBytes(path, ProtectedData.Protect(secret, legacyEntropy, DataProtectionScope.CurrentUser));

        var loaded = await new DpapiSecretStore(env.Paths.SecretsDirectory).TryGetAsync(name);

        Assert.Equal(secret, loaded);
        var peerOnQEntropy = SHA256.HashData([
            .. "PeerOnQ.DeviceSecret.v1"u8.ToArray(),
            .. Encoding.UTF8.GetBytes(name)]);
        Assert.Equal(
            secret,
            ProtectedData.Unprotect(File.ReadAllBytes(path), peerOnQEntropy, DataProtectionScope.CurrentUser));
    }
}

public class DataDirectoryMigrationTests
{
    [Fact]
    public void Legacy_data_is_copied_once_and_identity_files_are_preserved()
    {
        var parent = Path.Combine(Path.GetTempPath(), "peeronq-migration-tests", Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(parent, "Link" + "ora");
        var current = Path.Combine(parent, "PeerOnQ");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacy, "secrets"));
            Directory.CreateDirectory(Path.Combine(legacy, "logs"));
            File.WriteAllText(Path.Combine(legacy, "linkora.db"), "database-identity");
            File.WriteAllText(Path.Combine(legacy, "secrets", "device-secret.dpapi"), "protected-identity");
            File.WriteAllText(Path.Combine(legacy, "logs", "linkora-20260811.log"), "masked-log");

            var paths = new PeerOnQPaths(current, legacy).EnsureCreated();
            new PeerOnQPaths(current, legacy).EnsureCreated();

            Assert.Equal("database-identity", File.ReadAllText(paths.DatabaseFile));
            Assert.Equal(
                "protected-identity",
                File.ReadAllText(Path.Combine(paths.SecretsDirectory, "device-secret.dpapi")));
            Assert.True(File.Exists(Path.Combine(paths.LogDirectory, "peeronq-20260811.log")));
            Assert.True(File.Exists(Path.Combine(legacy, "linkora.db")));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Conflicting_current_and_legacy_data_fail_closed()
    {
        var parent = Path.Combine(Path.GetTempPath(), "peeronq-migration-tests", Guid.NewGuid().ToString("N"));
        var legacy = Path.Combine(parent, "Link" + "ora");
        var current = Path.Combine(parent, "PeerOnQ");
        try
        {
            Directory.CreateDirectory(legacy);
            Directory.CreateDirectory(current);
            File.WriteAllText(Path.Combine(legacy, "linkora.db"), "legacy");
            File.WriteAllText(Path.Combine(current, "peeronq.db"), "current");

            Assert.Throws<InvalidOperationException>(() => new PeerOnQPaths(current, legacy).EnsureCreated());
            Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacy, "linkora.db")));
            Assert.Equal("current", File.ReadAllText(Path.Combine(current, "peeronq.db")));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }
}

public class DeviceProvisioningTests
{
    private static DeviceProvisioningService Service(TempEnvironment env) => new(
        new SqliteDeviceIdentityRepository(env.Database),
        new DpapiSecretStore(env.Paths.SecretsDirectory),
        NullLogger<DeviceProvisioningService>.Instance);

    [Fact]
    public async Task First_run_creates_an_identity_and_reruns_reuse_it()
    {
        using var env = new TempEnvironment();

        var first = await Service(env).GetOrCreateAsync("Sharer PC");
        var second = await Service(env).GetOrCreateAsync("Ignored Name");

        Assert.Equal(first.InternalId, second.InternalId);
        Assert.Equal(first.PublicId, second.PublicId);
        Assert.Equal("Sharer PC", second.DisplayName);
        Assert.NotNull(first.PublicKey);
    }

    [Fact]
    public async Task Legacy_identity_without_public_key_is_bound_to_and_persists_protected_signing_key()
    {
        using var env = new TempEnvironment();
        var repository = new SqliteDeviceIdentityRepository(env.Database);
        var legacy = DeviceIdentity.Create("Legacy PC") with { PublicKey = null };
        await repository.SaveAsync(legacy);

        var migrated = await Service(env).GetOrCreateAsync("Ignored Name");
        var reloaded = await repository.LoadAsync();

        Assert.NotNull(migrated.PublicKey);
        Assert.Equal(migrated.PublicKey, reloaded!.PublicKey);
        Assert.Equal(legacy.InternalId, migrated.InternalId);
    }

    [Fact]
    public async Task Existing_identity_with_mismatched_public_key_fails_closed()
    {
        using var env = new TempEnvironment();
        var service = Service(env);
        var identity = await service.GetOrCreateAsync("Sharer PC");
        using var differentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var mismatched = identity with
        {
            PublicKey = Convert.ToBase64String(differentKey.ExportSubjectPublicKeyInfo()),
        };
        await new SqliteDeviceIdentityRepository(env.Database).SaveAsync(mismatched);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(env).GetOrCreateAsync("Ignored Name"));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_secret_material_is_written_to_the_database()
    {
        using var env = new TempEnvironment();
        await Service(env).GetOrCreateAsync("Sharer PC");

        var secretStore = new DpapiSecretStore(env.Paths.SecretsDirectory);
        var deviceSecret = await secretStore.TryGetAsync(DeviceProvisioningService.DeviceSecretName);
        var signingKey = await secretStore.TryGetAsync(DeviceProvisioningService.SigningKeyName);

        Assert.NotNull(deviceSecret);
        Assert.NotNull(signingKey);

        env.ClearDatabasePool();
        var databaseBytes = File.ReadAllBytes(env.Database.DatabaseFile);

        Assert.False(Contains(databaseBytes, deviceSecret!), "device secret leaked into SQLite");
        Assert.False(Contains(databaseBytes, signingKey!), "private key leaked into SQLite");
    }

    [Fact]
    public async Task Registration_proof_verifies_against_the_public_key_only()
    {
        using var env = new TempEnvironment();
        var service = Service(env);
        var identity = await service.GetOrCreateAsync("Sharer PC");

        var proof = await service.ComputeRegistrationProofAsync("challenge-1");

        // What the signaling server does: verify with the pinned public key.
        using var verifier = System.Security.Cryptography.ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(identity.PublicKey!), out _);

        Assert.True(verifier.VerifyData(
            System.Text.Encoding.UTF8.GetBytes("challenge-1"),
            Convert.FromBase64String(proof),
            System.Security.Cryptography.HashAlgorithmName.SHA256));

        // A signature for one challenge must not authenticate another.
        Assert.False(verifier.VerifyData(
            System.Text.Encoding.UTF8.GetBytes("challenge-2"),
            Convert.FromBase64String(proof),
            System.Security.Cryptography.HashAlgorithmName.SHA256));

        // A different device cannot produce a proof that verifies against this key.
        using var otherEnv = new TempEnvironment();
        var otherService = Service(otherEnv);
        await otherService.GetOrCreateAsync("Impostor PC");
        var otherProof = await otherService.ComputeRegistrationProofAsync("challenge-1");

        Assert.False(verifier.VerifyData(
            System.Text.Encoding.UTF8.GetBytes("challenge-1"),
            Convert.FromBase64String(otherProof),
            System.Security.Cryptography.HashAlgorithmName.SHA256));
    }

    [Fact]
    public async Task Session_mac_is_keyed_by_the_device_secret_and_never_reveals_it()
    {
        using var env = new TempEnvironment();
        var service = Service(env);
        await service.GetOrCreateAsync("Sharer PC");

        var macA = await service.ComputeSessionMacAsync("session-1");
        var macB = await service.ComputeSessionMacAsync("session-1");
        var macC = await service.ComputeSessionMacAsync("session-2");

        Assert.Equal(macA, macB);
        Assert.NotEqual(macA, macC);

        var secret = await new DpapiSecretStore(env.Paths.SecretsDirectory)
            .TryGetAsync(DeviceProvisioningService.DeviceSecretName);
        Assert.DoesNotContain(Convert.ToBase64String(secret!), macA);
    }

    [Fact]
    public async Task Rename_keeps_the_public_id()
    {
        using var env = new TempEnvironment();
        var service = Service(env);
        var identity = await service.GetOrCreateAsync("Old Name");

        var renamed = await service.RenameAsync(identity, "  New Name  ");

        Assert.Equal("New Name", renamed.DisplayName);
        Assert.Equal(identity.PublicId, renamed.PublicId);
        Assert.Equal(identity.PublicId, (await service.GetOrCreateAsync("x")).PublicId);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }

        return false;
    }
}

public class LoggingMaskingTests
{
    [Fact]
    public void Full_ids_in_log_text_are_masked()
    {
        var masked = PeerOnQIdMaskingEnricher.Mask("connecting to LNK-483-921-756-204 now");

        Assert.Equal("connecting to 483-***-***-204 now", masked);
    }

    [Fact]
    public void Serilog_file_sink_writes_masked_ids_only()
    {
        using var env = new TempEnvironment();
        var id = PeerOnQId.Parse("LNK-483-921-756-204");

        var logger = PeerOnQLogging.CreateSerilogLogger(env.Paths.LogDirectory);
        logger.Information("Session request from {Peer}", id.Value);
        (logger as IDisposable)?.Dispose();

        var contents = string.Concat(Directory
            .GetFiles(env.Paths.LogDirectory, "*.log")
            .Select(File.ReadAllText));

        Assert.Contains("483-***-***-204", contents);
        Assert.DoesNotContain("LNK-", contents);
        Assert.DoesNotContain("LNK-483-921-756-204", contents);
    }
}
