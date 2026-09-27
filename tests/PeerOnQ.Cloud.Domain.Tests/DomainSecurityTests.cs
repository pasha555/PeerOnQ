using System.Security.Cryptography;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;

namespace PeerOnQ.Cloud.Domain.Tests;

public sealed class DomainSecurityTests
{
    [Fact]
    public void VerifiedActiveCustomerWithoutALockCanLogin()
    {
        var now = DateTimeOffset.UtcNow;
        var account = new CustomerAccount(Guid.NewGuid(), "customer@example.test", "Customer", "password-hash", now);
        account.VerifyEmail();

        Assert.True(account.IsLoginAllowed(now, emailVerificationRequired: true));
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InstallationCannotBeAttachedToDifferentIdentityClaim()
    {
        var claimed = SHA256.HashData("claimed"u8);
        var installation = Installation.Register(Guid.NewGuid(), claimed, PlatformKind.Windows,
            ArchitectureKind.X64, "1.2.3", "Windows 11", InstallChannel.Stable, "1", "eu-west", Now);

        Assert.Throws<InvalidOperationException>(() =>
            installation.AttachDevice(Guid.NewGuid(), SHA256.HashData("clone"u8), Now));
        Assert.Null(installation.DeviceId);
    }

    [Fact]
    public void DevicePinsFingerprintAndRevocationFailsClosed()
    {
        var device = Device.Create(SHA256.HashData("public-id"u8), "123-***-***-012", "Workstation", new string('a', 64), Now);

        Assert.True(device.MatchesFingerprint(new string('A', 64)));
        Assert.False(device.MatchesFingerprint(new string('b', 64)));

        device.Revoke("Owner requested revocation", Now.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => device.Touch("Renamed", Now.AddMinutes(2)));
    }

    [Fact]
    public void SessionRequiresTurnFlagToMatchSelectedPath()
    {
        var session = RemoteSession.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PermissionMode.ViewOnly, "eu-west", "1.0.0", "1.0.0", Now);

        Assert.Throws<ArgumentException>(() => session.MarkConnected(ConnectionPath.TurnTcp, false, Now.AddSeconds(1)));
        session.MarkConnected(ConnectionPath.TurnTls, true, Now.AddSeconds(2));

        Assert.True(session.UsedTurn);
        Assert.Equal(SessionLifecycle.Connected, session.Lifecycle);
    }

    [Fact]
    public void SessionSupportsVerifiedRelayWithUnknownTransport()
    {
        var session = RemoteSession.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PermissionMode.ViewOnly, "eu-west", "1.0.0", "1.0.0", Now);

        session.MarkConnected(ConnectionPath.Unknown, true, Now.AddSeconds(1));

        Assert.Equal(ConnectionPath.Unknown, session.ConnectionPath);
        Assert.True(session.UsedTurn);
    }

    [Fact]
    public void DiagnosticUploadAuthorizationIsOneUse()
    {
        var rawToken = RandomNumberGenerator.GetBytes(32);
        var bundle = DiagnosticBundle.Create(Guid.NewGuid(), SHA256.HashData(rawToken), Now.AddMinutes(15),
            Now.AddDays(14), "1.0.0", "Windows 11", ArchitectureKind.X64, "E-1", "network", 3, Now);

        bundle.MarkUploaded(SHA256.HashData(rawToken), 1024, new string('b', 64),
            "diagnostics/2026/08/id.zip", "D-ABC123", Now.AddMinutes(1));

        Assert.Equal(DiagnosticStatus.Uploaded, bundle.Status);
        Assert.Empty(bundle.UploadTokenHash);
        Assert.Throws<InvalidOperationException>(() => bundle.MarkUploaded(
            SHA256.HashData(rawToken), 1024, new string('b', 64), "other", "D-OTHER", Now.AddMinutes(2)));
    }

    [Fact]
    public void PrivilegedAdminLockoutAndMfaStateAreExplicit()
    {
        var user = new AdminUser(Guid.NewGuid(), "OWNER@EXAMPLE.COM", "framework-password-hash", Now);
        user.EnableMfa(RandomNumberGenerator.GetBytes(64));
        user.RecordFailedLogin(Now, 3, TimeSpan.FromMinutes(10));
        user.RecordFailedLogin(Now, 3, TimeSpan.FromMinutes(10));
        user.RecordFailedLogin(Now, 3, TimeSpan.FromMinutes(10));

        Assert.Equal("owner@example.com", user.Email);
        Assert.True(user.MfaEnabled);
        Assert.True(user.IsLocked(Now.AddMinutes(1)));
        Assert.False(user.IsLocked(Now.AddMinutes(11)));
    }

    [Fact]
    public void CustomerInvitationIsBoundToEmailAndIsSingleUse()
    {
        var rawToken = RandomNumberGenerator.GetBytes(32);
        var tokenHash = SHA256.HashData(rawToken);
        var invitation = new OrganizationInvitation(Guid.NewGuid(), Guid.NewGuid(), "member@example.com",
            CustomerRoleKind.Member, tokenHash, Guid.NewGuid(), Now, Now.AddMinutes(15));

        Assert.False(invitation.CanAccept("attacker@example.com", tokenHash, Now.AddMinutes(1)));
        Assert.False(invitation.CanAccept("member@example.com", SHA256.HashData("guess"u8), Now.AddMinutes(1)));

        invitation.Accept("MEMBER@example.com", tokenHash, Now.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() =>
            invitation.Accept("member@example.com", tokenHash, Now.AddMinutes(2)));
    }

    [Fact]
    public void CustomerInvitationRejectsExpirationAndRevocation()
    {
        var tokenHash = SHA256.HashData("invite"u8);
        var expired = new OrganizationInvitation(Guid.NewGuid(), Guid.NewGuid(), "member@example.com",
            CustomerRoleKind.Member, tokenHash, Guid.NewGuid(), Now, Now.AddMinutes(1));
        var revoked = new OrganizationInvitation(Guid.NewGuid(), Guid.NewGuid(), "member@example.com",
            CustomerRoleKind.Member, tokenHash, Guid.NewGuid(), Now, Now.AddMinutes(15));
        revoked.Revoke(Now.AddSeconds(30));

        Assert.False(expired.CanAccept("member@example.com", tokenHash, Now.AddMinutes(2)));
        Assert.False(revoked.CanAccept("member@example.com", tokenHash, Now.AddMinutes(1)));
    }

    [Fact]
    public void OrganizationOwnershipTransferRequiresCurrentOwner()
    {
        var currentOwner = Guid.NewGuid();
        var newOwner = Guid.NewGuid();
        var organization = new Organization(Guid.NewGuid(), "Engineering", currentOwner, Now);

        Assert.Throws<UnauthorizedAccessException>(() => organization.TransferOwnership(Guid.NewGuid(), newOwner));

        organization.TransferOwnership(currentOwner, newOwner);

        Assert.Equal(newOwner, organization.OwnerAccountId);
    }

    [Fact]
    public void OrganizationDeletionRequiresSingleRemainingOwner()
    {
        var owner = Guid.NewGuid();
        var organization = new Organization(Guid.NewGuid(), "Engineering", owner, Now);

        Assert.Throws<InvalidOperationException>(() => organization.RequestDeletion(owner, 2, Now));
        Assert.Throws<UnauthorizedAccessException>(() => organization.RequestDeletion(Guid.NewGuid(), 1, Now));

        organization.RequestDeletion(owner, 1, Now);
        Assert.Equal(Now, organization.DeletionRequestedAtUtc);
    }

    [Fact]
    public void OrganizationPolicyDeniesDisabledConnectionModes()
    {
        var policy = new OrganizationPolicy(Guid.NewGuid());
        policy.Update(viewOnly: true, fullControl: false, fileTransfer: false, clipboard: false,
            unattended: false, mfaRequired: true, trustedDeviceLifetimeDays: 7, auditRetentionDays: 30,
            approvedRelayRegionsCsv: "eu-west", minimumClientVersion: "0.7.0", hybridSecurityRequired: false);

        Assert.True(policy.Allows(PermissionMode.ViewOnly));
        Assert.False(policy.Allows(PermissionMode.FullControl));
        Assert.False(policy.Allows(PermissionMode.FileTransfer));
        Assert.True(policy.MfaRequired);
    }

    [Fact]
    public void DisabledCustomerCannotLogin()
    {
        var account = new CustomerAccount(Guid.NewGuid(), "person@example.com", "Person", "framework-password-hash", Now);
        account.VerifyEmail();
        account.Disable();

        Assert.False(account.IsLoginAllowed(Now.AddMinutes(1), emailVerificationRequired: true));
    }

    [Fact]
    public void ReleaseRejectsUnsignedManifestDigest()
    {
        Assert.Throws<ArgumentException>(() => new AppRelease(Guid.NewGuid(), "1.0.0", InstallChannel.Stable,
            ArchitectureKind.X64, Now, "1.0.0", "1.0.0", 100, "unsigned",
            new Uri("https://download.peeronq.com/1.0.0/x64.msi"), 1024, new string('c', 64)));
    }
}
