using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeerOnQ.Transport.Protocol;
using StackExchange.Redis;

namespace PeerOnQ.Signaling.Server.Registry;

public sealed record DeviceRoute(
    string DeviceId,
    string InstanceId,
    string ConnectionId,
    string DisplayName,
    string PublicKeyFingerprint,
    Guid? OrganizationId,
    int OrganizationPolicyFlags,
    string ApprovedRelayRegionsCsv,
    ClientCapabilityManifest ClientCapabilities);

public sealed record RoutedDeviceCommand(
    string Kind,
    string DeviceId,
    string ConnectionId,
    string? PayloadBase64 = null,
    string? Reason = null);

public sealed record ExpiredDeviceOwner(
    string DeviceId,
    string InstanceId,
    string ConnectionId);

public interface ISignalingBackplane
{
    bool IsEnabled { get; }
    bool IsReady { get; }
    event Func<RoutedDeviceCommand, ValueTask>? CommandReceived;
    ValueTask<DeviceRoute?> RegisterOwnerAsync(DeviceRoute route, CancellationToken cancellationToken);
    ValueTask<bool> RefreshOwnerAsync(DeviceRoute route, CancellationToken cancellationToken);
    ValueTask RemoveOwnerAsync(DeviceRoute route, CancellationToken cancellationToken);
    ValueTask<DeviceRoute?> ResolveOwnerAsync(string deviceId, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ExpiredDeviceOwner>> TakeExpiredOwnersAsync(
        int maximumCount,
        CancellationToken cancellationToken);
    ValueTask<bool> PublishAsync(string instanceId, RoutedDeviceCommand command, CancellationToken cancellationToken);
}

public sealed class LocalSignalingBackplane : ISignalingBackplane
{
    public bool IsEnabled => false;
    public bool IsReady => true;
    public event Func<RoutedDeviceCommand, ValueTask>? CommandReceived
    {
        add { }
        remove { }
    }

    public ValueTask<DeviceRoute?> RegisterOwnerAsync(DeviceRoute route, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DeviceRoute?>(null);

    public ValueTask<bool> RefreshOwnerAsync(DeviceRoute route, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    public ValueTask RemoveOwnerAsync(DeviceRoute route, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<DeviceRoute?> ResolveOwnerAsync(string deviceId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DeviceRoute?>(null);

    public ValueTask<IReadOnlyList<ExpiredDeviceOwner>> TakeExpiredOwnersAsync(
        int maximumCount,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<ExpiredDeviceOwner>>(Array.Empty<ExpiredDeviceOwner>());

    public ValueTask<bool> PublishAsync(
        string instanceId,
        RoutedDeviceCommand command,
        CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

public sealed class RedisSignalingBackplane(
    IConnectionMultiplexer redis,
    IOptions<SignalingOptions> options,
    ILogger<RedisSignalingBackplane> logger) : ISignalingBackplane, IHostedService, IAsyncDisposable
{
    private const int MaxEnvelopeBytes = SignalingCodec.MaxFrameBytes * 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly LuaScript ReplaceOwnerScript = LuaScript.Prepare(
        "local previous = redis.call('GET', @key); " +
        "redis.call('SET', @key, @value, 'PX', @ttl); " +
        "local now = redis.call('TIME'); " +
        "local expires = (now[1] * 1000) + math.floor(now[2] / 1000) + @ttl; " +
        "redis.call('ZADD', @expiryIndex, expires, @ownerMember); return previous");
    private static readonly LuaScript RefreshOwnerScript = LuaScript.Prepare(
        "if redis.call('GET', @key) == @value then " +
        "redis.call('PEXPIRE', @key, @ttl); " +
        "local now = redis.call('TIME'); " +
        "local expires = (now[1] * 1000) + math.floor(now[2] / 1000) + @ttl; " +
        "redis.call('ZADD', @expiryIndex, expires, @ownerMember); return 1 else return 0 end");
    private static readonly LuaScript RemoveOwnerScript = LuaScript.Prepare(
        "if redis.call('GET', @key) == @value then " +
        "redis.call('DEL', @key); redis.call('ZREM', @expiryIndex, @ownerMember); " +
        "return 1 else return 0 end");
    private static readonly LuaScript TakeExpiredOwnersScript = LuaScript.Prepare(
        "local now = redis.call('TIME'); " +
        "local current = (now[1] * 1000) + math.floor(now[2] / 1000); " +
        "local values = redis.call('ZRANGEBYSCORE', @expiryIndex, '-inf', current, 'LIMIT', 0, @limit); " +
        "for _, value in ipairs(values) do redis.call('ZREM', @expiryIndex, value) end; return values");

    private readonly SignalingClusterOptions _cluster = options.Value.Cluster;
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly ISubscriber _subscriber = redis.GetSubscriber();
    private ChannelMessageQueue? _subscription;

    public bool IsEnabled => true;
    public bool IsReady => redis.IsConnected && _subscription is not null;
    public event Func<RoutedDeviceCommand, ValueTask>? CommandReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.PingAsync();
        _subscription = await _subscriber.SubscribeAsync(RouteChannel(_cluster.InstanceId));
        _subscription.OnMessage(DispatchAsync);
        logger.LogInformation("Signaling instance {InstanceId} joined the distributed routing backplane", _cluster.InstanceId);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is null) return;
        await _subscription.UnsubscribeAsync();
        _subscription = null;
    }

    public async ValueTask<DeviceRoute?> RegisterOwnerAsync(
        DeviceRoute route,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serialized = Serialize(route);
        var result = await _database.ScriptEvaluateAsync(
            ReplaceOwnerScript,
            new
            {
                key = (RedisKey)OwnerKey(route.DeviceId),
                value = (RedisValue)serialized,
                ttl = (long)_cluster.DeviceLeaseDuration.TotalMilliseconds,
                expiryIndex = (RedisKey)OwnerExpiryIndexKey(),
                ownerMember = (RedisValue)OwnerMember(route),
            });
        return result.IsNull ? null : DeserializeRoute((string)result!);
    }

    public async ValueTask<bool> RefreshOwnerAsync(DeviceRoute route, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _database.ScriptEvaluateAsync(
            RefreshOwnerScript,
            new
            {
                key = (RedisKey)OwnerKey(route.DeviceId),
                value = (RedisValue)Serialize(route),
                ttl = (long)_cluster.DeviceLeaseDuration.TotalMilliseconds,
                expiryIndex = (RedisKey)OwnerExpiryIndexKey(),
                ownerMember = (RedisValue)OwnerMember(route),
            });
        return (long)result > 0;
    }

    public async ValueTask RemoveOwnerAsync(DeviceRoute route, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _database.ScriptEvaluateAsync(
            RemoveOwnerScript,
            new
            {
                key = (RedisKey)OwnerKey(route.DeviceId),
                value = (RedisValue)Serialize(route),
                expiryIndex = (RedisKey)OwnerExpiryIndexKey(),
                ownerMember = (RedisValue)OwnerMember(route),
            });
    }

    public async ValueTask<DeviceRoute?> ResolveOwnerAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _database.StringGetAsync(OwnerKey(deviceId));
        return value.IsNullOrEmpty ? null : DeserializeRoute(value!);
    }

    public async ValueTask<IReadOnlyList<ExpiredDeviceOwner>> TakeExpiredOwnersAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = (RedisResult[]?)await _database.ScriptEvaluateAsync(
            TakeExpiredOwnersScript,
            new
            {
                expiryIndex = (RedisKey)OwnerExpiryIndexKey(),
                limit = Math.Clamp(maximumCount, 1, 1024),
            });
        if (result is null || result.Length == 0) return Array.Empty<ExpiredDeviceOwner>();

        var expired = new List<ExpiredDeviceOwner>(result.Length);
        foreach (var item in result)
        {
            var parsed = ParseOwnerMember((string?)item);
            if (parsed is not null) expired.Add(parsed);
        }

        return expired;
    }

    public async ValueTask<bool> PublishAsync(
        string instanceId,
        RoutedDeviceCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions);
        if (payload.Length > MaxEnvelopeBytes)
            throw new InvalidOperationException("The routed signaling envelope exceeds its bounded size.");
        return await _subscriber.PublishAsync(RouteChannel(instanceId), payload) > 0;
    }

    private async Task DispatchAsync(ChannelMessage channelMessage)
    {
        try
        {
            var payload = (byte[]?)channelMessage.Message;
            if (payload is null || payload.Length is 0 or > MaxEnvelopeBytes) return;
            var command = JsonSerializer.Deserialize<RoutedDeviceCommand>(payload, JsonOptions);
            if (command is null
                || string.IsNullOrWhiteSpace(command.DeviceId)
                || command.DeviceId.Length > 32
                || string.IsNullOrWhiteSpace(command.ConnectionId)
                || command.ConnectionId.Length > 128
                || command.Kind is not ("signal" or "close")
                || command.Reason?.Length > 64
                || (command.Kind == "signal" && string.IsNullOrWhiteSpace(command.PayloadBase64))) return;

            var handlers = CommandReceived;
            if (handlers is null) return;
            foreach (Func<RoutedDeviceCommand, ValueTask> handler in handlers.GetInvocationList())
                await handler(command);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Rejected a malformed distributed signaling command");
        }
    }

    private string OwnerKey(string deviceId) => $"{_cluster.KeyPrefix}:device:{deviceId}";
    private string OwnerExpiryIndexKey() => $"{_cluster.KeyPrefix}:device-owner-expiry";
    private RedisChannel RouteChannel(string instanceId) =>
        RedisChannel.Literal($"{_cluster.KeyPrefix}:route:{instanceId}");

    private static string OwnerMember(DeviceRoute route) =>
        $"{route.DeviceId}|{route.InstanceId}|{route.ConnectionId}";

    private static ExpiredDeviceOwner? ParseOwnerMember(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('|', 3, StringSplitOptions.None);
        return parts.Length == 3
               && parts[0].Length is > 0 and <= 32
               && parts[1].Length is > 0 and <= 64
               && parts[2].Length is > 0 and <= 128
            ? new ExpiredDeviceOwner(parts[0], parts[1], parts[2])
            : null;
    }

    private static string Serialize(DeviceRoute route) => JsonSerializer.Serialize(route, JsonOptions);

    private static DeviceRoute? DeserializeRoute(RedisValue value)
    {
        try
        {
            var route = JsonSerializer.Deserialize<DeviceRoute>((string)value!, JsonOptions);
            if (route is null
                || !PeerOnQ.Domain.Identity.PeerOnQId.TryParse(route.DeviceId, out _)
                || string.IsNullOrWhiteSpace(route.InstanceId)
                || route.InstanceId.Length > 64
                || route.InstanceId.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
                || string.IsNullOrWhiteSpace(route.ConnectionId)
                || route.ConnectionId.Length > 128
                || route.DisplayName is null or { Length: > 128 }
                || route.PublicKeyFingerprint is null or { Length: > 128 }
                || route.ApprovedRelayRegionsCsv is null or { Length: > 512 }
                || !CapabilityNegotiator.TryNormalize(route.ClientCapabilities, out var capabilities, out _))
                return null;

            return route with { ClientCapabilities = capabilities };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}
