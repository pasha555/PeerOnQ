using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace PeerOnQ.Cloud.Infrastructure.Redis;

public interface IDistributedOperationLease : IAsyncDisposable
{
    CancellationToken LeaseLost { get; }
}

public interface IDistributedOperationLeaseManager
{
    Task<IDistributedOperationLease?> TryAcquireAsync(
        string operation,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
}

/// <summary>
/// Short-lived renewable Redis ownership for singleton background operations. The random owner
/// token is never logged, and compare-and-renew/delete scripts prevent one replica from extending
/// or releasing another replica's lease.
/// </summary>
public sealed class RedisDistributedOperationLeaseManager(
    IConnectionMultiplexer redis,
    RedisKeySpace keySpace,
    ILogger<RedisDistributedOperationLeaseManager> logger) : IDistributedOperationLeaseManager
{
    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """;
    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    public async Task<IDistributedOperationLease?> TryAcquireAsync(
        string operation,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        Validate(operation, leaseDuration);
        cancellationToken.ThrowIfCancellationRequested();
        var key = (RedisKey)$"{keySpace.Prefix}:operation-lease:{operation}";
        var owner = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var acquired = await redis.GetDatabase()
            .StringSetAsync(key, owner, leaseDuration, When.NotExists)
            .WaitAsync(cancellationToken);
        return acquired
            ? new Lease(redis.GetDatabase(), key, owner, leaseDuration, operation, logger)
            : null;
    }

    private static void Validate(string operation, TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 64
            || operation.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '-' or '_')))
        {
            throw new ArgumentException("Distributed operation name is invalid.", nameof(operation));
        }

        if (leaseDuration < TimeSpan.FromSeconds(30) || leaseDuration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    }

    private sealed class Lease : IDistributedOperationLease
    {
        private readonly IDatabase _database;
        private readonly RedisKey _key;
        private readonly RedisValue _owner;
        private readonly TimeSpan _duration;
        private readonly string _operation;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stopRenewal = new();
        private readonly CancellationTokenSource _leaseLost = new();
        private readonly Task _renewal;
        private int _disposed;

        public Lease(
            IDatabase database,
            RedisKey key,
            RedisValue owner,
            TimeSpan duration,
            string operation,
            ILogger logger)
        {
            _database = database;
            _key = key;
            _owner = owner;
            _duration = duration;
            _operation = operation;
            _logger = logger;
            _renewal = RenewAsync();
        }

        public CancellationToken LeaseLost => _leaseLost.Token;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await _stopRenewal.CancelAsync();
            try
            {
                await _renewal;
            }
            catch (OperationCanceledException)
            {
                // Expected when the owner completes before the next renewal.
            }

            try
            {
                await _database.ScriptEvaluateAsync(ReleaseScript, [_key], [_owner])
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is RedisException or TimeoutException)
            {
                _logger.LogWarning(
                    "Distributed operation lease release failed for {Operation} with {ExceptionType}",
                    _operation,
                    exception.GetType().Name);
            }
            finally
            {
                _stopRenewal.Dispose();
                _leaseLost.Dispose();
            }
        }

        private async Task RenewAsync()
        {
            var interval = TimeSpan.FromTicks(_duration.Ticks / 3);
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(_stopRenewal.Token))
                {
                    var renewed = (long)await _database.ScriptEvaluateAsync(
                        RenewScript,
                        [_key],
                        [_owner, checked((long)_duration.TotalMilliseconds)]);
                    if (renewed == 1) continue;

                    await _leaseLost.CancelAsync();
                    _logger.LogWarning("Distributed operation lease was lost for {Operation}", _operation);
                    return;
                }
            }
            catch (OperationCanceledException) when (_stopRenewal.IsCancellationRequested)
            {
                // Normal disposal.
            }
            catch (Exception exception) when (exception is RedisException or TimeoutException)
            {
                await _leaseLost.CancelAsync();
                _logger.LogWarning(
                    "Distributed operation lease renewal failed for {Operation} with {ExceptionType}",
                    _operation,
                    exception.GetType().Name);
            }
        }
    }
}
