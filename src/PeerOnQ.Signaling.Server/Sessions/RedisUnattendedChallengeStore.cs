using System.Text.Json;
using Microsoft.Extensions.Options;
using PeerOnQ.Domain.Identity;
using StackExchange.Redis;

namespace PeerOnQ.Signaling.Server.Sessions;

public sealed class RedisUnattendedChallengeStore(
    IConnectionMultiplexer redis,
    IOptions<SignalingOptions> options,
    TimeProvider timeProvider) : IUnattendedChallengeStore
{
    private const int MaxPendingChallenges = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly LuaScript CreateScript = LuaScript.Prepare(
        "redis.call('ZREMRANGEBYSCORE', @index, '-inf', @now); " +
        "if redis.call('ZCARD', @index) >= tonumber(@limit) then return 0 end; " +
        "if not redis.call('SET', @key, @value, 'PX', @ttl, 'NX') then return 0 end; " +
        "redis.call('ZADD', @index, @expiry, @requestId); return 1");
    private static readonly LuaScript ConsumeScript = LuaScript.Prepare(
        "local value = redis.call('GET', @key); if not value then return nil end; " +
        "redis.call('DEL', @key); redis.call('ZREM', @index, @requestId); return value");
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly string _prefix = options.Value.Cluster.KeyPrefix;

    public async ValueTask<bool> TryCreateAsync(
        string requestId,
        PeerOnQId requester,
        PeerOnQId target,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(requestId)
            || requestId.Length > 128
            || requester == target
            || lifetime <= TimeSpan.Zero
            || lifetime > TimeSpan.FromMinutes(5))
            return false;

        var now = timeProvider.GetUtcNow();
        var expiresAt = now + lifetime;
        var pending = new RedisPendingChallenge(requestId, requester.Value, target.Value, expiresAt);
        var result = await _database.ScriptEvaluateAsync(
            CreateScript,
            new
            {
                index = (RedisKey)IndexKey,
                key = (RedisKey)ChallengeKey(requestId),
                value = (RedisValue)JsonSerializer.Serialize(pending, JsonOptions),
                ttl = (long)lifetime.TotalMilliseconds,
                now = now.ToUnixTimeMilliseconds(),
                expiry = expiresAt.ToUnixTimeMilliseconds(),
                limit = MaxPendingChallenges,
                requestId = (RedisValue)requestId,
            });
        return (long)result == 1;
    }

    public async ValueTask<PendingUnattendedChallenge?> TryConsumeAsync(
        string requestId,
        PeerOnQId respondingTarget,
        PeerOnQId claimedRequester,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _database.ScriptEvaluateAsync(
            ConsumeScript,
            new
            {
                index = (RedisKey)IndexKey,
                key = (RedisKey)ChallengeKey(requestId),
                requestId = (RedisValue)requestId,
            });
        if (result.IsNull) return null;
        try
        {
            var pending = JsonSerializer.Deserialize<RedisPendingChallenge>((string)result!, JsonOptions);
            return pending is not null
                   && string.Equals(pending.RequestId, requestId, StringComparison.Ordinal)
                   && pending.ExpiresAt > timeProvider.GetUtcNow()
                   && string.Equals(pending.TargetId, respondingTarget.Value, StringComparison.Ordinal)
                   && string.Equals(pending.RequesterId, claimedRequester.Value, StringComparison.Ordinal)
                ? new PendingUnattendedChallenge(
                    pending.RequestId,
                    claimedRequester,
                    respondingTarget,
                    pending.ExpiresAt)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string ChallengeKey(string requestId) => $"{_prefix}:unattended:{requestId}";
    private string IndexKey => $"{_prefix}:unattended:index";

    private sealed record RedisPendingChallenge(
        string RequestId,
        string RequesterId,
        string TargetId,
        DateTimeOffset ExpiresAt);
}
