using System.Collections.Concurrent;
using System.Net.WebSockets;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Transport.Protocol;
using Microsoft.Extensions.Logging;
using PeerOnQ.Shared.Contracts.Security;
using Microsoft.Extensions.Options;

namespace PeerOnQ.Signaling.Server.Registry;

/// <summary>One live signaling socket.</summary>
public sealed class DeviceConnection(
    string connectionId,
    WebSocket socket,
    TimeProvider time,
    int maxPendingSends = 128)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _pendingSends;

    public string ConnectionId { get; } = connectionId;
    public WebSocket Socket { get; } = socket;
    public PeerOnQId? DeviceId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? PublicKeyFingerprint { get; set; }
    public string? Challenge { get; set; }
    public DateTimeOffset ChallengeExpiresAt { get; set; }
    public string? Token { get; set; }
    public Guid? OrganizationId { get; set; }
    public int OrganizationPolicyFlags { get; set; } = SignalingOrganizationPolicyFlags.Unmanaged;
    public string MinimumClientVersion { get; set; } = string.Empty;
    public string ApprovedRelayRegionsCsv { get; set; } = string.Empty;
    public ClientCapabilityManifest? ClientCapabilities { get; set; }
    public bool IsRegistered => DeviceId is not null;
    public DateTimeOffset LastSeen { get; private set; } = time.GetUtcNow();

    public string MaskedId => DeviceId?.Masked ?? "(unregistered)";

    public void Touch() => LastSeen = time.GetUtcNow();

    public async Task SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        var payload = SignalingCodec.Encode(message);
        await SendPayloadAsync(payload, WebSocketMessageType.Text, cancellationToken);
    }

    /// <summary>Sends an already validated opaque binary file record to this device.</summary>
    public Task SendFileRelayAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
        SendPayloadAsync(payload, WebSocketMessageType.Binary, cancellationToken);

    private async Task SendPayloadAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _pendingSends) > Math.Max(1, maxPendingSends))
        {
            Interlocked.Decrement(ref _pendingSends);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "outbound_backpressure");
            return;
        }

        try
        {
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                if (Socket.State != WebSocketState.Open) return;
                await Socket.SendAsync(payload, messageType, endOfMessage: true, cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // The peer vanished mid-write; the receive loop will clean the connection up.
            }
            finally
            {
                _sendGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSends);
        }
    }

    /// <summary>
    /// Sends the close frame without waiting for the peer's acknowledgement. Waiting here
    /// would let a wedged client stall an unrelated registration.
    /// </summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await Socket.CloseOutputAsync(status, reason, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// Maps a PeerOnQ ID to its single live connection. A second registration for the same ID
/// replaces the first, and the displaced connection is closed with a clear reason.
/// </summary>
public sealed class DeviceRegistry
{
    private readonly ConcurrentDictionary<string, DeviceConnection> _byDevice = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeviceConnection> _byConnection = new(StringComparer.Ordinal);
    private readonly ISignalingBackplane _backplane;
    private readonly ILogger<DeviceRegistry> _logger;
    private readonly string _instanceId;

    public DeviceRegistry(
        ISignalingBackplane backplane,
        IOptions<SignalingOptions> options,
        ILogger<DeviceRegistry> logger)
    {
        _backplane = backplane;
        _logger = logger;
        _instanceId = options.Value.Cluster.InstanceId;
        _backplane.CommandReceived += OnCommandReceivedAsync;
    }

    public int OnlineCount => _byDevice.Count;

    public void Add(DeviceConnection connection) => _byConnection[connection.ConnectionId] = connection;

    public async Task<DeviceConnection?> RegisterAsync(
        DeviceConnection connection,
        PeerOnQId deviceId,
        CancellationToken cancellationToken = default)
    {
        connection.DeviceId = deviceId;

        DeviceConnection? displaced = null;
        _byDevice.AddOrUpdate(
            deviceId.Value,
            connection,
            (_, existing) =>
            {
                if (!ReferenceEquals(existing, connection))
                {
                    displaced = existing;
                }

                return connection;
            });

        if (displaced is not null)
        {
            _logger.LogInformation(
                "Device {Device} registered from a new connection; closing the previous one", deviceId.Masked);
            await displaced.CloseAsync(WebSocketCloseStatus.NormalClosure, "replaced_by_new_connection");
        }

        if (_backplane.IsEnabled)
        {
            DeviceRoute? previous;
            try
            {
                previous = await _backplane.RegisterOwnerAsync(ToRoute(connection), cancellationToken);
            }
            catch
            {
                RemoveLocal(connection);
                throw;
            }

            if (previous is not null
                && (!string.Equals(previous.InstanceId, _instanceId, StringComparison.Ordinal)
                    || !string.Equals(previous.ConnectionId, connection.ConnectionId, StringComparison.Ordinal)))
            {
                await _backplane.PublishAsync(
                    previous.InstanceId,
                    new RoutedDeviceCommand(
                        "close",
                        previous.DeviceId,
                        previous.ConnectionId,
                        Reason: "replaced_by_new_connection"),
                    cancellationToken);
            }
        }

        return displaced;
    }

    public bool TryGet(PeerOnQId deviceId, out DeviceConnection connection) =>
        _byDevice.TryGetValue(deviceId.Value, out connection!);

    public bool IsOnline(PeerOnQId deviceId) =>
        _byDevice.TryGetValue(deviceId.Value, out var connection) && connection.Socket.State == WebSocketState.Open;

    public async ValueTask<DeviceRoute?> ResolveAsync(
        PeerOnQId deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!_backplane.IsEnabled)
            return TryGet(deviceId, out var local) && local.Socket.State == WebSocketState.Open
                ? ToRoute(local)
                : null;

        var route = await _backplane.ResolveOwnerAsync(deviceId.Value, cancellationToken);
        if (route is null) return null;
        if (!string.Equals(route.InstanceId, _instanceId, StringComparison.Ordinal)) return route;
        return _byConnection.TryGetValue(route.ConnectionId, out var owned)
               && owned.DeviceId == deviceId
               && owned.Socket.State == WebSocketState.Open
            ? route
            : null;
    }

    public async ValueTask<bool> IsOnlineAsync(
        PeerOnQId deviceId,
        CancellationToken cancellationToken = default) =>
        await ResolveAsync(deviceId, cancellationToken) is not null;

    public async ValueTask<bool> SendAsync(
        PeerOnQId deviceId,
        SignalingMessage message,
        CancellationToken cancellationToken = default)
    {
        var route = await ResolveAsync(deviceId, cancellationToken);
        if (route is null) return false;

        if (string.Equals(route.InstanceId, _instanceId, StringComparison.Ordinal))
        {
            if (!_byConnection.TryGetValue(route.ConnectionId, out var connection)
                || connection.DeviceId != deviceId
                || connection.Socket.State != WebSocketState.Open) return false;
            await connection.SendAsync(message, cancellationToken);
            return true;
        }

        return await _backplane.PublishAsync(
            route.InstanceId,
            new RoutedDeviceCommand(
                "signal",
                route.DeviceId,
                route.ConnectionId,
                Convert.ToBase64String(SignalingCodec.Encode(message))),
            cancellationToken);
    }

    public async ValueTask<bool> SendFileRelayAsync(
        PeerOnQId deviceId,
        ReadOnlyMemory<byte> frame,
        CancellationToken cancellationToken = default)
    {
        var route = await ResolveAsync(deviceId, cancellationToken);
        if (route is null) return false;

        if (string.Equals(route.InstanceId, _instanceId, StringComparison.Ordinal))
        {
            if (!_byConnection.TryGetValue(route.ConnectionId, out var connection)
                || connection.DeviceId != deviceId
                || connection.Socket.State != WebSocketState.Open) return false;
            await connection.SendFileRelayAsync(frame, cancellationToken);
            return true;
        }

        return await _backplane.PublishAsync(
            route.InstanceId,
            new RoutedDeviceCommand(
                "file",
                route.DeviceId,
                route.ConnectionId,
                Convert.ToBase64String(frame.Span)),
            cancellationToken);
    }

    public void Remove(DeviceConnection connection) =>
        RemoveAsync(connection, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public async ValueTask<bool> RemoveAsync(
        DeviceConnection connection,
        CancellationToken cancellationToken = default)
    {
        var removedCurrentOwner = RemoveLocal(connection);
        if (_backplane.IsEnabled && connection.DeviceId is not null)
            await _backplane.RemoveOwnerAsync(ToRoute(connection), cancellationToken);
        return removedCurrentOwner;
    }

    public IReadOnlyCollection<DeviceConnection> All() => _byConnection.Values.ToArray();

    public async Task RefreshDistributedOwnershipAsync(CancellationToken cancellationToken)
    {
        if (!_backplane.IsEnabled) return;

        foreach (var connection in All().Where(item => item.IsRegistered))
        {
            if (await _backplane.RefreshOwnerAsync(ToRoute(connection), cancellationToken)) continue;
            _logger.LogWarning(
                "Device {Device} lost distributed ownership; closing stale connection {ConnectionId}",
                connection.MaskedId,
                connection.ConnectionId);
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "ownership_lost");
            RemoveLocal(connection);
        }
    }

    private async ValueTask OnCommandReceivedAsync(RoutedDeviceCommand command)
    {
        if (!_byConnection.TryGetValue(command.ConnectionId, out var connection)
            || !string.Equals(connection.DeviceId?.Value, command.DeviceId, StringComparison.Ordinal)) return;

        if (command.Kind == "close")
        {
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, command.Reason ?? "ownership_lost");
            RemoveLocal(connection);
            return;
        }

        if (string.IsNullOrWhiteSpace(command.PayloadBase64)) return;
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(command.PayloadBase64);
        }
        catch (FormatException)
        {
            return;
        }

        if (command.Kind == "file")
        {
            if (payload.Length > FileRelayFrameCodec.MaxFrameBytes) return;
            await connection.SendFileRelayAsync(payload);
            return;
        }

        if (!SignalingCodec.TryDecode(payload, out var message, out _) || message is null) return;
        await connection.SendAsync(message);
    }

    private DeviceRoute ToRoute(DeviceConnection connection) => new(
        connection.DeviceId!.Value.Value,
        _instanceId,
        connection.ConnectionId,
        connection.DisplayName,
        connection.PublicKeyFingerprint ?? string.Empty,
        connection.OrganizationId,
        connection.OrganizationPolicyFlags,
        connection.ApprovedRelayRegionsCsv,
        connection.ClientCapabilities!);

    private bool RemoveLocal(DeviceConnection connection)
    {
        _byConnection.TryRemove(connection.ConnectionId, out _);
        if (connection.DeviceId is not { } deviceId) return false;

        // Remove only the exact connection that still owns this device. A new socket may have
        // replaced it while the old receive loop was unwinding; key-only removal would delete the
        // new owner and let the stale cleanup mark its live sessions disconnected.
        return ((ICollection<KeyValuePair<string, DeviceConnection>>)_byDevice).Remove(
            new KeyValuePair<string, DeviceConnection>(deviceId.Value, connection));
    }
}
