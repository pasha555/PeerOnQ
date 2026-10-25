using Microsoft.Data.Sqlite;

namespace PeerOnQ.Infrastructure.Persistence;

/// <summary>
/// Owns the SQLite schema. The database holds public identity, blocked peers and the local
/// session audit trail only - never a device secret, private key or session token.
/// </summary>
public sealed class PeerOnQDatabase(string databaseFile)
{
    public const int SchemaVersion = 6;

    public string DatabaseFile { get; } = databaseFile;

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabaseFile,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
    }.ToString();

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    public void Migrate()
    {
        var directory = Path.GetDirectoryName(DatabaseFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS device_identity (
                singleton         INTEGER PRIMARY KEY CHECK (singleton = 1),
                internal_id       TEXT    NOT NULL,
                public_id         TEXT    NOT NULL,
                display_name      TEXT    NOT NULL,
                created_at        TEXT    NOT NULL,
                identity_version  INTEGER NOT NULL,
                public_key        TEXT    NULL,
                public_id_server_assigned INTEGER NOT NULL DEFAULT 0 CHECK (public_id_server_assigned IN (0, 1))
            );

            CREATE TABLE IF NOT EXISTS blocked_devices (
                public_id  TEXT PRIMARY KEY,
                blocked_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS session_audit (
                session_id       TEXT PRIMARY KEY,
                role             INTEGER NOT NULL,
                peer_masked_id   TEXT    NOT NULL,
                peer_name        TEXT    NOT NULL,
                mode             INTEGER NOT NULL,
                started_at       TEXT    NOT NULL,
                ended_at         TEXT    NULL,
                end_reason       INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_session_audit_started ON session_audit (started_at DESC);

            CREATE TABLE IF NOT EXISTS security_audit (
                event_id        TEXT PRIMARY KEY,
                event_type      INTEGER NOT NULL,
                occurred_at     TEXT    NOT NULL,
                session_id      TEXT    NULL,
                peer_masked_id  TEXT    NULL,
                local_device    TEXT    NULL,
                permission_set  INTEGER NULL,
                outcome         TEXT    NULL,
                failure_category TEXT   NULL,
                network_path    TEXT    NULL,
                app_version     TEXT    NULL,
                safe_metadata   TEXT    NOT NULL,
                integrity_metadata TEXT NOT NULL DEFAULT '{}',
                previous_hash   TEXT    NULL,
                record_hash     TEXT    NULL
            );

            CREATE INDEX IF NOT EXISTS ix_security_audit_occurred ON security_audit (occurred_at DESC);

            CREATE TABLE IF NOT EXISTS client_telemetry_outbox (
                event_id        TEXT PRIMARY KEY,
                event_kind      INTEGER NOT NULL,
                session_id      TEXT NOT NULL,
                payload_json    TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                next_attempt_at TEXT NOT NULL,
                attempt_count   INTEGER NOT NULL DEFAULT 0,
                delivered_at    TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_client_telemetry_pending
                ON client_telemetry_outbox (delivered_at, next_attempt_at, created_at);

            CREATE TABLE IF NOT EXISTS client_update_telemetry_outbox (
                event_id        TEXT PRIMARY KEY,
                payload_json    TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                next_attempt_at TEXT NOT NULL,
                attempt_count   INTEGER NOT NULL DEFAULT 0,
                delivered_at    TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_client_update_telemetry_pending
                ON client_update_telemetry_outbox (delivered_at, next_attempt_at, created_at);
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, transaction, "security_audit", "local_device", "TEXT NULL");
        EnsureColumn(connection, transaction, "security_audit", "permission_set", "INTEGER NULL");
        EnsureColumn(connection, transaction, "security_audit", "failure_category", "TEXT NULL");
        EnsureColumn(connection, transaction, "security_audit", "network_path", "TEXT NULL");
        EnsureColumn(connection, transaction, "security_audit", "app_version", "TEXT NULL");
        EnsureColumn(connection, transaction, "security_audit", "integrity_metadata", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumn(connection, transaction, "security_audit", "previous_hash", "TEXT NULL");
        EnsureColumn(connection, transaction, "security_audit", "record_hash", "TEXT NULL");
        EnsureColumn(connection, transaction, "device_identity", "public_id_server_assigned", "INTEGER NOT NULL DEFAULT 0 CHECK (public_id_server_assigned IN (0, 1))");

        command.CommandText = "SELECT COUNT(*) FROM schema_info;";
        var rows = Convert.ToInt64(command.ExecuteScalar());
        if (rows == 0)
        {
            command.CommandText = "INSERT INTO schema_info (version) VALUES ($v);";
            command.Parameters.AddWithValue("$v", SchemaVersion);
            command.ExecuteNonQuery();
        }
        else
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT version FROM schema_info LIMIT 1;";
            var currentVersion = Convert.ToInt32(command.ExecuteScalar());
            if (currentVersion > SchemaVersion)
                throw new InvalidOperationException("The PeerOnQ database was created by a newer application version.");
            if (currentVersion < SchemaVersion)
            {
                command.CommandText = "UPDATE schema_info SET version = $v;";
                command.Parameters.AddWithValue("$v", SchemaVersion);
                command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string declaration)
    {
        using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        alter.ExecuteNonQuery();
    }
}
