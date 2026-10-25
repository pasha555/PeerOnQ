using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Infrastructure;
using PeerOnQ.Infrastructure.Persistence;
using PeerOnQ.Infrastructure.Security;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

/// <summary>A throwaway PeerOnQ data directory per test.</summary>
public sealed class TempEnvironment : IDisposable
{
    public TempEnvironment()
    {
        Paths = new PeerOnQPaths(Path.Combine(Path.GetTempPath(), "peeronq-tests", Guid.NewGuid().ToString("N")))
            .EnsureCreated();
        Database = new PeerOnQDatabase(Paths.DatabaseFile);
        Database.Migrate();
    }

    public PeerOnQPaths Paths { get; }
    public PeerOnQDatabase Database { get; }

    public void ClearDatabasePool()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(Database.ConnectionString);
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
    }

    public void Dispose()
    {
        ClearDatabasePool();
        try
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
        catch (IOException)
        {
            // The log file may still be held; the temp directory is disposable anyway.
        }
    }
}

public class DeviceIdentityRepositoryTests
{
    [Fact]
    public async Task Identity_survives_a_restart()
    {
        using var env = new TempEnvironment();
        var identity = DeviceIdentity.Create("Sharer PC") with { PublicKey = "cHVibGlj" };

        await new SqliteDeviceIdentityRepository(env.Database).SaveAsync(identity);

        // A brand new repository instance stands in for restarting the application.
        var loaded = await new SqliteDeviceIdentityRepository(env.Database).LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(identity.InternalId, loaded!.InternalId);
        Assert.Equal(identity.PublicId, loaded.PublicId);
        Assert.Equal(identity.DisplayName, loaded.DisplayName);
        Assert.Equal(identity.IdentityVersion, loaded.IdentityVersion);
        Assert.Equal("cHVibGlj", loaded.PublicKey);
        Assert.Equal(identity.CreatedAt.ToUnixTimeMilliseconds(), loaded.CreatedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Load_returns_null_before_provisioning()
    {
        using var env = new TempEnvironment();

        Assert.Null(await new SqliteDeviceIdentityRepository(env.Database).LoadAsync());
    }

    [Fact]
    public async Task Saving_twice_updates_the_single_row()
    {
        using var env = new TempEnvironment();
        var repository = new SqliteDeviceIdentityRepository(env.Database);
        var identity = DeviceIdentity.Create("First");

        await repository.SaveAsync(identity);
        await repository.SaveAsync(identity with { DisplayName = "Renamed" });

        using var connection = env.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM device_identity;";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));

        var loaded = await repository.LoadAsync();
        Assert.Equal("Renamed", loaded!.DisplayName);
        Assert.Equal(identity.PublicId, loaded.PublicId);
    }
}

public class BlockedDeviceStoreTests
{
    [Fact]
    public async Task Blocking_is_persisted_and_queryable()
    {
        using var env = new TempEnvironment();
        var store = new SqliteBlockedDeviceStore(env.Database);
        var id = PeerOnQId.Parse("LNK-483-921-756-204");

        Assert.False(await store.IsBlockedAsync(id));

        await store.BlockAsync(id);

        Assert.True(await store.IsBlockedAsync(id));
        Assert.Contains(id, await store.ListAsync());
        Assert.False(await store.IsBlockedAsync(PeerOnQId.Parse("LNK-111-222-333-444")));
    }

    [Fact]
    public async Task Blocking_the_same_device_twice_is_idempotent()
    {
        using var env = new TempEnvironment();
        var store = new SqliteBlockedDeviceStore(env.Database);
        var id = PeerOnQId.NewId();

        await store.BlockAsync(id);
        await store.BlockAsync(id);

        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Unblocking_removes_only_the_selected_device_and_is_idempotent()
    {
        using var env = new TempEnvironment();
        var store = new SqliteBlockedDeviceStore(env.Database);
        var selected = PeerOnQId.Parse("LNK-407-111-222-464");
        var retained = PeerOnQId.Parse("LNK-126-333-444-356");

        await store.BlockAsync(selected);
        await store.BlockAsync(retained);

        await store.UnblockAsync(selected);
        await store.UnblockAsync(selected);

        Assert.False(await store.IsBlockedAsync(selected));
        Assert.True(await store.IsBlockedAsync(retained));
        Assert.Equal([retained], await store.ListAsync());
    }
}

public class SessionAuditLogTests
{
    [Fact]
    public async Task Audit_entries_record_start_and_end_without_full_ids()
    {
        using var env = new TempEnvironment();
        var log = new SqliteSessionAuditLog(env.Database);
        var peer = PeerOnQId.Parse("LNK-483-921-756-204");
        var sessionId = SessionId.New();

        var entry = new SessionAuditEntry
        {
            SessionId = sessionId,
            Role = SessionRole.Sharer,
            PeerMaskedId = peer.Masked,
            PeerDisplayName = "Viewer PC",
            Mode = SessionMode.ViewOnly,
            StartedAt = DateTimeOffset.UtcNow,
        };

        await log.RecordAsync(entry);
        await log.RecordAsync(entry with
        {
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(3),
            EndReason = SessionEndReason.EndedBySharer,
        });

        var recent = await log.RecentAsync();
        var stored = Assert.Single(recent);

        Assert.Equal(sessionId, stored.SessionId);
        Assert.Equal(SessionEndReason.EndedBySharer, stored.EndReason);
        Assert.NotNull(stored.EndedAt);
        Assert.Equal("LNK-483-***-***-204", stored.PeerMaskedId);
        Assert.DoesNotContain("921", stored.PeerMaskedId);
    }
}
