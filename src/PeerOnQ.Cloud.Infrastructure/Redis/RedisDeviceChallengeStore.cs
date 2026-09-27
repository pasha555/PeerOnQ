using System.Text.Json;
using PeerOnQ.Cloud.Application.Abstractions;
using StackExchange.Redis;

namespace PeerOnQ.Cloud.Infrastructure.Redis;

public sealed class RedisDeviceChallengeStore(IConnectionMultiplexer redis, RedisKeySpace keySpace) : IDeviceChallengeStore
{
    public async Task StoreAsync(DeviceChallengeRecord challenge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        keySpace.Validate();
        var lifetime = challenge.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(challenge), "Challenge is already expired.");
        var stored = await redis.GetDatabase().StringSetAsync(
            Key(challenge.ChallengeId),
            JsonSerializer.Serialize(challenge),
            lifetime,
            When.NotExists);
        if (!stored) throw new InvalidOperationException("Challenge ID collision detected.");
    }

    public async Task<DeviceChallengeRecord?> ConsumeAsync(string challengeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(challengeId) || challengeId.Length > 128) return null;
        var value = await redis.GetDatabase().StringGetDeleteAsync(Key(challengeId));
        if (value.IsNullOrEmpty) return null;
        return JsonSerializer.Deserialize<DeviceChallengeRecord>((string)value!);
    }

    private RedisKey Key(string challengeId) => $"{keySpace.Prefix}:challenge:{challengeId}";
}
