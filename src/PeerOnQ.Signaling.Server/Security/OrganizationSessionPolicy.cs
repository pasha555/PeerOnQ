using PeerOnQ.Domain.Sessions;
using PeerOnQ.Shared.Contracts.Security;

namespace PeerOnQ.Signaling.Server.Security;

public static class OrganizationSessionPolicy
{
    public static bool AllowsSession(Guid? requesterOrganizationId, int requesterFlags, Guid? targetOrganizationId,
        int targetFlags, string mode, int rawPermissions, string accessKind)
    {
        if (requesterOrganizationId != targetOrganizationId &&
            (requesterOrganizationId is not null || targetOrganizationId is not null)) return false;
        return Allows(requesterOrganizationId, requesterFlags, mode, rawPermissions, accessKind) &&
               Allows(targetOrganizationId, targetFlags, mode, rawPermissions, accessKind);
    }

    public static bool AllowsRelayRegion(Guid? organizationId, string approvedRegionsCsv, string region)
    {
        if (organizationId is null || string.IsNullOrWhiteSpace(approvedRegionsCsv)) return true;
        return approvedRegionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(region, StringComparer.OrdinalIgnoreCase);
    }

    private static bool Allows(Guid? organizationId, int flags, string mode, int raw, string accessKind)
    {
        if (organizationId is null) return true;
        if ((flags & SignalingOrganizationPolicyFlags.HybridRequired) != 0) return false;
        if (accessKind == "unattended" && (flags & SignalingOrganizationPolicyFlags.Unattended) == 0) return false;
        var permissions = (SessionPermission)raw;
        if (mode == "view-only" && (flags & SignalingOrganizationPolicyFlags.ViewOnly) == 0) return false;
        if (mode == "full-control" && (flags & SignalingOrganizationPolicyFlags.FullControl) == 0) return false;
        if (mode == "file-transfer-only" && (flags & SignalingOrganizationPolicyFlags.FileTransfer) == 0) return false;
        if ((permissions & SessionPermission.FileTransfer) != 0 && (flags & SignalingOrganizationPolicyFlags.FileTransfer) == 0) return false;
        if ((permissions & (SessionPermission.ClipboardText | SessionPermission.ClipboardImage)) != 0 &&
            (flags & SignalingOrganizationPolicyFlags.Clipboard) == 0) return false;
        if ((permissions & SessionPermission.ControlInput) != 0 && (flags & SignalingOrganizationPolicyFlags.FullControl) == 0) return false;
        if ((permissions & SessionPermission.ViewScreen) != 0 &&
            (flags & (SignalingOrganizationPolicyFlags.ViewOnly | SignalingOrganizationPolicyFlags.FullControl)) == 0) return false;
        return true;
    }
}
