using System.Net;

namespace PeerOnQ.Application.Abstractions;

/// <summary>Versioned data-plane transport selected for a remote session.</summary>
public enum RemoteSessionTransportKind
{
    Unknown = 0,
    NativeQuic = 1,
    WebRtcCompatibility = 2,
}

/// <summary>
/// Logical channels stay independent so bulk work cannot create application-level head-of-line
/// blocking for input or control records.
/// </summary>
public enum RemoteTransportChannel : byte
{
    Mouse = 1,
    Keyboard = 2,
    Control = 3,
    Screen = 4,
    Audio = 5,
    Clipboard = 6,
    FileTransfer = 7,
    Telemetry = 8,
}

public enum RemoteTransportDelivery
{
    ReliableOrdered = 0,
    UnreliableLatest = 1,
}

public enum RemoteTransportPriority
{
    Interactive = 0,
    Control = 1,
    RealtimeMedia = 2,
    Clipboard = 3,
    Background = 4,
}

public sealed record RemoteTransportChannelDefinition(
    RemoteTransportChannel Channel,
    RemoteTransportDelivery Delivery,
    RemoteTransportPriority Priority,
    int MaximumMessageBytes,
    bool AllowsParallelLanes = false);

/// <summary>Single source of truth for the native data-plane channel contract.</summary>
public static class RemoteTransportChannels
{
    private static readonly IReadOnlyDictionary<RemoteTransportChannel, RemoteTransportChannelDefinition> Definitions =
        new Dictionary<RemoteTransportChannel, RemoteTransportChannelDefinition>
        {
            [RemoteTransportChannel.Mouse] = new(
                RemoteTransportChannel.Mouse,
                RemoteTransportDelivery.ReliableOrdered,
                RemoteTransportPriority.Interactive,
                4 * 1024),
            [RemoteTransportChannel.Keyboard] = new(
                RemoteTransportChannel.Keyboard,
                RemoteTransportDelivery.ReliableOrdered,
                RemoteTransportPriority.Interactive,
                4 * 1024),
            [RemoteTransportChannel.Control] = new(
                RemoteTransportChannel.Control,
                RemoteTransportDelivery.ReliableOrdered,
                RemoteTransportPriority.Control,
                256 * 1024),
            [RemoteTransportChannel.Screen] = new(
                RemoteTransportChannel.Screen,
                RemoteTransportDelivery.UnreliableLatest,
                RemoteTransportPriority.RealtimeMedia,
                64 * 1024),
            [RemoteTransportChannel.Audio] = new(
                RemoteTransportChannel.Audio,
                RemoteTransportDelivery.UnreliableLatest,
                RemoteTransportPriority.RealtimeMedia,
                16 * 1024),
            [RemoteTransportChannel.Clipboard] = new(
                RemoteTransportChannel.Clipboard,
                RemoteTransportDelivery.ReliableOrdered,
                RemoteTransportPriority.Clipboard,
                4 * 1024 * 1024),
            [RemoteTransportChannel.FileTransfer] = new(
                RemoteTransportChannel.FileTransfer,
                RemoteTransportDelivery.ReliableOrdered,
                RemoteTransportPriority.Background,
                1024 * 1024,
                AllowsParallelLanes: true),
            [RemoteTransportChannel.Telemetry] = new(
                RemoteTransportChannel.Telemetry,
                RemoteTransportDelivery.ReliableOrdered,
                RemoteTransportPriority.Background,
                64 * 1024),
        };

    public static IReadOnlyCollection<RemoteTransportChannelDefinition> All { get; } =
        Definitions.Values.ToArray();

    public static RemoteTransportChannelDefinition Get(RemoteTransportChannel channel) =>
        Definitions.TryGetValue(channel, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(channel));
}

public sealed record RemoteTransportCapabilities
{
    public required RemoteSessionTransportKind Kind { get; init; }
    public required int ProtocolVersion { get; init; }
    public bool SupportsReliableStreams { get; init; }
    public bool SupportsUnreliableDatagrams { get; init; }
    public bool SupportsPathMigration { get; init; }
}

/// <summary>
/// One reliable ordered lane. File transfer may open several lanes; every other channel has one
/// default lane in each direction.
/// </summary>
public interface IRemoteTransportLane : IAsyncDisposable
{
    RemoteTransportChannel Channel { get; }
    ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}

/// <summary>Reliable portion of a native remote-session connection.</summary>
public interface IReliableRemoteSessionTransport : IAsyncDisposable
{
    EndPoint LocalEndPoint { get; }
    EndPoint RemoteEndPoint { get; }
    RemoteTransportCapabilities Capabilities { get; }

    ValueTask SendAsync(
        RemoteTransportChannel channel,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
        RemoteTransportChannel channel,
        CancellationToken cancellationToken = default);

    ValueTask<IRemoteTransportLane> OpenLaneAsync(
        RemoteTransportChannel channel,
        CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates one ephemeral, certificate-pinned reliable endpoint for an authenticated session.
/// The certificate fingerprint and listener port are exchanged only inside the existing protected
/// collaboration session; implementations never place certificate keys or peer addresses in logs.
/// </summary>
public interface INativeBulkTransportFactory
{
    bool IsSupported { get; }

    ValueTask<INativeBulkTransportEndpoint> CreateEndpointAsync(
        CancellationToken cancellationToken = default);
}

public interface INativeBulkTransportEndpoint : IAsyncDisposable
{
    string CertificateSha256 { get; }

    ValueTask<INativeBulkTransportListener> ListenAsync(
        IPAddress localAddress,
        string expectedPeerCertificateSha256,
        CancellationToken cancellationToken = default);

    ValueTask<IReliableRemoteSessionTransport> ConnectAsync(
        IPEndPoint remoteEndPoint,
        string expectedPeerCertificateSha256,
        CancellationToken cancellationToken = default);
}

public interface INativeBulkTransportListener : IAsyncDisposable
{
    IPEndPoint LocalEndPoint { get; }

    ValueTask<IReliableRemoteSessionTransport> AcceptAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Unreliable latest-first media boundary. The managed .NET QUIC adapter does not implement this
/// interface because System.Net.Quic does not expose QUIC DATAGRAM; the native MsQuic adapter must.
/// </summary>
public interface IUnreliableRemoteSessionTransport
{
    ValueTask SendLatestAsync(
        RemoteTransportChannel channel,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadLatestAsync(
        RemoteTransportChannel channel,
        CancellationToken cancellationToken = default);
}
