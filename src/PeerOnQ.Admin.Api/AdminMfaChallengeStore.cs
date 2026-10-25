using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace PeerOnQ.Admin.Api;

public sealed record AdminMfaChallenge(
    Guid UserId,
    string ContextHash,
    DateTimeOffset ExpiresAtUtc);

public interface IAdminMfaChallengeStore
{
    Task<string> CreateAsync(Guid userId, string contextHash, CancellationToken cancellationToken);
    Task<AdminMfaChallenge?> ConsumeAsync(string challengeId, string contextHash, CancellationToken cancellationToken);
}

public sealed class RedisAdminMfaChallengeStore(
    IConnectionMultiplexer redis,
    IOptions<AdminAuthenticationOptions> options,
    TimeProvider timeProvider) : IAdminMfaChallengeStore
{
    private const string ConsumeScript = "local value = redis.call('GET', KEYS[1]); if value then redis.call('DEL', KEYS[1]); end; return value";

    public async Task<string> CreateAsync(Guid userId, string contextHash, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(contextHash)) throw new ArgumentException("MFA challenge context is invalid.");
        var challengeId = WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var lifetime = TimeSpan.FromMinutes(options.Value.MfaChallengeMinutes);
        var value = JsonSerializer.Serialize(new AdminMfaChallenge(userId, contextHash, timeProvider.GetUtcNow() + lifetime));
        var stored = await redis.GetDatabase().StringSetAsync(
            Key(challengeId),
            value,
            lifetime,
            When.NotExists).WaitAsync(cancellationToken);
        if (!stored) throw new InvalidOperationException("A unique MFA challenge could not be created.");
        return challengeId;
    }

    public async Task<AdminMfaChallenge?> ConsumeAsync(string challengeId, string contextHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(challengeId) || challengeId.Length > 128 || string.IsNullOrWhiteSpace(contextHash)) return null;
        var result = await redis.GetDatabase().ScriptEvaluateAsync(
            ConsumeScript,
            [Key(challengeId)],
            []).WaitAsync(cancellationToken);
        if (result.IsNull) return null;
        try
        {
            var challenge = JsonSerializer.Deserialize<AdminMfaChallenge>((string)result!);
            return challenge is not null
                && challenge.ExpiresAtUtc > timeProvider.GetUtcNow()
                && CryptographicEquals(challenge.ContextHash, contextHash)
                    ? challenge
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RedisKey Key(string challengeId) => $"peeronq:{{admin}}:mfa:{challengeId}";

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
