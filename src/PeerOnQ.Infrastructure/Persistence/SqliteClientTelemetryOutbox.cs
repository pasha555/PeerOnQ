using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeerOnQ.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace PeerOnQ.Infrastructure.Persistence;

public sealed record PendingClientTelemetry(
    Guid EventId,
    ClientSessionEventKind Kind,
    string SessionId,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount);

public sealed class SqliteClientTelemetryOutbox(
    PeerOnQDatabase database,
    TimeProvider? timeProvider = null) : IClientSessionEventSink
{
    public const int MaximumPendingEvents = 10_000;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, byte> _activeConnectedSessions = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public async ValueTask EnqueueAsync(
        ClientSessionTelemetryEvent telemetryEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        if (telemetryEvent.Kind is ClientSessionEventKind.Ended or ClientSessionEventKind.Failed)
            _activeConnectedSessions.TryRemove(telemetryEvent.SessionId.Value, out _);
        var payload = JsonSerializer.Serialize(telemetryEvent, JsonOptions);
        if (payload.Length > 32 * 1024)
            throw new InvalidOperationException("Client telemetry payload exceeded the safe limit.");

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var trim = connection.CreateCommand())
        {
            trim.Transaction = (SqliteTransaction)transaction;
            trim.CommandText =
                """
                DELETE FROM client_telemetry_outbox
                WHERE event_id IN (
                    SELECT event_id FROM client_telemetry_outbox
                    WHERE delivered_at IS NOT NULL OR attempt_count >= 20
                    ORDER BY COALESCE(delivered_at, created_at) ASC
                    LIMIT MAX(0, (SELECT COUNT(*) FROM client_telemetry_outbox) - $maximum + 1)
                );
                """;
            trim.Parameters.AddWithValue("$maximum", MaximumPendingEvents);
            await trim.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO client_telemetry_outbox
                    (event_id, event_kind, session_id, payload_json, created_at, next_attempt_at, attempt_count)
                VALUES ($id, $kind, $session, $payload, $created, $next, 0);
                """;
            command.Parameters.AddWithValue("$id", telemetryEvent.EventId.ToString("D"));
            command.Parameters.AddWithValue("$kind", (int)telemetryEvent.Kind);
            command.Parameters.AddWithValue("$session", telemetryEvent.SessionId.ToString());
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$created", telemetryEvent.OccurredAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$next", telemetryEvent.OccurredAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        if (telemetryEvent.Kind == ClientSessionEventKind.Connected)
            _activeConnectedSessions.TryAdd(telemetryEvent.SessionId.Value, 0);
    }

    public IReadOnlyList<Guid> GetActiveConnectedSessionIds(int maximum = 64)
    {
        if (maximum is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximum));
        return _activeConnectedSessions.Keys.Take(maximum).ToArray();
    }

    public void StopSessionHeartbeat(Guid sessionId) => _activeConnectedSessions.TryRemove(sessionId, out _);

    public async Task<IReadOnlyList<PendingClientTelemetry>> ReadPendingAsync(
        int maximum = 50,
        CancellationToken cancellationToken = default)
    {
        maximum = Math.Clamp(maximum, 1, 100);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, event_kind, session_id, payload_json, created_at, attempt_count
            FROM client_telemetry_outbox
            WHERE delivered_at IS NULL AND attempt_count < 20 AND next_attempt_at <= $now
            ORDER BY created_at ASC
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$maximum", maximum);

        var result = new List<PendingClientTelemetry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PendingClientTelemetry(
                Guid.Parse(reader.GetString(0)),
                (ClientSessionEventKind)reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.GetInt32(5)));
        }

        return result;
    }

    public Task MarkDeliveredAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        UpdateAsync(eventId, delivered: true, cancellationToken);

    public Task MarkFailedAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        UpdateAsync(eventId, delivered: false, cancellationToken);

    public async Task MarkTerminallyRejectedAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE client_telemetry_outbox SET attempt_count = 20 WHERE event_id = $id AND delivered_at IS NULL;";
        command.Parameters.AddWithValue("$id", eventId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateAsync(Guid eventId, bool delivered, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var currentAttempts = 0;
        if (!delivered)
        {
            await using var inspect = connection.CreateCommand();
            inspect.CommandText =
                "SELECT attempt_count FROM client_telemetry_outbox WHERE event_id = $id AND delivered_at IS NULL;";
            inspect.Parameters.AddWithValue("$id", eventId.ToString("D"));
            currentAttempts = Convert.ToInt32(await inspect.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        await using var command = connection.CreateCommand();
        if (delivered)
        {
            command.CommandText =
                "UPDATE client_telemetry_outbox SET delivered_at = $now WHERE event_id = $id AND delivered_at IS NULL;";
        }
        else
        {
            command.CommandText =
                """
                UPDATE client_telemetry_outbox
                SET attempt_count = MIN(attempt_count + 1, 20),
                    next_attempt_at = $next
                WHERE event_id = $id AND delivered_at IS NULL;
                """;
        }

        var now = _time.GetUtcNow();
        var exponentialSeconds = Math.Min(300, Math.Pow(2, Math.Min(currentAttempts, 8)));
        var delay = TimeSpan.FromSeconds(exponentialSeconds + Random.Shared.NextDouble());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$next", (now + delay).ToString("O"));
        command.Parameters.AddWithValue("$id", eventId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
