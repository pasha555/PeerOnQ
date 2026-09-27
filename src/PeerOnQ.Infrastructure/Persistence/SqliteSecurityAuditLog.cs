using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Infrastructure.Diagnostics;

namespace PeerOnQ.Infrastructure.Persistence;

public sealed partial class SqliteSecurityAuditLog : ISecurityAuditLog
{
    private static readonly HashSet<string> ForbiddenMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "file", "filename", "clipboard", "content", "password", "secret", "token",
        "address", "ip", "uri", "url", "email", "username", "key",
    };

    private readonly PeerOnQDatabase _database;
    private readonly byte[] _integrityKey;
    private readonly string? _localDevice;
    private readonly string? _appVersion;
    private readonly Lock _gate = new();

    public SqliteSecurityAuditLog(
        PeerOnQDatabase database,
        ReadOnlyMemory<byte> integrityKey = default,
        string? localDevice = null,
        string? appVersion = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _integrityKey = integrityKey.ToArray();
        if (_integrityKey.Length is not 0 and < 32)
            throw new ArgumentException("The audit integrity key must contain at least 256 bits.", nameof(integrityKey));
        _localDevice = Sanitize(localDevice, 32);
        _appVersion = Sanitize(appVersion, 32);
    }

    public Task AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(auditEvent);

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var previousHash = ReadLastHash(connection, transaction);
            var recordHash = ComputeHash(previousHash, normalized);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO security_audit
                    (event_id, event_type, occurred_at, session_id, peer_masked_id, local_device,
                     permission_set, outcome, failure_category, network_path, app_version,
                     safe_metadata, integrity_metadata, previous_hash, record_hash)
                VALUES
                    ($id, $type, $occurred, $session, $peer, $local, $permissions, $outcome,
                     $failure, $network, $version, $metadata, $integrity, $previous, $record);
                """;
            Bind(command, normalized, previousHash, recordHash);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecurityAuditEvent>> ReadRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limit = Math.Clamp(limit, 1, 500);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " FROM security_audit ORDER BY occurred_at DESC, rowid DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        var events = new List<SecurityAuditEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) events.Add(ReadEvent(reader));
        return Task.FromResult<IReadOnlyList<SecurityAuditEvent>>(events);
    }

    public Task<AuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = SelectColumns + ", previous_hash, record_hash FROM security_audit ORDER BY occurred_at, rowid;";
            using var reader = command.ExecuteReader();

            string? expectedPrevious = null;
            var verified = 0;
            var chainStarted = false;
            while (reader.Read())
            {
                var storedPrevious = reader.IsDBNull(13) ? null : reader.GetString(13);
                var storedHash = reader.IsDBNull(14) ? null : reader.GetString(14);
                if (storedHash is null)
                {
                    if (chainStarted)
                        return Task.FromResult(new AuditIntegrityResult(false, verified, "A chained audit record is missing its integrity hash."));
                    continue; // Schema-v2 legacy prefix.
                }

                chainStarted = true;
                if (!FixedHashEquals(storedPrevious, expectedPrevious))
                    return Task.FromResult(new AuditIntegrityResult(false, verified, "The audit chain predecessor does not match."));

                var calculated = ComputeHash(expectedPrevious, ReadEvent(reader));
                if (!FixedHashEquals(storedHash, calculated))
                    return Task.FromResult(new AuditIntegrityResult(false, verified, "An audit record failed integrity verification."));

                expectedPrevious = storedHash;
                verified++;
            }

            return Task.FromResult(new AuditIntegrityResult(true, verified, null));
        }
    }

    public async Task ExportSanitizedJsonLinesAsync(string destinationFile, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);
        var integrity = await VerifyIntegrityAsync(cancellationToken);
        if (!integrity.IsValid)
            throw new InvalidOperationException($"Audit export stopped: {integrity.Failure}");

        var fullPath = Path.GetFullPath(destinationFile);
        var parent = Path.GetDirectoryName(fullPath)
                     ?? throw new ArgumentException("The audit export destination has no parent directory.", nameof(destinationFile));
        Directory.CreateDirectory(parent);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + ", previous_hash, record_hash FROM security_audit ORDER BY occurred_at, rowid;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var auditEvent = ReadEvent(reader);
            var export = new
            {
                eventId = auditEvent.EventId,
                eventType = auditEvent.EventType.ToString(),
                timestampUtc = auditEvent.OccurredAt,
                localDevice = auditEvent.LocalDevice,
                remoteDevice = auditEvent.PeerMaskedId,
                sessionId = auditEvent.SessionId,
                permissionSet = auditEvent.PermissionSet?.ToString(),
                result = auditEvent.Outcome,
                failureCategory = auditEvent.FailureCategory,
                networkPath = auditEvent.NetworkPath,
                appVersion = auditEvent.AppVersion,
                metadata = auditEvent.SafeMetadata,
                integrityMetadata = auditEvent.IntegrityMetadata,
                previousHash = reader.IsDBNull(13) ? null : reader.GetString(13),
                recordHash = reader.IsDBNull(14) ? null : reader.GetString(14),
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(export).AsMemory(), cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);

        await AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = SecurityAuditEventType.AuditExported,
            OccurredAt = DateTimeOffset.UtcNow,
            Outcome = "completed",
        }, cancellationToken);
    }

    public Task ApplyRetentionAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        if (retention < TimeSpan.FromDays(1) || retention > TimeSpan.FromDays(3650))
            throw new ArgumentOutOfRangeException(nameof(retention), "Audit retention must be between 1 and 3650 days.");
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM security_audit WHERE occurred_at < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(retention).ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            RebuildChain(connection, transaction);
            transaction.Commit();
        }
        return Task.CompletedTask;
    }

    public async Task ClearAsync(bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) throw new InvalidOperationException("Audit clearing requires explicit confirmation.");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM security_audit;";
            command.ExecuteNonQuery();
        }

        await AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = SecurityAuditEventType.AuditCleared,
            OccurredAt = DateTimeOffset.UtcNow,
            Outcome = "confirmed",
        }, cancellationToken);
    }

    private SecurityAuditEvent Normalize(SecurityAuditEvent auditEvent) => auditEvent with
    {
        SessionId = Sanitize(auditEvent.SessionId, 64),
        PeerMaskedId = Sanitize(auditEvent.PeerMaskedId, 32),
        LocalDevice = Sanitize(auditEvent.LocalDevice, 32) ?? _localDevice,
        Outcome = Sanitize(auditEvent.Outcome, 64),
        FailureCategory = Sanitize(auditEvent.FailureCategory, 64),
        NetworkPath = Sanitize(auditEvent.NetworkPath, 32),
        AppVersion = Sanitize(auditEvent.AppVersion, 32) ?? _appVersion,
        SafeMetadata = SanitizeMetadata(auditEvent.SafeMetadata),
        IntegrityMetadata = SanitizeMetadata(auditEvent.IntegrityMetadata),
    };

    private static IReadOnlyDictionary<string, string> SanitizeMetadata(IReadOnlyDictionary<string, string> source)
    {
        var safe = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in source
                     .Where(pair => !ForbiddenMetadataKeys.Any(forbidden => pair.Key.Contains(forbidden, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                     .Take(20))
        {
            var key = Sanitize(pair.Key, 40);
            var value = Sanitize(pair.Value, 120);
            if (key is not null && value is not null) safe[key] = value;
        }
        return safe;
    }

    private string ComputeHash(string? previousHash, SecurityAuditEvent auditEvent)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            previousHash,
            eventId = auditEvent.EventId.ToString("D"),
            eventType = (int)auditEvent.EventType,
            occurredAt = auditEvent.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            auditEvent.SessionId,
            auditEvent.PeerMaskedId,
            auditEvent.LocalDevice,
            permissionSet = auditEvent.PermissionSet is null ? null : (long?)auditEvent.PermissionSet.Value,
            auditEvent.Outcome,
            auditEvent.FailureCategory,
            auditEvent.NetworkPath,
            auditEvent.AppVersion,
            safeMetadata = auditEvent.SafeMetadata.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            integrityMetadata = auditEvent.IntegrityMetadata.OrderBy(pair => pair.Key, StringComparer.Ordinal),
        });

        return _integrityKey.Length == 0
            ? Convert.ToHexString(SHA256.HashData(canonical))
            : Convert.ToHexString(HMACSHA256.HashData(_integrityKey, canonical));
    }

    private static string? ReadLastHash(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT record_hash FROM security_audit WHERE record_hash IS NOT NULL ORDER BY occurred_at DESC, rowid DESC LIMIT 1;";
        return command.ExecuteScalar() as string;
    }

    private void RebuildChain(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var rows = new List<SecurityAuditEvent>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = SelectColumns + " FROM security_audit ORDER BY occurred_at, rowid;";
            using var reader = read.ExecuteReader();
            while (reader.Read()) rows.Add(ReadEvent(reader));
        }

        string? previousHash = null;
        foreach (var auditEvent in rows)
        {
            var recordHash = ComputeHash(previousHash, auditEvent);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                "UPDATE security_audit SET previous_hash = $previous, record_hash = $record WHERE event_id = $id;";
            update.Parameters.AddWithValue("$previous", (object?)previousHash ?? DBNull.Value);
            update.Parameters.AddWithValue("$record", recordHash);
            update.Parameters.AddWithValue("$id", auditEvent.EventId.ToString("D"));
            update.ExecuteNonQuery();
            previousHash = recordHash;
        }
    }

    private static void Bind(
        Microsoft.Data.Sqlite.SqliteCommand command,
        SecurityAuditEvent auditEvent,
        string? previousHash,
        string recordHash)
    {
        command.Parameters.AddWithValue("$id", auditEvent.EventId.ToString("D"));
        command.Parameters.AddWithValue("$type", (int)auditEvent.EventType);
        command.Parameters.AddWithValue("$occurred", auditEvent.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$session", (object?)auditEvent.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$peer", (object?)auditEvent.PeerMaskedId ?? DBNull.Value);
        command.Parameters.AddWithValue("$local", (object?)auditEvent.LocalDevice ?? DBNull.Value);
        command.Parameters.AddWithValue("$permissions", auditEvent.PermissionSet is null ? DBNull.Value : (long)auditEvent.PermissionSet.Value);
        command.Parameters.AddWithValue("$outcome", (object?)auditEvent.Outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure", (object?)auditEvent.FailureCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$network", (object?)auditEvent.NetworkPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", (object?)auditEvent.AppVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(auditEvent.SafeMetadata));
        command.Parameters.AddWithValue("$integrity", JsonSerializer.Serialize(auditEvent.IntegrityMetadata));
        command.Parameters.AddWithValue("$previous", (object?)previousHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$record", recordHash);
    }

    private static SecurityAuditEvent ReadEvent(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        EventId = Guid.Parse(reader.GetString(0)),
        EventType = (SecurityAuditEventType)reader.GetInt32(1),
        OccurredAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
        SessionId = reader.IsDBNull(3) ? null : reader.GetString(3),
        PeerMaskedId = reader.IsDBNull(4) ? null : reader.GetString(4),
        LocalDevice = reader.IsDBNull(5) ? null : reader.GetString(5),
        PermissionSet = reader.IsDBNull(6) ? null : (PeerOnQ.Domain.Sessions.SessionPermission?)reader.GetInt64(6),
        Outcome = reader.IsDBNull(7) ? null : reader.GetString(7),
        FailureCategory = reader.IsDBNull(8) ? null : reader.GetString(8),
        NetworkPath = reader.IsDBNull(9) ? null : reader.GetString(9),
        AppVersion = reader.IsDBNull(10) ? null : reader.GetString(10),
        SafeMetadata = DeserializeMetadata(reader.GetString(11)),
        IntegrityMetadata = DeserializeMetadata(reader.GetString(12)),
    };

    private static IReadOnlyDictionary<string, string> DeserializeMetadata(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    private const string SelectColumns =
        "SELECT event_id, event_type, occurred_at, session_id, peer_masked_id, local_device, " +
        "permission_set, outcome, failure_category, network_path, app_version, safe_metadata, integrity_metadata";

    private static bool FixedHashEquals(string? left, string? right)
    {
        if (left is null || right is null) return left == right;
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? Sanitize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = new string(value.Where(character => !char.IsControl(character)).Take(maximumLength * 2).ToArray()).Trim();
        result = PeerOnQIdMaskingEnricher.Mask(result);
        result = Ipv4Pattern().Replace(result, "[redacted-address]");
        result = Ipv6CandidatePattern().Replace(result, match => IsIpv6(match.Value) ? "[redacted-address]" : match.Value);
        result = result.Length > maximumLength ? result[..maximumLength] : result;
        return result.Length == 0 ? null : result;
    }

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])\[?[0-9A-Fa-f:]+(?:%[A-Za-z0-9_.-]+)?\]?(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6CandidatePattern();

    private static bool IsIpv6(string candidate)
    {
        candidate = candidate.Trim('[', ']');
        var zoneIndex = candidate.IndexOf('%');
        if (zoneIndex >= 0) candidate = candidate[..zoneIndex];
        return candidate.Contains(':')
               && IPAddress.TryParse(candidate, out var address)
               && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    }
}
