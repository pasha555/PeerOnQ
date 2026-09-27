using System.Globalization;
using System.Text.Json;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using StackExchange.Redis;

namespace PeerOnQ.Cloud.Infrastructure.Redis;

public sealed class RedisPresenceLeaseStore(IConnectionMultiplexer redis, RedisKeySpace keySpace) : IPresenceLeaseStore
{
    private const string AcquireScript = """
        local previous = redis.call('GET', KEYS[1])
        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
        redis.call('SET', KEYS[2], ARGV[1], 'PX', ARGV[3])
        redis.call('ZADD', KEYS[3], ARGV[4], ARGV[5])
        redis.call('ZADD', KEYS[4], ARGV[4], ARGV[5])
        local top = redis.call('ZREVRANGE', KEYS[4], 0, 0, 'WITHSCORES')
        redis.call('ZADD', KEYS[5], top[2], ARGV[6])
        return previous
        """;

    private const string RefreshScript = """
        local current = redis.call('GET', KEYS[1])
        if not current then return false end
        local decoded = cjson.decode(current)
        if decoded.ConnectionId ~= ARGV[1] then return false end
        redis.call('SET', KEYS[1], ARGV[2], 'PX', ARGV[3])
        redis.call('SET', KEYS[2], ARGV[2], 'PX', ARGV[4])
        redis.call('ZADD', KEYS[3], ARGV[5], ARGV[6])
        redis.call('ZADD', KEYS[4], ARGV[5], ARGV[6])
        local top = redis.call('ZREVRANGE', KEYS[4], 0, 0, 'WITHSCORES')
        redis.call('ZADD', KEYS[5], top[2], ARGV[7])
        return ARGV[2]
        """;

    private const string ReleaseScript = """
        local current = redis.call('GET', KEYS[1])
        if not current then return false end
        local decoded = cjson.decode(current)
        if decoded.ConnectionId ~= ARGV[1] then return false end
        redis.call('DEL', KEYS[1])
        redis.call('DEL', KEYS[2])
        redis.call('ZREM', KEYS[3], ARGV[2])
        local device = string.gsub(decoded.DeviceId, '-', '')
        local deviceKey = ARGV[4] .. device
        redis.call('ZREM', deviceKey, ARGV[2])
        redis.call('ZREMRANGEBYSCORE', deviceKey, '-inf', ARGV[3])
        local top = redis.call('ZREVRANGE', deviceKey, 0, 0, 'WITHSCORES')
        if top[2] then
          redis.call('ZADD', KEYS[4], top[2], device)
        else
          redis.call('ZREM', KEYS[4], device)
          redis.call('DEL', deviceKey)
        end
        return current
        """;

    private const string CollectExpiredScript = """
        local ids = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, ARGV[2])
        local leases = {}
        for _, id in ipairs(ids) do
          local shadow = redis.call('GET', ARGV[3] .. id)
          if shadow then
            table.insert(leases, shadow)
            local decoded = cjson.decode(shadow)
            local device = string.gsub(decoded.DeviceId, '-', '')
            local deviceKey = ARGV[5] .. device
            redis.call('ZREM', deviceKey, id)
            redis.call('ZREMRANGEBYSCORE', deviceKey, '-inf', ARGV[1])
            local top = redis.call('ZREVRANGE', deviceKey, 0, 0, 'WITHSCORES')
            if top[2] then
              redis.call('ZADD', KEYS[2], top[2], device)
            else
              redis.call('ZREM', KEYS[2], device)
              redis.call('DEL', deviceKey)
            end
          end
          redis.call('DEL', ARGV[4] .. id)
          redis.call('DEL', ARGV[3] .. id)
          redis.call('ZREM', KEYS[1], id)
        end
        return leases
        """;

    public async Task<PresenceLeaseAcquireResult> AcquireAsync(PresenceLeaseRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        keySpace.Validate();
        var lease = new PresenceLease(request.InstallationId, request.DeviceId,
            request.ConnectionId.Trim(), request.ServerId.Trim(), request.State,
            request.AppVersion.Trim(), request.Region.Trim(), request.NowUtc, request.NowUtc,
            request.NowUtc + request.LeaseDuration);
        var serialized = JsonSerializer.Serialize(lease);
        var database = redis.GetDatabase();
        var result = await database.ScriptEvaluateAsync(AcquireScript,
            [LiveKey(request.InstallationId), ShadowKey(request.InstallationId), IndexKey, DeviceInstallationsKey(request.DeviceId), DeviceExpiryIndexKey],
            [serialized, Milliseconds(request.LeaseDuration), Milliseconds(request.LeaseDuration + keySpace.PresenceShadowRetention), Score(lease.ExpiresAtUtc), request.InstallationId.ToString("N"), request.DeviceId.ToString("N")]);
        var displaced = Deserialize(result);
        return new PresenceLeaseAcquireResult(lease, displaced?.ConnectionId, displaced?.ServerId, displaced);
    }

    public async Task<PresenceLease?> RefreshAsync(Guid installationId, string connectionId, PresenceState state, DateTimeOffset nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = await GetAsync(installationId, cancellationToken);
        if (current is null || !string.Equals(current.ConnectionId, connectionId, StringComparison.Ordinal)) return null;
        var refreshed = current with { State = state, LastHeartbeatAtUtc = nowUtc, ExpiresAtUtc = nowUtc + leaseDuration };
        var result = await redis.GetDatabase().ScriptEvaluateAsync(RefreshScript,
            [LiveKey(installationId), ShadowKey(installationId), IndexKey, DeviceInstallationsKey(refreshed.DeviceId), DeviceExpiryIndexKey],
            [connectionId, JsonSerializer.Serialize(refreshed), Milliseconds(leaseDuration), Milliseconds(leaseDuration + keySpace.PresenceShadowRetention), Score(refreshed.ExpiresAtUtc), installationId.ToString("N"), refreshed.DeviceId.ToString("N")]);
        return Deserialize(result);
    }

    public async Task<PresenceLease?> ReleaseAsync(Guid installationId, string connectionId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript,
            [LiveKey(installationId), ShadowKey(installationId), IndexKey, DeviceExpiryIndexKey],
            [connectionId, installationId.ToString("N"), Score(nowUtc), $"{keySpace.Prefix}:presence-device:"]);
        return Deserialize(result);
    }

    public async Task<PresenceLease?> GetAsync(Guid installationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await redis.GetDatabase().StringGetAsync(LiveKey(installationId));
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<PresenceLease>((string)value!);
    }

    public Task<long> CountActiveAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return redis.GetDatabase().SortedSetLengthAsync(IndexKey, Score(nowUtc), double.PositiveInfinity, Exclude.Start);
    }

    public Task<long> CountActiveDevicesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return redis.GetDatabase().SortedSetLengthAsync(DeviceExpiryIndexKey, Score(nowUtc), double.PositiveInfinity, Exclude.Start);
    }

    public async Task<IReadOnlyList<PresenceLease>> QueryActiveAsync(DateTimeOffset nowUtc, int offset, int limit, bool descending, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0 || limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        var order = descending ? Order.Descending : Order.Ascending;
        var ids = await redis.GetDatabase().SortedSetRangeByScoreAsync(IndexKey, Score(nowUtc), double.PositiveInfinity, Exclude.Start, order, offset, limit);
        if (ids.Length == 0) return [];
        var values = await redis.GetDatabase().StringGetAsync(ids.Select(value => LiveKey(Guid.ParseExact((string)value!, "N"))).ToArray());
        return values.Where(value => !value.IsNullOrEmpty).Select(value => JsonSerializer.Deserialize<PresenceLease>((string)value!)!).ToArray();
    }

    public async Task<IReadOnlyList<PresenceLease>> CollectExpiredAsync(DateTimeOffset nowUtc, int maximumCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        var result = await redis.GetDatabase().ScriptEvaluateAsync(CollectExpiredScript,
            [IndexKey, DeviceExpiryIndexKey],
            [Score(nowUtc), maximumCount, $"{keySpace.Prefix}:presence-shadow:", $"{keySpace.Prefix}:presence:", $"{keySpace.Prefix}:presence-device:"]);
        var values = (RedisResult[]?)result;
        return values is null ? [] : values.Select(Deserialize).Where(value => value is not null).Cast<PresenceLease>().ToArray();
    }

    private static void Validate(PresenceLeaseRequest request)
    {
        if (request.InstallationId == Guid.Empty || request.DeviceId == Guid.Empty) throw new ArgumentException("Presence identity is required.");
        if (string.IsNullOrWhiteSpace(request.ConnectionId) || request.ConnectionId.Length > 128) throw new ArgumentException("Connection ID is invalid.");
        if (string.IsNullOrWhiteSpace(request.ServerId) || request.ServerId.Length > 128) throw new ArgumentException("Server ID is invalid.");
        if (string.IsNullOrWhiteSpace(request.AppVersion) || request.AppVersion.Length > 64) throw new ArgumentException("Application version is invalid.");
        if (string.IsNullOrWhiteSpace(request.Region) || request.Region.Length > 64) throw new ArgumentException("Region is invalid.");
        if (request.LeaseDuration < TimeSpan.FromSeconds(20) || request.LeaseDuration > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(request.LeaseDuration));
    }

    private PresenceLease? Deserialize(RedisResult result)
    {
        if (result.IsNull) return null;
        var value = (string?)result;
        return string.IsNullOrEmpty(value) ? null : JsonSerializer.Deserialize<PresenceLease>(value);
    }

    private RedisKey LiveKey(Guid id) => $"{keySpace.Prefix}:presence:{id:N}";
    private RedisKey ShadowKey(Guid id) => $"{keySpace.Prefix}:presence-shadow:{id:N}";
    private RedisKey DeviceInstallationsKey(Guid deviceId) => $"{keySpace.Prefix}:presence-device:{deviceId:N}";
    private RedisKey IndexKey => $"{keySpace.Prefix}:presence-expiry";
    private RedisKey DeviceExpiryIndexKey => $"{keySpace.Prefix}:presence-device-expiry";
    private static long Milliseconds(TimeSpan value) => checked((long)value.TotalMilliseconds);
    private static double Score(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
}
