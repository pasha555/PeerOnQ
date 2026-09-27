using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace PeerOnQ.Signaling.Server.Security;

/// <summary>Issues and validates short-lived connection tokens (HMAC, server-side key only).</summary>
public sealed class TokenService(IOptions<SignalingOptions> options, TimeProvider? timeProvider = null)
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public (string Token, DateTimeOffset ExpiresAt) Issue(string deviceId)
    {
        var expires = _time.GetUtcNow() + options.Value.ConnectionTokenLifetime;
        var payload = $"{deviceId}|{expires.ToUnixTimeSeconds()}";
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload));
        return ($"{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))}.{Convert.ToBase64String(mac)}", expires);
    }

    public bool Validate(string? token, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 2) return false;

        byte[] payloadBytes, macBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(parts[0]);
            macBytes = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(_key, payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, macBytes)) return false;

        var payload = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (payload.Length != 2 || payload[0] != deviceId) return false;
        if (!long.TryParse(payload[1], out var expiresUnix)) return false;

        return DateTimeOffset.FromUnixTimeSeconds(expiresUnix) > _time.GetUtcNow();
    }
}

/// <summary>
/// Trust-on-first-use registry of device public keys. The first successful registration pins
/// the key; later registrations for that PeerOnQ ID must present the same key.
/// </summary>
public sealed class DevicePublicKeyRegistry
{
    private readonly ConcurrentDictionary<string, string> _pinned;
    private readonly IDevicePinStore _store;

    public DevicePublicKeyRegistry(IDevicePinStore? store = null)
    {
        _store = store ?? new InMemoryDevicePinStore();
        _pinned = new ConcurrentDictionary<string, string>(_store.Load(), StringComparer.Ordinal);
    }

    /// <summary>
    /// First registration for an id pins its key; later ones must match. The pin survives a
    /// server restart when a persistent store is configured.
    /// </summary>
    public bool TryPinOrMatch(string deviceId, string publicKeyBase64)
    {
        if (_pinned.TryAdd(deviceId, publicKeyBase64))
        {
            // Newly pinned: persist immediately so a crash cannot lose it.
            _store.Save(_pinned);
            return true;
        }

        return _pinned.TryGetValue(deviceId, out var current)
               && string.Equals(current, publicKeyBase64, StringComparison.Ordinal);
    }

    /// <summary>Removes a pin, e.g. after a device is legitimately re-provisioned.</summary>
    public bool Unpin(string deviceId)
    {
        var removed = _pinned.TryRemove(deviceId, out _);
        if (removed) _store.Save(_pinned);
        return removed;
    }

    public bool VerifySignature(string publicKeyBase64, string challenge, string signatureBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(challenge),
                Convert.FromBase64String(signatureBase64),
                HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    public int Count => _pinned.Count;
}

/// <summary>Rejects replayed session-request nonces inside the configured window.</summary>
public interface ISessionRequestReplayGuard
{
    ValueTask<bool> TryRegisterAsync(
        string deviceId,
        string nonce,
        CancellationToken cancellationToken = default);
    bool IsWithinClockSkew(DateTimeOffset sentAt);
}

public sealed class ReplayGuard(IOptions<SignalingOptions> options, TimeProvider? timeProvider = null)
    : ISessionRequestReplayGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public bool TryRegister(string deviceId, string nonce)
    {
        Sweep();
        var key = $"{deviceId}:{nonce}";
        return _seen.TryAdd(key, _time.GetUtcNow() + options.Value.NonceLifetime);
    }

    public bool IsWithinClockSkew(DateTimeOffset sentAt) =>
        (_time.GetUtcNow() - sentAt).Duration() <= options.Value.MaxRequestClockSkew;

    public ValueTask<bool> TryRegisterAsync(
        string deviceId,
        string nonce,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryRegister(deviceId, nonce));
    }

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, expiry) in _seen)
        {
            if (expiry <= now)
            {
                _seen.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>Per-connection token bucket. Protects the server from a chatty or hostile client.</summary>
public sealed class MessageRateLimiter(int messagesPerSecond, int burst, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private double _tokens = burst;
    private DateTimeOffset _last = (timeProvider ?? TimeProvider.System).GetUtcNow();

    public bool TryConsume()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var elapsed = (now - _last).TotalSeconds;
            _last = now;

            _tokens = Math.Min(burst, _tokens + (elapsed * messagesPerSecond));
            if (_tokens < 1) return false;

            _tokens -= 1;
            return true;
        }
    }
}
