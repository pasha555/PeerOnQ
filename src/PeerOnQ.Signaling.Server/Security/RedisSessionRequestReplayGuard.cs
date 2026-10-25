using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace PeerOnQ.Signaling.Server.Security;

public sealed class RedisSessionRequestReplayGuard(
    IConnectionMultiplexer redis,
    IOptions<SignalingOptions> options,
    TimeProvider timeProvider) : ISessionRequestReplayGuard
{
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly SignalingOptions _options = options.Value;

    public bool IsWithinClockSkew(DateTimeOffset sentAt) =>
        (timeProvider.GetUtcNow() - sentAt).Duration() <= _options.MaxRequestClockSkew;

    public async ValueTask<bool> TryRegisterAsync(
        string deviceId,
        string nonce,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PeerOnQ.Domain.Identity.PeerOnQId.IsValid(deviceId)
            || string.IsNullOrWhiteSpace(nonce)
            || nonce.Length > 128)
            return false;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId}\n{nonce}"));
        var key = $"{_options.Cluster.KeyPrefix}:replay:{Convert.ToHexString(digest)}";
        CryptographicOperations.ZeroMemory(digest);
        return await _database.StringSetAsync(
            key,
            "1",
            _options.NonceLifetime,
            When.NotExists);
    }
}
