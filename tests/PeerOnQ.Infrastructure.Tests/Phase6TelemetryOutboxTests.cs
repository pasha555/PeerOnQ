using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using PeerOnQ.Shared.Contracts.V1;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

public sealed class Phase6TelemetryOutboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "peeronq-phase6-outbox", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Session_metadata_is_durable_idempotent_and_contains_no_content_fields()
    {
        Directory.CreateDirectory(_root);
        var database = new PeerOnQDatabase(Path.Combine(_root, "peeronq.db"));
        database.Migrate();
        var outbox = new SqliteClientTelemetryOutbox(database);
        var eventId = Guid.NewGuid();
        var telemetry = new ClientSessionTelemetryEvent
        {
            EventId = eventId,
            Kind = ClientSessionEventKind.Connected,
            SessionId = SessionId.New(),
            Role = SessionRole.Viewer,
            PeerPublicDeviceId = PeerOnQId.NewId().Value,
            PermissionMode = SessionMode.ViewOnly,
            Permissions = SessionPermission.ViewScreen,
            StartedAtUtc = DateTimeOffset.UtcNow,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            ConnectionPath = "DirectInternet",
        };

        await outbox.EnqueueAsync(telemetry);
        await outbox.EnqueueAsync(telemetry);

        var pending = await outbox.ReadPendingAsync();
        var item = Assert.Single(pending);
        Assert.Equal(eventId, item.EventId);
        Assert.DoesNotContain("screenContent", item.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clipboard", item.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileName", item.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keystroke", item.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([telemetry.SessionId.Value], outbox.GetActiveConnectedSessionIds());

        await outbox.EnqueueAsync(telemetry with
        {
            EventId = Guid.NewGuid(),
            Kind = ClientSessionEventKind.Ended,
            OccurredAtUtc = telemetry.OccurredAtUtc,
        });
        Assert.Empty(outbox.GetActiveConnectedSessionIds());

        await outbox.MarkDeliveredAsync(eventId);
        Assert.Single(await outbox.ReadPendingAsync());
    }

    [Fact]
    public async Task Update_events_are_durable_idempotent_and_terminal_rejections_do_not_retry()
    {
        Directory.CreateDirectory(_root);
        var database = new PeerOnQDatabase(Path.Combine(_root, "peeronq.db"));
        database.Migrate();
        var outbox = new SqliteClientUpdateTelemetryOutbox(database);
        var updateEvent = new ClientUpdateEventV1(
            Guid.NewGuid(),
            UpdateEventKindV1.Downloaded,
            "0.6.0",
            InstallChannelV1.Beta,
            ArchitectureKindV1.X64,
            null,
            DateTimeOffset.UtcNow);

        await outbox.EnqueueAsync(updateEvent);
        await outbox.EnqueueAsync(updateEvent);

        var pending = Assert.Single(await outbox.ReadPendingAsync());
        Assert.Equal(updateEvent.EventId, pending.EventId);
        Assert.DoesNotContain("token", pending.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceId", pending.PayloadJson, StringComparison.OrdinalIgnoreCase);

        await outbox.MarkTerminallyRejectedAsync(updateEvent.EventId);
        Assert.Empty(await outbox.ReadPendingAsync());
    }

    public void Dispose()
    {
        var database = new PeerOnQDatabase(Path.Combine(_root, "peeronq.db"));
        using var connection = new SqliteConnection(database.ConnectionString);
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
