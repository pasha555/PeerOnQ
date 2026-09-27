using System.Security.Cryptography;
using System.Text.Json;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Application.Security;
using StackExchange.Redis;

namespace PeerOnQ.Cloud.Infrastructure.Redis;

public class RedisDeviceAccessTokenValidator(
    IConnectionMultiplexer redis,
    RedisKeySpace keySpace,
    TimeProvider? timeProvider = null) : IDeviceAccessTokenValidator
{
    protected IConnectionMultiplexer Redis { get; } = redis;
    protected RedisKeySpace KeySpace { get; } = keySpace;
    protected TimeProvider Time { get; } = timeProvider ?? TimeProvider.System;

    public async Task<DeviceAccessPrincipal?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryDecode(token, out var raw)) return null;
        var value = await Redis.GetDatabase().StringGetAsync(Key(raw));
        if (value.IsNullOrEmpty) return null;
        var principal = JsonSerializer.Deserialize<DeviceAccessPrincipal>((string)value!);
        return principal is not null && principal.ExpiresAtUtc > Time.GetUtcNow() ? principal : null;
    }

    protected RedisKey Key(ReadOnlySpan<byte> rawToken) => $"{KeySpace.Prefix}:device-token:{Convert.ToHexString(SHA256.HashData(rawToken)).ToLowerInvariant()}";

    protected static bool TryDecode(string token, out byte[] raw)
    {
        raw = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return false;
        try
        {
            var normalized = token.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            raw = Convert.FromBase64String(normalized);
            return raw.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class RedisDeviceAccessTokenIssuer(
    IConnectionMultiplexer redis,
    RedisKeySpace keySpace,
    TimeProvider? timeProvider = null)
    : RedisDeviceAccessTokenValidator(redis, keySpace, timeProvider), IDeviceAccessTokenIssuer
{
    public async Task<DeviceAccessToken> IssueAsync(Guid deviceId, Guid installationId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deviceId == Guid.Empty || installationId == Guid.Empty || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = CanonicalDeviceChallenge.Base64Url(raw);
        var principal = new DeviceAccessPrincipal(deviceId, installationId, Time.GetUtcNow() + lifetime);
        await Redis.GetDatabase().StringSetAsync(Key(raw), JsonSerializer.Serialize(principal), lifetime, When.NotExists);
        return new DeviceAccessToken(token, principal.ExpiresAtUtc);
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryDecode(token, out var raw)) await Redis.GetDatabase().KeyDeleteAsync(Key(raw));
    }
}
