using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Cloud.Infrastructure.Redis;
using StackExchange.Redis;

namespace PeerOnQ.Observability;

public static class CloudDependencyHealthChecks
{
    public static IHealthChecksBuilder AddPeerOnQCloudDependencyHealthChecks(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<PostgresReadinessHealthCheck>(
                "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "startup"],
                timeout: TimeSpan.FromSeconds(5))
            .AddCheck<RedisReadinessHealthCheck>(
                "redis",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "startup"],
                timeout: TimeSpan.FromSeconds(3));

    public static IHealthChecksBuilder AddPeerOnQDeviceTokenValidationHealthCheck(this IServiceCollection services) =>
        services.AddHealthChecks()
            .AddCheck<DeviceTokenValidationRedisReadinessHealthCheck>(
                "redis-device-token-validation",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "startup"],
                timeout: TimeSpan.FromSeconds(3));
}

public sealed class PostgresReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            if (!await db.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("database_unavailable");

            if (!environment.IsEnvironment("Testing"))
            {
                var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
                if (pending.Any()) return HealthCheckResult.Unhealthy("database_migration_pending");
            }

            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("database_timeout");
        }
        catch
        {
            return HealthCheckResult.Unhealthy("database_unavailable");
        }
    }
}

public sealed class RedisReadinessHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("redis_timeout");
        }
        catch
        {
            return HealthCheckResult.Unhealthy("redis_unavailable");
        }
    }
}

public sealed class DeviceTokenValidationRedisReadinessHealthCheck(
    DeviceTokenValidationRedisConnection redis,
    RedisKeySpace keySpace) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.Connection.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            await redis.Connection.GetDatabase()
                .StringGetAsync($"{keySpace.Prefix}:device-token:readiness")
                .WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("device_token_validation_redis_timeout");
        }
        catch
        {
            return HealthCheckResult.Unhealthy("device_token_validation_redis_unavailable");
        }
    }
}
