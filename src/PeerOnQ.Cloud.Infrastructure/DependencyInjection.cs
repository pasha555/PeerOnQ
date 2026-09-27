using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Application.Security;
using PeerOnQ.Cloud.Application.Services;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Cloud.Infrastructure.Redis;
using PeerOnQ.Cloud.Infrastructure.Workers;
using StackExchange.Redis;

namespace PeerOnQ.Cloud.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPeerOnQCloudInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var databaseConnection = configuration.GetConnectionString("Postgres")
            ?? configuration.GetConnectionString("CloudDatabase");
        if (string.IsNullOrWhiteSpace(databaseConnection))
            throw new InvalidOperationException("ConnectionStrings:Postgres is required (CloudDatabase remains a compatibility alias). ");
        var redisConnection = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redisConnection))
            throw new InvalidOperationException("ConnectionStrings:Redis is required for distributed challenge, token, and presence state.");

        var securityOptions = configuration.GetSection("CloudSecurity").Get<CloudSecurityOptions>() ?? new CloudSecurityOptions();
        securityOptions.Validate();
        var retentionOptions = configuration.GetSection("DataRetention").Get<RetentionOptions>() ?? new RetentionOptions();
        retentionOptions.Validate(securityOptions.DiagnosticUploadTokenLifetime);
        var redisKeySpace = configuration.GetSection("RedisKeySpace").Get<RedisKeySpace>() ?? new RedisKeySpace();
        var workerOptions = configuration.GetSection("CloudWorkers").Get<CloudWorkerOptions>() ?? new CloudWorkerOptions();
        redisKeySpace.Validate();
        if (workerOptions.EnablePresenceExpiration
            || workerOptions.EnableSessionReconciliation
            || workerOptions.EnableRetention)
            workerOptions.Validate(securityOptions.HeartbeatIntervalSeconds);

        services.AddSingleton(securityOptions);
        services.AddSingleton(retentionOptions);
        services.AddSingleton(redisKeySpace);
        services.AddSingleton(workerOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<CloudDbContext>(options => options.UseNpgsql(databaseConnection, npgsql =>
        {
            npgsql.MigrationsAssembly(typeof(CloudDbContext).Assembly.FullName);
            npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(2), null);
        }).EnableDetailedErrors(environment.IsDevelopment()));

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConnection);
            options.AbortOnConnectFail = false;
            options.ConnectRetry = 3;
            options.ReconnectRetryPolicy = new ExponentialRetry(1000, 10000);
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddSingleton<PublicDeviceIdService>(_ =>
        {
            var publicIdKeys = ReadHmacKeys(configuration);
            return new PublicDeviceIdService(
                publicIdKeys.ActiveVersion,
                publicIdKeys.ActiveKey,
                publicIdKeys.PreviousKeys);
        });
        services.AddSingleton<IPublicDeviceIdService>(provider => provider.GetRequiredService<PublicDeviceIdService>());
        services.AddSingleton<IPrivacyHasher>(provider => provider.GetRequiredService<PublicDeviceIdService>());
        services.AddSingleton<IDeviceProofVerifier, EcdsaDeviceProofVerifier>();
        services.AddSingleton<IDeviceChallengeStore, RedisDeviceChallengeStore>();
        services.AddSingleton<IDeviceAccessTokenIssuer, RedisDeviceAccessTokenIssuer>();
        services.AddSingleton<IDeviceAccessTokenValidator>(provider => provider.GetRequiredService<IDeviceAccessTokenIssuer>());
        services.AddSingleton<IPresenceLeaseStore, RedisPresenceLeaseStore>();
        services.AddSingleton<IDistributedOperationLeaseManager, RedisDistributedOperationLeaseManager>();

        services.AddScoped<CloudRepository>();
        services.AddScoped<IDeviceRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<IInstallationRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<ISessionRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<IDownloadRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<IDownloadArtifactRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<IReleaseRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<IDiagnosticRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<IAuditRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<IAdminIdentityRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<IRetentionRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<IAnalyticsQueryRepository>(provider => provider.GetRequiredService<CloudRepository>());
        services.AddScoped<ICloudUnitOfWork>(provider => provider.GetRequiredService<CloudDbContext>());
        services.AddScoped<IPresenceHistoryWriter, PresenceHistoryWriter>();
        services.AddScoped<IDeviceAccessStateValidator, DeviceAccessStateValidator>();
        services.AddScoped<IDeviceBootstrapStore, DeviceBootstrapStore>();

        services.AddScoped<IInstallationService, InstallationService>();
        services.AddScoped<ISessionTelemetryService, SessionTelemetryService>();
        services.AddScoped<IDownloadTrackingService, DownloadTrackingService>();
        services.AddScoped<IReleaseTelemetryService, ReleaseTelemetryService>();
        services.AddScoped<IDiagnosticsService, DiagnosticsService>();
        services.AddScoped<IAdminQueryService, AdminQueryService>();

        if (workerOptions.EnablePresenceExpiration) services.AddHostedService<PresenceExpirationWorker>();
        if (workerOptions.EnableSessionReconciliation) services.AddHostedService<StaleSessionReconciliationWorker>();
        if (workerOptions.EnableRetention) services.AddHostedService<RetentionWorker>();
        return services;
    }

    public static IServiceCollection AddPeerOnQDedicatedDeviceTokenValidation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DeviceTokenValidationRedis");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DeviceTokenValidationRedis is required for least-privilege device token validation.");
        }

        services.AddSingleton(_ => DeviceTokenValidationRedisConnection.Connect(connectionString));
        services.Replace(ServiceDescriptor.Singleton<IDeviceAccessTokenValidator>(provider =>
            new RedisDeviceAccessTokenValidator(
                provider.GetRequiredService<DeviceTokenValidationRedisConnection>().Connection,
                provider.GetRequiredService<RedisKeySpace>(),
                provider.GetRequiredService<TimeProvider>())));
        return services;
    }

    private static PublicIdKeyConfiguration ReadHmacKeys(IConfiguration configuration)
    {
        var encoded = configuration["CloudSecurity:PublicDeviceIdHmacKeyBase64"];
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("CloudSecurity:PublicDeviceIdHmacKeyBase64 is required and must be provided by secret storage.");
        try
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length < 32)
                throw new InvalidOperationException("Public device ID HMAC key must contain at least 32 bytes.");
            var activeVersion = configuration.GetValue<int?>("CloudSecurity:PublicDeviceIdHmacKeyVersion") ?? 1;
            if (activeVersion <= 0)
                throw new InvalidOperationException("Public device ID HMAC key version must be positive.");

            var previous = new Dictionary<int, byte[]>();
            foreach (var child in configuration.GetSection("CloudSecurity:PreviousPublicDeviceIdHmacKeysBase64").GetChildren())
            {
                if (!int.TryParse(child.Key, out var version) || version <= 0 || version == activeVersion ||
                    string.IsNullOrWhiteSpace(child.Value))
                {
                    throw new InvalidOperationException("Previous public device ID HMAC key version is invalid.");
                }
                var previousKey = Convert.FromBase64String(child.Value);
                if (previousKey.Length < 32 || !previous.TryAdd(version, previousKey))
                    throw new InvalidOperationException("Previous public device ID HMAC key is invalid.");
            }
            return new PublicIdKeyConfiguration(activeVersion, key, previous);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Public device ID HMAC key is not valid Base64.", exception);
        }
    }

    private sealed record PublicIdKeyConfiguration(
        int ActiveVersion,
        byte[] ActiveKey,
        IReadOnlyDictionary<int, byte[]> PreviousKeys);
}
