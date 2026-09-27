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
