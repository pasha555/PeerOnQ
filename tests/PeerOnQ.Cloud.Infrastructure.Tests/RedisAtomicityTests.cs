using System.Security.Cryptography;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Infrastructure.Redis;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Shared.Contracts.V1;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class RedisAtomicityTests
{
    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task ChallengeIsConsumedExactlyOnceWhenRedisIsConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var store = new RedisDeviceChallengeStore(redis, new RedisKeySpace { Prefix = $"peeronq:test:{Guid.NewGuid():N}" });
        var now = DateTimeOffset.UtcNow;
        var challenge = new DeviceChallengeRecord(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Guid.NewGuid(),
            Convert.ToHexString(SHA256.HashData("device"u8)).ToLowerInvariant(), "Device",
            PlatformKindV1.Windows, ArchitectureKindV1.X64, "1.0.0", "Windows 11",
            InstallChannelV1.Stable, "eu-west", "1", "canonical", now, now.AddMinutes(1));
        await store.StoreAsync(challenge, CancellationToken.None);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => store.ConsumeAsync(challenge.ChallengeId, CancellationToken.None)));

        Assert.Single(attempts, value => value is not null);
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task PresenceLeaseAtomicallyDisplacesAndRejectsOldConnection()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var store = new RedisPresenceLeaseStore(redis, new RedisKeySpace { Prefix = $"peeronq:test:{Guid.NewGuid():N}" });
        var now = DateTimeOffset.UtcNow;
        var installationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var first = await store.AcquireAsync(new PresenceLeaseRequest(installationId, deviceId, "connection-1",
            "presence-a", PresenceState.Online, "1.0.0", "eu-west", now, TimeSpan.FromSeconds(30)), CancellationToken.None);
        var second = await store.AcquireAsync(new PresenceLeaseRequest(installationId, deviceId, "connection-2",
            "presence-b", PresenceState.InSession, "1.0.0", "eu-west", now.AddSeconds(1), TimeSpan.FromSeconds(30)), CancellationToken.None);

        Assert.Null(first.DisplacedLease);
        Assert.Equal("connection-1", second.DisplacedConnectionId);
        Assert.Null(await store.RefreshAsync(installationId, "connection-1", PresenceState.Online, now.AddSeconds(2), TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.NotNull(await store.RefreshAsync(installationId, "connection-2", PresenceState.Busy, now.AddSeconds(2), TimeSpan.FromSeconds(30), CancellationToken.None));
        var secondInstallationId = Guid.NewGuid();
        await store.AcquireAsync(new PresenceLeaseRequest(secondInstallationId, deviceId, "connection-3",
            "presence-a", PresenceState.Online, "1.0.0", "eu-west", now.AddSeconds(2), TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.Equal(2, await store.CountActiveAsync(now.AddSeconds(2), CancellationToken.None));
        Assert.Equal(1, await store.CountActiveDevicesAsync(now.AddSeconds(2), CancellationToken.None));
        Assert.NotNull(await store.ReleaseAsync(installationId, "connection-2", now.AddSeconds(3), CancellationToken.None));
        Assert.Equal(1, await store.CountActiveAsync(now.AddSeconds(3), CancellationToken.None));
        Assert.Equal(1, await store.CountActiveDevicesAsync(now.AddSeconds(3), CancellationToken.None));
        Assert.NotNull(await store.ReleaseAsync(secondInstallationId, "connection-3", now.AddSeconds(3), CancellationToken.None));
        Assert.Equal(0, await store.CountActiveAsync(now.AddSeconds(3), CancellationToken.None));
        Assert.Equal(0, await store.CountActiveDevicesAsync(now.AddSeconds(3), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "ExternalIntegration")]
    public async Task DistributedOperationLease_HasOneOwnerAndTransfersAfterRelease()
    {
        var connectionString = Environment.GetEnvironmentVariable("PEERONQ_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var keySpace = new RedisKeySpace { Prefix = $"peeronq:test:{Guid.NewGuid():N}" };
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var firstManager = new RedisDistributedOperationLeaseManager(
            redis,
            keySpace,
            loggerFactory.CreateLogger<RedisDistributedOperationLeaseManager>());
        var secondManager = new RedisDistributedOperationLeaseManager(
            redis,
            keySpace,
            loggerFactory.CreateLogger<RedisDistributedOperationLeaseManager>());

        await using var first = await firstManager.TryAcquireAsync(
            "retention",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        var rejected = await secondManager.TryAcquireAsync(
            "retention",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(rejected);
        await first!.DisposeAsync();
        await using var transferred = await secondManager.TryAcquireAsync(
            "retention",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        Assert.NotNull(transferred);
    }
}
