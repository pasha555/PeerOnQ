using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Infrastructure.Redis;
using PeerOnQ.Observability;
using PeerOnQ.Presence.Server;
using StackExchange.Redis;

namespace PeerOnQ.Presence.Tests;

public sealed class DeviceTokenValidationTests
{
    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task CloudIssuedToken_AuthenticatesThroughReadOnlyPresenceReader()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var adminOptions = ConfigurationOptions.Parse(connectionString);
        adminOptions.AllowAdmin = true;
        adminOptions.AbortOnConnectFail = true;
        await using var admin = await ConnectionMultiplexer.ConnectAsync(adminOptions);
        var endpoint = admin.GetEndPoints().Single();
        var server = admin.GetServer(endpoint);
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var readerUser = $"peeronq_test_token_reader_{suffix}";
        var presenceUser = $"peeronq_test_presence_{suffix}";
        var readerPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var presencePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        const string prefix = "peeronq:cloud:v1";
        var issuedKeys = new List<RedisKey>();

        await CreateReaderUserAsync(server, readerUser, readerPassword, $"{prefix}:device-token:*");
        await CreatePresenceUserAsync(server, presenceUser, presencePassword, $"{prefix}:presence:*");
        try
        {
            var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-11T12:00:00Z"));
            var keySpace = new RedisKeySpace { Prefix = prefix };
            var issuer = new RedisDeviceAccessTokenIssuer(admin, keySpace, time);
            var token = await issuer.IssueAsync(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(30), CancellationToken.None);
            var tokenKey = GetTokenKey(prefix, token.Token);
            issuedKeys.Add(tokenKey);

            await using var reader = DeviceTokenValidationRedisConnection.Connect(
                BuildConnectionString(adminOptions, readerUser, readerPassword));
            var validator = new RedisDeviceAccessTokenValidator(reader.Connection, keySpace, time);

            var validated = await validator.ValidateAsync(token.Token, CancellationToken.None);
            Assert.NotNull(validated);
            var authentication = await AuthenticatePresenceQueryTokenAsync(token.Token, validator, time);
            Assert.True(authentication.Succeeded);
            Assert.Equal(validated.DeviceId.ToString("N"), authentication.Principal?.FindFirst(DeviceAccessAuthentication.DeviceIdClaim)?.Value);

            Assert.False((await reader.Connection.GetDatabase().StringGetAsync(tokenKey)).IsNullOrEmpty);
            await AssertNoPermissionAsync(() => reader.Connection.GetDatabase().StringSetAsync(tokenKey, "tampered"));
            await AssertNoPermissionAsync(() => reader.Connection.GetDatabase().KeyDeleteAsync(tokenKey));
            await AssertNoPermissionAsync(() => reader.Connection.GetDatabase().ExecuteAsync("SCAN", "0"));

            await using (var presence = await ConnectionMultiplexer.ConnectAsync(
                BuildConnectionOptions(adminOptions, presenceUser, presencePassword)))
            {
                await AssertNoPermissionAsync(() => presence.GetDatabase().StringGetAsync(tokenKey));
            }

            Assert.Null(await validator.ValidateAsync("not-a-device-token", CancellationToken.None));
            Assert.False((await AuthenticatePresenceQueryTokenAsync("not-a-device-token", validator, time)).Succeeded);

            time.Advance(TimeSpan.FromMinutes(31));
            Assert.Null(await validator.ValidateAsync(token.Token, CancellationToken.None));
            Assert.False((await AuthenticatePresenceQueryTokenAsync(token.Token, validator, time)).Succeeded);

            var revoked = await issuer.IssueAsync(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(30), CancellationToken.None);
            issuedKeys.Add(GetTokenKey(prefix, revoked.Token));
            await issuer.RevokeAsync(revoked.Token, CancellationToken.None);
            Assert.Null(await validator.ValidateAsync(revoked.Token, CancellationToken.None));
            Assert.False((await AuthenticatePresenceQueryTokenAsync(revoked.Token, validator, time)).Succeeded);

            var healthCheck = new DeviceTokenValidationRedisReadinessHealthCheck(reader, keySpace);
            Assert.Equal(HealthStatus.Healthy, (await healthCheck.CheckHealthAsync(new HealthCheckContext())).Status);
            await server.ExecuteAsync("ACL", "SETUSER", readerUser, "-get");
            Assert.Equal(HealthStatus.Unhealthy, (await healthCheck.CheckHealthAsync(new HealthCheckContext())).Status);
            await server.ExecuteAsync("ACL", "SETUSER", readerUser, "+get");
        }
        finally
        {
            if (issuedKeys.Count > 0) await admin.GetDatabase().KeyDeleteAsync([.. issuedKeys]);
            await server.ExecuteAsync("ACL", "DELUSER", readerUser, presenceUser);
        }
    }

    [Fact]
    public void TokenReaderHealthCheck_IsNotPartOfLiveness()
    {
        var services = new ServiceCollection();
        services.AddPeerOnQDeviceTokenValidationHealthCheck();
        using var provider = services.BuildServiceProvider();
        var registration = Assert.Single(provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);

        Assert.Equal("redis-device-token-validation", registration.Name);
        Assert.Contains("ready", registration.Tags);
        Assert.Contains("startup", registration.Tags);
        Assert.DoesNotContain("live", registration.Tags);
    }

    private static async Task<AuthenticateResult> AuthenticatePresenceQueryTokenAsync(
        string token,
        IDeviceAccessTokenValidator validator,
        TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDeviceAccessTokenValidator>(validator);
        services.AddSingleton<IDeviceAccessStateValidator, ActiveDeviceAccessStateValidator>();
        services.AddSingleton(timeProvider);
        services.AddSingleton(new PeerOnQMetricState());
        services.AddSingleton<PeerOnQMetrics>();
        services.AddPeerOnQDeviceAuthentication(options =>
        {
            options.AllowQueryToken = true;
            options.QueryTokenPath = PresenceHub.Path;
            options.RequireHttpsForQueryToken = true;
        });
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Request.Scheme = "https";
        context.Request.Path = PresenceHub.Path;
        context.Request.QueryString = QueryString.Create("access_token", token);
        return await context.AuthenticateAsync(DeviceAccessAuthentication.Scheme);
    }

    private static async Task CreateReaderUserAsync(IServer server, string user, string password, string tokenPattern) =>
        await server.ExecuteAsync(
            "ACL", "SETUSER", user, "reset", "on", $">{password}", "resetkeys", "resetchannels",
            $"%R~{tokenPattern}", "+ping", "+get", "+echo");

    private static async Task CreatePresenceUserAsync(IServer server, string user, string password, string presencePattern) =>
        await server.ExecuteAsync(
            "ACL", "SETUSER", user, "reset", "on", $">{password}", "resetkeys", "resetchannels",
            $"%RW~{presencePattern}", "+ping", "+get", "+set", "+del", "+echo");

    private static ConfigurationOptions BuildConnectionOptions(
        ConfigurationOptions source,
        string user,
        string password)
    {
        var options = ConfigurationOptions.Parse(source.ToString(includePassword: true));
        options.User = user;
        options.Password = password;
        options.AllowAdmin = false;
        options.AbortOnConnectFail = true;
        return options;
    }

    private static string BuildConnectionString(ConfigurationOptions source, string user, string password) =>
        BuildConnectionOptions(source, user, password).ToString(includePassword: true);

    private static RedisKey GetTokenKey(string prefix, string token)
    {
        var normalized = token.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        var raw = Convert.FromBase64String(normalized);
        return $"{prefix}:device-token:{Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant()}";
    }

    private static async Task AssertNoPermissionAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<RedisServerException>(action);
        Assert.Contains("NOPERM", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ActiveDeviceAccessStateValidator : IDeviceAccessStateValidator
    {
        public Task<bool> IsActiveAsync(DeviceAccessPrincipal principal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }
}
