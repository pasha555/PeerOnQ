using PeerOnQ.Domain.Sessions;
using PeerOnQ.Shared.Contracts.Security;
using PeerOnQ.Signaling.Server.Security;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class OrganizationSessionPolicyTests
{
    [Fact]
    public void AccountlessUnmanagedDevicesRemainAllowed()
    {
        Assert.True(OrganizationSessionPolicy.AllowsSession(null, SignalingOrganizationPolicyFlags.Unmanaged,
            null, SignalingOrganizationPolicyFlags.Unmanaged, "full-control",
            (int)SessionPermissionPolicy.ForMode(SessionMode.FullControl), "attended"));
    }

    [Fact]
    public void ManagedPolicyIsEnforcedForBothParticipants()
    {
        var organizationId = Guid.NewGuid();
        var viewOnly = SignalingOrganizationPolicyFlags.ViewOnly;

        Assert.True(OrganizationSessionPolicy.AllowsSession(organizationId, viewOnly, organizationId, viewOnly,
            "view-only", (int)SessionPermission.ViewScreen, "attended"));
        Assert.False(OrganizationSessionPolicy.AllowsSession(organizationId, viewOnly, organizationId, viewOnly,
            "full-control", (int)SessionPermissionPolicy.ForMode(SessionMode.FullControl), "attended"));
        Assert.False(OrganizationSessionPolicy.AllowsSession(organizationId, viewOnly, organizationId, viewOnly,
            "view-only", (int)SessionPermission.ViewScreen, "unattended"));
    }

    [Fact]
    public void ManagedAudioNeedsIndependentSignedOptInForBothParticipantsAndAttendedViewing()
    {
        var organization = Guid.NewGuid();
        var flags = SignalingOrganizationPolicyFlags.ViewOnly | SignalingOrganizationPolicyFlags.FullControl;
        var audioFlags = flags | SignalingOrganizationPolicyFlags.SystemAudio;
        var permissions = (int)(SessionPermission.ViewScreen | SessionPermission.SystemAudio);
        Assert.False(OrganizationSessionPolicy.AllowsSession(organization, flags, organization, flags, "view-only", permissions, "attended"));
        Assert.False(OrganizationSessionPolicy.AllowsSession(organization, audioFlags, organization, flags, "view-only", permissions, "attended"));
        Assert.False(OrganizationSessionPolicy.AllowsSession(organization, flags, organization, audioFlags, "view-only", permissions, "attended"));
        Assert.True(OrganizationSessionPolicy.AllowsSession(organization, audioFlags, organization, audioFlags, "view-only", permissions, "attended"));
        Assert.False(OrganizationSessionPolicy.AllowsSession(organization, audioFlags, organization, audioFlags, "view-only", permissions, "unattended"));
        Assert.False(OrganizationSessionPolicy.AllowsSession(organization, audioFlags, organization, audioFlags, "full-control", (int)SessionPermission.SystemAudio, "attended"));
        Assert.Equal(32, SignalingOrganizationPolicyFlags.HybridRequired);
        Assert.Equal(64, SignalingOrganizationPolicyFlags.SystemAudio);
        Assert.True(OrganizationSessionPolicy.AllowsSession(organization, flags, organization, flags, "full-control", (int)(SessionPermission.ViewScreen | SessionPermission.ControlInput), "attended"));
    }

    [Fact]
    public void CrossTenantManagedSessionFailsClosed()
    {
        var flags = SignalingOrganizationPolicyFlags.ViewOnly | SignalingOrganizationPolicyFlags.FullControl;
        Assert.False(OrganizationSessionPolicy.AllowsSession(Guid.NewGuid(), flags, Guid.NewGuid(), flags,
            "view-only", (int)SessionPermission.ViewScreen, "attended"));
    }

    [Fact]
    public void UnsupportedHybridRequirementFailsClosedAndRelayRegionIsScoped()
    {
        var organizationId = Guid.NewGuid();
        var flags = SignalingOrganizationPolicyFlags.ViewOnly | SignalingOrganizationPolicyFlags.HybridRequired;
        Assert.False(OrganizationSessionPolicy.AllowsSession(organizationId, flags, organizationId, flags,
            "view-only", (int)SessionPermission.ViewScreen, "attended"));
        Assert.True(OrganizationSessionPolicy.AllowsRelayRegion(organizationId, "eu-west,us-east", "EU-WEST"));
        Assert.False(OrganizationSessionPolicy.AllowsRelayRegion(organizationId, "eu-west,us-east", "ap-south"));
    }
}
