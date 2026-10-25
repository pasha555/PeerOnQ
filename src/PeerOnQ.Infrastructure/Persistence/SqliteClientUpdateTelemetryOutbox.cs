using System.Text.Json;
using System.Text.Json.Serialization;
using PeerOnQ.Infrastructure.Updates;
using Microsoft.Data.Sqlite;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Infrastructure.Persistence;

public sealed record PendingClientUpdateTelemetry(
    Guid EventId,
    string PayloadJson,
    DateTimeOffset CreatedAtUtc,
    int AttemptCount);

public sealed class SqliteClientUpdateTelemetryOutbox(
    PeerOnQDatabase database,
    TimeProvider? timeProvider = null) : IUpdateEventSink
{
    public const int MaximumPendingEvents = 2_000;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };

    public async ValueTask EnqueueAsync(
        ClientUpdateEventV1 updateEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updateEvent);
        var payload = JsonSerializer.Serialize(updateEvent, JsonOptions);
        if (payload.Length > 16 * 1024)
            throw new InvalidOperationException("Client update telemetry exceeded the safe limit.");

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var trim = connection.CreateCommand())
        {
            trim.Transaction = (SqliteTransaction)transaction;
            trim.CommandText =
                """
                DELETE FROM client_update_telemetry_outbox
                WHERE event_id IN (
                    SELECT event_id FROM client_update_telemetry_outbox
                    WHERE delivered_at IS NOT NULL OR attempt_count >= 20
                    ORDER BY COALESCE(delivered_at, created_at) ASC
                    LIMIT MAX(0, (SELECT COUNT(*) FROM client_update_telemetry_outbox) - $maximum + 1)
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
                INSERT OR IGNORE INTO client_update_telemetry_outbox
                    (event_id, payload_json, created_at, next_attempt_at, attempt_count)
                VALUES ($id, $payload, $created, $next, 0);
                """;
            command.Parameters.AddWithValue("$id", updateEvent.EventId.ToString("D"));
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$created", updateEvent.OccurredAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$next", updateEvent.OccurredAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingClientUpdateTelemetry>> ReadPendingAsync(
        int maximum = 25,
        CancellationToken cancellationToken = default)
    {
        maximum = Math.Clamp(maximum, 1, 100);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, payload_json, created_at, attempt_count
            FROM client_update_telemetry_outbox
            WHERE delivered_at IS NULL AND attempt_count < 20 AND next_attempt_at <= $now
            ORDER BY created_at ASC
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$maximum", maximum);

        var result = new List<PendingClientUpdateTelemetry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PendingClientUpdateTelemetry(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.GetInt32(3)));
        }

        return result;
    }

    public Task MarkDeliveredAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        UpdateAsync(eventId, delivered: true, terminal: false, cancellationToken);

    public Task MarkFailedAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        UpdateAsync(eventId, delivered: false, terminal: false, cancellationToken);

    public Task MarkTerminallyRejectedAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        UpdateAsync(eventId, delivered: false, terminal: true, cancellationToken);

    private async Task UpdateAsync(
        Guid eventId,
        bool delivered,
        bool terminal,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var currentAttempts = 0;
        if (!delivered && !terminal)
        {
            await using var inspect = connection.CreateCommand();
            inspect.CommandText =
                "SELECT attempt_count FROM client_update_telemetry_outbox WHERE event_id = $id AND delivered_at IS NULL;";
            inspect.Parameters.AddWithValue("$id", eventId.ToString("D"));
            currentAttempts = Convert.ToInt32(await inspect.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        await using var command = connection.CreateCommand();
        var now = _time.GetUtcNow();
        if (delivered)
        {
            command.CommandText =
                "UPDATE client_update_telemetry_outbox SET delivered_at = $now WHERE event_id = $id AND delivered_at IS NULL;";
        }
        else if (terminal)
        {
            command.CommandText =
                "UPDATE client_update_telemetry_outbox SET attempt_count = 20 WHERE event_id = $id AND delivered_at IS NULL;";
        }
        else
        {
            command.CommandText =
                """
                UPDATE client_update_telemetry_outbox
                SET attempt_count = MIN(attempt_count + 1, 20),
                    next_attempt_at = $next
                WHERE event_id = $id AND delivered_at IS NULL;
                """;
        }

        command.Parameters.AddWithValue("$now", now.ToString("O"));
        var exponentialSeconds = Math.Min(300, Math.Pow(2, Math.Min(currentAttempts, 8)));
        var delay = TimeSpan.FromSeconds(exponentialSeconds + Random.Shared.NextDouble());
        command.Parameters.AddWithValue("$next", (now + delay).ToString("O"));
        command.Parameters.AddWithValue("$id", eventId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
