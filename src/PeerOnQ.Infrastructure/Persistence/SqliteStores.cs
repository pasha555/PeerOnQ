using System.Globalization;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using Microsoft.Data.Sqlite;

namespace PeerOnQ.Infrastructure.Persistence;

public sealed class SqliteDeviceIdentityRepository(PeerOnQDatabase database) : IDeviceIdentityRepository
{
    public Task<DeviceIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT internal_id, public_id, display_name, created_at, identity_version, public_key,
                   public_id_server_assigned
            FROM device_identity WHERE singleton = 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return Task.FromResult<DeviceIdentity?>(null);
        }

        var identity = new DeviceIdentity
        {
            InternalId = Guid.Parse(reader.GetString(0)),
            PublicId = PeerOnQId.Parse(reader.GetString(1)),
            DisplayName = reader.GetString(2),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            IdentityVersion = reader.GetInt32(4),
            PublicKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            PublicIdServerAssigned = reader.GetInt32(6) == 1,
        };

        return Task.FromResult<DeviceIdentity?>(identity);
    }

    public Task SaveAsync(DeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO device_identity
                (singleton, internal_id, public_id, display_name, created_at, identity_version, public_key,
                 public_id_server_assigned)
            VALUES (1, $internal, $public, $name, $created, $version, $key, $serverAssigned)
            ON CONFLICT(singleton) DO UPDATE SET
                internal_id = excluded.internal_id,
                public_id = excluded.public_id,
                display_name = excluded.display_name,
                created_at = excluded.created_at,
                identity_version = excluded.identity_version,
                public_key = excluded.public_key,
                public_id_server_assigned = excluded.public_id_server_assigned;
            """;

        command.Parameters.AddWithValue("$internal", identity.InternalId.ToString("D"));
        command.Parameters.AddWithValue("$public", identity.PublicId.Value);
        command.Parameters.AddWithValue("$name", identity.DisplayName);
        command.Parameters.AddWithValue("$created", identity.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$version", identity.IdentityVersion);
        command.Parameters.AddWithValue("$key", (object?)identity.PublicKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$serverAssigned", identity.PublicIdServerAssigned ? 1 : 0);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }
}

public sealed class SqliteBlockedDeviceStore(PeerOnQDatabase database) : IBlockedDeviceStore
{
    public Task<bool> IsBlockedAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM blocked_devices WHERE public_id = $id;";
        command.Parameters.AddWithValue("$id", id.Value);
        return Task.FromResult(command.ExecuteScalar() is not null);
    }

    public Task BlockAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR REPLACE INTO blocked_devices (public_id, blocked_at) VALUES ($id, $at);";
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task UnblockAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM blocked_devices WHERE public_id = $id;";
        command.Parameters.AddWithValue("$id", id.Value);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PeerOnQId>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT public_id FROM blocked_devices ORDER BY blocked_at DESC;";

        var result = new List<PeerOnQId>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(PeerOnQId.Parse(reader.GetString(0)));
        }

        return Task.FromResult<IReadOnlyList<PeerOnQId>>(result);
    }
}

public sealed class SqliteSessionAuditLog(PeerOnQDatabase database) : ISessionAuditLog
{
    public Task RecordAsync(SessionAuditEntry entry, CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO session_audit
                (session_id, role, peer_masked_id, peer_name, mode, started_at, ended_at, end_reason)
            VALUES ($id, $role, $peer, $name, $mode, $started, $ended, $reason)
            ON CONFLICT(session_id) DO UPDATE SET
                ended_at = excluded.ended_at,
                end_reason = excluded.end_reason;
            """;

        command.Parameters.AddWithValue("$id", entry.SessionId.ToString());
        command.Parameters.AddWithValue("$role", (int)entry.Role);
        command.Parameters.AddWithValue("$peer", entry.PeerMaskedId);
        command.Parameters.AddWithValue("$name", entry.PeerDisplayName);
        command.Parameters.AddWithValue("$mode", (int)entry.Mode);
        command.Parameters.AddWithValue("$started", entry.StartedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$ended",
            (object?)entry.EndedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (int)entry.EndReason);
        command.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionAuditEntry>> RecentAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, role, peer_masked_id, peer_name, mode, started_at, ended_at, end_reason
            FROM session_audit ORDER BY started_at DESC LIMIT $take;
            """;
        command.Parameters.AddWithValue("$take", take);

        var result = new List<SessionAuditEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SessionAuditEntry
            {
                SessionId = SessionId.TryParse(reader.GetString(0), out var id) ? id : SessionId.New(),
                Role = (SessionRole)reader.GetInt32(1),
                PeerMaskedId = reader.GetString(2),
                PeerDisplayName = reader.GetString(3),
                Mode = (SessionMode)reader.GetInt32(4),
                StartedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                EndedAt = reader.IsDBNull(6)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                EndReason = (SessionEndReason)reader.GetInt32(7),
            });
        }

        return Task.FromResult<IReadOnlyList<SessionAuditEntry>>(result);
    }
}
