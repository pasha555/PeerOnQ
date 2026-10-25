using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Abstractions;

public enum ClientSessionEventKind
{
    Connected = 0,
    Ended = 1,
    Failed = 2,
    Started = 3,
}

/// <summary>
/// Privacy-bounded operational metadata. This contract deliberately has no fields for screen,
/// input, clipboard, filenames, transferred content, credentials, or network addresses.
/// </summary>
public sealed record ClientSessionTelemetryEvent
{
    public required Guid EventId { get; init; }
    public required ClientSessionEventKind Kind { get; init; }
    public required SessionId SessionId { get; init; }
    public required SessionRole Role { get; init; }
    public required string PeerPublicDeviceId { get; init; }
    public required SessionMode PermissionMode { get; init; }
    public required SessionPermission Permissions { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public string ConnectionPath { get; init; } = "Unknown";
    public string? RelayServerId { get; init; }
    public string? ServerRegion { get; init; }
    public string? EndReason { get; init; }
    public string? FailureStage { get; init; }
    public string? FailureCode { get; init; }
    public bool UsedTurn { get; init; }
    public int ReconnectCount { get; init; }
}

public interface IClientSessionEventSink
{
    ValueTask EnqueueAsync(
        ClientSessionTelemetryEvent telemetryEvent,
        CancellationToken cancellationToken = default);
}

public sealed class NoOpClientSessionEventSink : IClientSessionEventSink
{
    public static NoOpClientSessionEventSink Instance { get; } = new();

    private NoOpClientSessionEventSink() { }

    public ValueTask EnqueueAsync(
        ClientSessionTelemetryEvent telemetryEvent,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
