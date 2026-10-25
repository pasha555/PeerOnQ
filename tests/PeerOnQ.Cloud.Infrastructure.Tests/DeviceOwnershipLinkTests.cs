using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Api;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Redis;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Observability;
using StackExchange.Redis;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed partial class PostgreSqlMigrationTests
{
    [ExternalIntegrationFact("PEERONQ_TEST_POSTGRES", "PEERONQ_TEST_REDIS")]
    [Trait("Category", "ExternalIntegration")]
    public async Task OwnershipLinkRequiresExplicitBoundPreviewAndIsHashedOneUseAndTenantScoped()
    {
        var connection = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES") ?? throw new InvalidOperationException("PostgreSQL integration connection is required.");
        var redisConnection = Environment.GetEnvironmentVariable("PEERONQ_TEST_REDIS") ?? throw new InvalidOperationException("Redis integration connection is required.");
        await using var db = CreateContext(connection);
        await MigrateForTestAsync(db, connection);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        var clock = new OwnershipClock(DateTimeOffset.UtcNow);
        var prefix = new RedisKeySpace { Prefix = $"peeronq:ownership:{Guid.NewGuid():N}" };
        var owner = Account("Owner", clock.GetUtcNow());
        var other = Account("Other owner", clock.GetUtcNow());
        var ownerSession = Session(owner.Id, clock.GetUtcNow());
        var secondOwnerSession = Session(owner.Id, clock.GetUtcNow());
        var otherSession = Session(other.Id, clock.GetUtcNow());
        var org = new Organization(Guid.NewGuid(), "Ownership test", owner.Id, clock.GetUtcNow());
        var otherOrg = new Organization(Guid.NewGuid(), "Other tenant", other.Id, clock.GetUtcNow());
        var device = Device.Create(RandomNumberGenerator.GetBytes(32), "**** **** 1234", "Local device", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), clock.GetUtcNow());
        var installation = Installation.RegisterProofBound(Guid.NewGuid(), device.Id, device.PublicDeviceIdHash,
            PlatformKind.Windows, ArchitectureKind.X64, "1.0.0", "Test", InstallChannel.Stable, "3", "eu-west", clock.GetUtcNow());
        db.CustomerAccounts.AddRange(owner, other);
        db.CustomerSessions.AddRange(ownerSession, secondOwnerSession, otherSession);
        db.Organizations.AddRange(org, otherOrg);
        db.OrganizationMemberships.AddRange(new OrganizationMembership(org.Id, owner.Id, CustomerRoleKind.Owner, clock.GetUtcNow()),
            new OrganizationMembership(otherOrg.Id, other.Id, CustomerRoleKind.Owner, clock.GetUtcNow()));
        db.Devices.Add(device);
        db.Installations.Add(installation);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new DeviceOwnershipLinkService(db, redis, prefix, clock, new HttpContextAccessor());
        var factory = new PooledDbContextFactory<CloudDbContext>(new DbContextOptionsBuilder<CloudDbContext>().UseNpgsql(connection).Options);
        var issuer = new RedisDeviceAccessTokenIssuer(redis, prefix, clock, factory);
        var access = await issuer.IssueAsync(device.Id, installation.Id, TimeSpan.FromMinutes(15), CancellationToken.None, device.IdentityFingerprint);
        var native = (await issuer.ValidateAsync(access.Token, CancellationToken.None))!;
        Assert.Equal(device.IdentityVersion, native.IdentityVersion);
        await Assert.ThrowsAsync<ApiProblemException>(() => service.CreateAsync(native with { IdentityVersion = 0, ProofBindingVersion = 0 }, CancellationToken.None));
        Assert.False((await service.StatusAsync(native, CancellationToken.None)).Linked);
        var link = await service.CreateAsync(native, CancellationToken.None);
        Assert.Equal(43, link.Code.Length);
        Assert.Equal(clock.GetUtcNow().AddMinutes(2), link.ExpiresAtUtc);
        var key = $"{prefix.Prefix}:ownership-link:{Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(link.Code)))}";
        Assert.DoesNotContain(link.Code, (string)(await redis.GetDatabase().StringGetAsync(key))!);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(other.Id, otherSession.Id, org.Id, link.Code, CancellationToken.None));
        await Assert.ThrowsAsync<ApiProblemException>(() => service.ClaimAsync(owner.Id, ownerSession.Id, org.Id, link.Code, new string('x', 43), CancellationToken.None));
        var preview = await service.PreviewAsync(owner.Id, ownerSession.Id, org.Id, link.Code, CancellationToken.None);
        Assert.Equal(device.MaskedPublicDeviceId, preview.MaskedPublicDeviceId);
        await Assert.ThrowsAsync<ApiProblemException>(() => service.ClaimAsync(owner.Id, secondOwnerSession.Id, org.Id, link.Code, preview.PreviewToken, CancellationToken.None));
        await Assert.ThrowsAsync<ApiProblemException>(() => service.PreviewAsync(other.Id, otherSession.Id, otherOrg.Id, link.Code, CancellationToken.None));
        await Assert.ThrowsAsync<ApiProblemException>(() => service.ClaimAsync(owner.Id, ownerSession.Id, org.Id, link.Code, new string('x', 43), CancellationToken.None));
        async Task<bool> ClaimOnceAsync()
        {
            await using var claimDb = CreateContext(connection);
            var claimant = new DeviceOwnershipLinkService(claimDb, redis, prefix, clock, new HttpContextAccessor());
            try
            {
                await claimant.ClaimAsync(owner.Id, ownerSession.Id, org.Id, link.Code, preview.PreviewToken, CancellationToken.None);
                return true;
            }
            catch (ApiProblemException) { return false; }
        }
        Assert.Equal(1, (await Task.WhenAll(ClaimOnceAsync(), ClaimOnceAsync())).Count(succeeded => succeeded));
        db.ChangeTracker.Clear();
        Assert.True((await service.StatusAsync(native, CancellationToken.None)).Linked);
        Assert.Equal(org.Id, (await db.Devices.AsNoTracking().SingleAsync(value => value.Id == device.Id)).OwnerOrganizationId);
        Assert.True((await redis.GetDatabase().StringGetAsync(key)).IsNullOrEmpty);
        await Assert.ThrowsAsync<ApiProblemException>(() => service.ClaimAsync(owner.Id, ownerSession.Id, org.Id, link.Code, preview.PreviewToken, CancellationToken.None));
        var audit = await db.CustomerSecurityEvents.AsNoTracking().SingleAsync(value => value.OrganizationId == org.Id && value.Action == "organization.device_claimed");
        Assert.Null(audit.SafeMetadata);
        var otherMembership = new OrganizationMembership(org.Id, other.Id, CustomerRoleKind.Member, clock.GetUtcNow());
        db.OrganizationMemberships.Add(otherMembership);
        await db.SaveChangesAsync();
        var sameOrgLink = await service.CreateAsync(native, CancellationToken.None);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(other.Id, otherSession.Id, org.Id, sameOrgLink.Code, CancellationToken.None));
        otherMembership.ChangeRole(CustomerRoleKind.Administrator);
        await db.SaveChangesAsync();
        var sameOrgPreview = await service.PreviewAsync(other.Id, otherSession.Id, org.Id, sameOrgLink.Code, CancellationToken.None);
        await service.ClaimAsync(other.Id, otherSession.Id, org.Id, sameOrgLink.Code, sameOrgPreview.PreviewToken, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Equal(owner.Id, (await db.Devices.AsNoTracking().SingleAsync(value => value.Id == device.Id)).OwnerUserId);
        var another = await service.CreateAsync(native, CancellationToken.None);
        var owned = await Assert.ThrowsAsync<ApiProblemException>(() => service.PreviewAsync(other.Id, otherSession.Id, otherOrg.Id, another.Code, CancellationToken.None));
        Assert.Equal("device_already_owned", owned.ErrorCode);
        clock.Now += TimeSpan.FromMinutes(3);
        await Assert.ThrowsAsync<ApiProblemException>(() => service.PreviewAsync(owner.Id, ownerSession.Id, org.Id, another.Code, CancellationToken.None));
        clock.Now -= TimeSpan.FromMinutes(3);
        var rotatedLink = await service.CreateAsync(native, CancellationToken.None);
        (await db.Devices.SingleAsync(value => value.Id == device.Id)).RotateIdentity(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), clock.GetUtcNow());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<ApiProblemException>(() => service.PreviewAsync(owner.Id, ownerSession.Id, org.Id, rotatedLink.Code, CancellationToken.None));
        await Assert.ThrowsAsync<ApiProblemException>(() => service.CreateAsync(native, CancellationToken.None));
        Assert.False(await new DeviceAccessStateValidator(db).IsActiveAsync(native, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => issuer.IssueAsync(device.Id, installation.Id, TimeSpan.FromMinutes(15), CancellationToken.None, device.IdentityFingerprint));
        var legacy = new CustomerOrganizationService(db, new CapturingMail(), issuer, Options.Create(new CustomerPortalOptions()), new HttpContextAccessor());
        await Assert.ThrowsAsync<ApiProblemException>(() => legacy.ClaimDeviceAsync(owner.Id, org.Id, device.Id, access.Token, CancellationToken.None));
        db.ChangeTracker.Clear();
        var freshAccess = await issuer.IssueAsync(device.Id, installation.Id, TimeSpan.FromMinutes(15), CancellationToken.None);
        native = (await issuer.ValidateAsync(freshAccess.Token, CancellationToken.None))!;
        var blockedLink = await service.CreateAsync(native, CancellationToken.None);
        (await db.Installations.SingleAsync(value => value.Id == installation.Id)).Block("Integration security check");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<ApiProblemException>(() => service.PreviewAsync(owner.Id, ownerSession.Id, org.Id, blockedLink.Code, CancellationToken.None));
        (await db.Installations.SingleAsync(value => value.Id == installation.Id)).Unblock();
        (await db.Devices.SingleAsync(value => value.Id == device.Id)).Revoke("Integration security check", clock.GetUtcNow());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<ApiProblemException>(() => service.CreateAsync(native, CancellationToken.None));
        // The analytics fixture asserts global installation counts. Remove only the two
        // uniquely generated test rows; retain the append-only organization audit evidence.
        await db.Installations.Where(value => value.Id == installation.Id).ExecuteDeleteAsync();
        await db.Devices.Where(value => value.Id == device.Id).ExecuteDeleteAsync();
    }

    private sealed class OwnershipClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
