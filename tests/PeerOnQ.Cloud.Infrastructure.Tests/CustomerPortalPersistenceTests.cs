using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Api;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed partial class PostgreSqlMigrationTests
{
    [ExternalIntegrationFact("PEERONQ_TEST_POSTGRES")]
    [Trait("Category", "ExternalIntegration")]
    public async Task ManagedSystemAudioPolicyDefaultsOffAndLegacyUpdatesPreserveExplicitConsent()
    {
        var connection = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PostgreSQL integration connection is required.");
        await using var db = CreateContext(connection);
        await MigrateForTestAsync(db, connection);
        var now = DateTimeOffset.UtcNow;
        var owner = Account("AudioPolicyOwner", now);
        var organization = new Organization(Guid.NewGuid(), "Audio policy", owner.Id, now);
        db.CustomerAccounts.Add(owner);
        db.Organizations.Add(organization);
        db.OrganizationMemberships.Add(new OrganizationMembership(organization.Id, owner.Id, CustomerRoleKind.Owner, now));
        db.OrganizationPolicies.Add(new OrganizationPolicy(organization.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new CustomerOrganizationService(db, new CapturingMail(), null!, Options.Create(new CustomerPortalOptions()), new HttpContextAccessor());
        Assert.False((await service.GetPolicyAsync(owner.Id, organization.Id, CancellationToken.None)).SystemAudioAllowed);
        var legacy = new UpdateOrganizationPolicyRequest(true, true, true, true, false, false, 30, 90, "", "", false);
        await service.UpdatePolicyAsync(owner.Id, organization.Id, legacy with { SystemAudioAllowed = true }, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.True((await service.GetPolicyAsync(owner.Id, organization.Id, CancellationToken.None)).SystemAudioAllowed);
        await service.UpdatePolicyAsync(owner.Id, organization.Id, legacy, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.True((await service.GetPolicyAsync(owner.Id, organization.Id, CancellationToken.None)).SystemAudioAllowed);
        await service.UpdatePolicyAsync(owner.Id, organization.Id, legacy with { SystemAudioAllowed = false }, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.False((await service.GetPolicyAsync(owner.Id, organization.Id, CancellationToken.None)).SystemAudioAllowed);
    }

    [ExternalIntegrationFact("PEERONQ_TEST_POSTGRES")]
    [Trait("Category", "ExternalIntegration")]
    public async Task RemovedMemberRejoinsUsingInvitationRoleWithoutDuplicatingMembership()
    {
        var connection = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PostgreSQL integration connection is required.");
        await using var db = CreateContext(connection);
        await MigrateForTestAsync(db, connection);
        var now = DateTimeOffset.UtcNow;
        var owner = Account("Owner", now);
        var member = Account("Member", now);
        var stranger = Account("Stranger", now);
        var organization = new Organization(Guid.NewGuid(), "Reinvite", owner.Id, now);
        var revoked = new OrganizationMembership(organization.Id, member.Id, CustomerRoleKind.Administrator, now);
        revoked.Revoke(now);
        db.CustomerAccounts.AddRange(owner, member, stranger);
        db.Organizations.Add(organization);
        db.OrganizationMemberships.AddRange(new OrganizationMembership(organization.Id, owner.Id, CustomerRoleKind.Owner, now), revoked);
        await db.SaveChangesAsync();
        var mail = new CapturingMail();
        var service = new CustomerOrganizationService(db, mail, null!, Options.Create(new CustomerPortalOptions()), new HttpContextAccessor());
        await service.InviteAsync(owner.Id, organization.Id, new(member.Email, CustomerRoleKind.Member), CancellationToken.None);
        var token = mail.Token;
        await Assert.ThrowsAsync<PeerOnQ.Observability.ApiProblemException>(() => service.AcceptInvitationAsync(stranger.Id, token, CancellationToken.None));
        await service.AcceptInvitationAsync(member.Id, token, CancellationToken.None);
        db.ChangeTracker.Clear();
        var rows = await db.OrganizationMemberships.Where(value => value.OrganizationId == organization.Id && value.AccountId == member.Id).ToListAsync();
        var active = Assert.Single(rows);
        Assert.True(active.IsActive);
        Assert.Equal(CustomerRoleKind.Member, active.Role);
        await Assert.ThrowsAsync<PeerOnQ.Observability.ApiProblemException>(() => service.AcceptInvitationAsync(member.Id, token, CancellationToken.None));
        var team = await service.CreateTeamAsync(owner.Id, organization.Id, "Rejoined team", CancellationToken.None);
        await service.AddTeamMemberAsync(owner.Id, organization.Id, team.Id, member.Id, CancellationToken.None);
        Assert.Equal(member.Id, Assert.Single(await service.ListTeamMembersAsync(owner.Id, organization.Id, team.Id, CancellationToken.None)).AccountId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ListTeamMembersAsync(owner.Id, organization.Id, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListTeamMembersAsync(stranger.Id, organization.Id, Guid.NewGuid(), CancellationToken.None));
        db.OrganizationMemberships.Add(new OrganizationMembership(organization.Id, stranger.Id, CustomerRoleKind.Member, now));
        (await db.CustomerAccounts.SingleAsync(value => value.Id == stranger.Id)).Disable();
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TransferOwnershipAsync(owner.Id, organization.Id, stranger.Id, CancellationToken.None));
    }

    [ExternalIntegrationFact("PEERONQ_TEST_POSTGRES")]
    [Trait("Category", "ExternalIntegration")]
    public async Task MfaDraftRequiresItsAccountSessionAndVersionAndReenrollmentRequiresStepUp()
    {
        var connection = Environment.GetEnvironmentVariable("PEERONQ_TEST_POSTGRES")
            ?? throw new InvalidOperationException("PostgreSQL integration connection is required.");
        await using var db = CreateContext(connection);
        await MigrateForTestAsync(db, connection);
        var now = DateTimeOffset.UtcNow;
        var account = Account("Mfa", now);
        var other = Account("Other", now);
        var passwords = new CustomerPasswordService();
        const string password = "AuditIntegrationPassword9"; // secret-scan: allow-test-vector; isolated test account only
        account.ChangePasswordHash(passwords.Hash(account, password));
        var session = Session(account.Id, now);
        var secondSession = Session(account.Id, now);
        var otherSession = Session(other.Id, now);
        db.CustomerAccounts.AddRange(account, other);
        db.CustomerSessions.AddRange(session, secondSession, otherSession);
        await db.SaveChangesAsync();
        var options = Options.Create(new CustomerPortalOptions { EnableMfa = true, JwtSigningKey = new string('x', 64) });
        var service = new CustomerAccountService(db, passwords, new CustomerTokenService(options),
            new EphemeralDataProtectionProvider(), new CapturingMail(), options, new HttpContextAccessor());
        var setup = await service.BeginMfaSetupAsync(account.Id, session.Id, null, CancellationToken.None);
        var code = Totp(setup.Secret);
        await Assert.ThrowsAsync<PeerOnQ.Observability.ApiProblemException>(() => service.ConfirmMfaAsync(other.Id, otherSession.Id, setup.SetupToken, code, CancellationToken.None));
        await Assert.ThrowsAsync<PeerOnQ.Observability.ApiProblemException>(() => service.ConfirmMfaAsync(account.Id, secondSession.Id, setup.SetupToken, code, CancellationToken.None));
        var recovery = await service.ConfirmMfaAsync(account.Id, session.Id, setup.SetupToken, code, CancellationToken.None);
        Assert.Equal(10, recovery.Count);
        await Assert.ThrowsAsync<PeerOnQ.Observability.ApiProblemException>(() => service.ConfirmMfaAsync(account.Id, session.Id, setup.SetupToken, code, CancellationToken.None));
        await Assert.ThrowsAsync<PeerOnQ.Observability.ApiProblemException>(() => service.BeginMfaSetupAsync(account.Id, session.Id, null, CancellationToken.None));
        await Assert.ThrowsAsync<PeerOnQ.Observability.ApiProblemException>(() => service.BeginMfaSetupAsync(account.Id, session.Id, new("WrongPassword9", code), CancellationToken.None));
        var replacement = await service.BeginMfaSetupAsync(account.Id, session.Id, new(password, recovery[0]), CancellationToken.None);
        await Assert.ThrowsAsync<PeerOnQ.Observability.ApiProblemException>(() => service.BeginMfaSetupAsync(account.Id, session.Id, new(password, recovery[0]), CancellationToken.None));
        Assert.Equal(10, (await service.ConfirmMfaAsync(account.Id, session.Id, replacement.SetupToken, Totp(replacement.Secret), CancellationToken.None)).Count);
        var stale = await service.BeginMfaSetupAsync(account.Id, session.Id, new(password, Totp(replacement.Secret)), CancellationToken.None);
        session.Revoke(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<PeerOnQ.Observability.ApiProblemException>(() => service.ConfirmMfaAsync(account.Id, session.Id, stale.SetupToken, Totp(stale.Secret), CancellationToken.None));
    }

    private static CustomerAccount Account(string name, DateTimeOffset now) =>
        new(Guid.NewGuid(), $"{Guid.NewGuid():N}@example.test", name, "pending", now);

    private static CustomerSession Session(Guid accountId, DateTimeOffset now) =>
        new(Guid.NewGuid(), accountId, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), now, now.AddHours(1), "Integration test");

    private static string Totp(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32.Select(value => Convert.ToString(alphabet.IndexOf(value), 2).PadLeft(5, '0')));
        var secret = Enumerable.Range(0, bits.Length / 8).Select(index => Convert.ToByte(bits.Substring(index * 8, 8), 2)).ToArray();
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 15;
        var value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private sealed class CapturingMail : ICustomerMailSender
    {
        public bool IsEnabled => true;
        public string Token { get; private set; } = string.Empty;
        public void EnsureEnabled() { }
        public Task SendAsync(string recipient, string subject, string textBody, CancellationToken cancellationToken)
        {
            Token = Uri.UnescapeDataString(textBody[(textBody.LastIndexOf("token=", StringComparison.Ordinal) + 6)..]);
            return Task.CompletedTask;
        }
    }
}
