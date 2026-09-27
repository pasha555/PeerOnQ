using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Observability;

namespace PeerOnQ.Cloud.Api;

public sealed class CustomerOrganizationService(
    CloudDbContext db,
    ICustomerMailSender mail,
    IDeviceAccessTokenIssuer deviceAccessTokens,
    IOptions<CustomerPortalOptions> options,
    IHttpContextAccessor httpContextAccessor)
{
    private static readonly CustomerRoleKind[] Managers = [CustomerRoleKind.Owner, CustomerRoleKind.Administrator];

    public async Task<IReadOnlyList<OrganizationSummary>> ListAsync(Guid accountId, CancellationToken cancellationToken) =>
        await (from membership in db.OrganizationMemberships.AsNoTracking()
               join organization in db.Organizations.AsNoTracking() on membership.OrganizationId equals organization.Id
               where membership.AccountId == accountId && membership.RevokedAtUtc == null && organization.DeletionRequestedAtUtc == null
               orderby organization.Name
               select new OrganizationSummary(organization.Id, organization.Name, membership.Role, organization.OwnerAccountId))
            .ToListAsync(cancellationToken);

    public async Task<OrganizationSummary> CreateAsync(Guid accountId, string name, CancellationToken cancellationToken)
    {
        await RequireActiveAccountAsync(accountId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var organization = new Organization(Guid.NewGuid(), name, accountId, now);
        db.Organizations.Add(organization);
        db.OrganizationMemberships.Add(new OrganizationMembership(organization.Id, accountId, CustomerRoleKind.Owner, now));
        db.OrganizationPolicies.Add(new OrganizationPolicy(organization.Id));
        AddEvent(accountId, organization.Id, "organization.created", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
        return new OrganizationSummary(organization.Id, organization.Name, CustomerRoleKind.Owner, accountId);
    }

    public async Task<IReadOnlyList<OrganizationMemberResult>> ListMembersAsync(Guid accountId, Guid organizationId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(accountId, organizationId, cancellationToken);
        return await (from membership in db.OrganizationMemberships.AsNoTracking()
                      join account in db.CustomerAccounts.AsNoTracking() on membership.AccountId equals account.Id
                      where membership.OrganizationId == organizationId && membership.RevokedAtUtc == null
                      orderby account.DisplayName
                      select new OrganizationMemberResult(account.Id, account.DisplayName, account.Email, membership.Role, membership.JoinedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task ChangeRoleAsync(Guid actorId, Guid organizationId, Guid memberAccountId, CustomerRoleKind role, CancellationToken cancellationToken)
    {
        var actor = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        var organization = await RequireOrganizationAsync(organizationId, cancellationToken);
        if (role == CustomerRoleKind.Owner || memberAccountId == organization.OwnerAccountId)
            throw Problem(StatusCodes.Status409Conflict, "owner_transfer_required", "Use the ownership-transfer operation for the organization owner.");
        if (actor.Role != CustomerRoleKind.Owner && role == CustomerRoleKind.Administrator)
            throw new UnauthorizedAccessException();
        var member = await RequireMembershipAsync(memberAccountId, organizationId, cancellationToken);
        member.ChangeRole(role);
        AddEvent(actorId, organizationId, "organization.member_role_changed", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMemberAsync(Guid actorId, Guid organizationId, Guid memberAccountId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        var organization = await RequireOrganizationAsync(organizationId, cancellationToken);
        if (memberAccountId == organization.OwnerAccountId)
            throw Problem(StatusCodes.Status409Conflict, "owner_transfer_required", "Transfer ownership before removing the owner.");
        var membership = await RequireMembershipAsync(memberAccountId, organizationId, cancellationToken);
        membership.Revoke(DateTimeOffset.UtcNow);
        var teamMemberships = await (from tm in db.TeamMemberships
                                     join team in db.Teams on tm.TeamId equals team.Id
                                     where team.OrganizationId == organizationId && tm.AccountId == memberAccountId
                                     select tm).ToListAsync(cancellationToken);
        db.TeamMemberships.RemoveRange(teamMemberships);
        AddEvent(actorId, organizationId, "organization.member_removed", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task TransferOwnershipAsync(Guid actorId, Guid organizationId, Guid newOwnerAccountId, CancellationToken cancellationToken)
    {
        var organization = await RequireOrganizationAsync(organizationId, cancellationToken);
        if (organization.OwnerAccountId != actorId) throw new UnauthorizedAccessException();
        var current = await RequireMembershipAsync(actorId, organizationId, cancellationToken);
        var next = await RequireMembershipAsync(newOwnerAccountId, organizationId, cancellationToken);
        organization.TransferOwnership(actorId, newOwnerAccountId);
        current.ChangeRole(CustomerRoleKind.Administrator);
        next.ChangeRole(CustomerRoleKind.Owner);
        AddEvent(actorId, organizationId, "organization.owner_transferred", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RequestDeletionAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken)
    {
        var organization = await RequireOrganizationAsync(organizationId, cancellationToken);
        var memberCount = await db.OrganizationMemberships.CountAsync(value =>
            value.OrganizationId == organizationId && value.RevokedAtUtc == null, cancellationToken);
        var deviceCount = await db.Devices.CountAsync(value => value.OwnerOrganizationId == organizationId && !value.IsRevoked, cancellationToken);
        if (deviceCount > 0) throw Problem(StatusCodes.Status409Conflict, "organization_devices_present", "Reassign or revoke organization devices before deletion.");
        organization.RequestDeletion(actorId, memberCount, DateTimeOffset.UtcNow);
        AddEvent(actorId, organizationId, "organization.deletion_requested", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvitationCreatedResult> InviteAsync(Guid actorId, Guid organizationId, InviteOrganizationMemberRequest request, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        mail.EnsureEnabled();
        if (request.Role == CustomerRoleKind.Owner) throw new ArgumentException("Ownership cannot be granted by invitation.");
        var email = CustomerAccount.NormalizeEmail(request.Email);
        if (await db.OrganizationMemberships.AnyAsync(value => value.OrganizationId == organizationId && value.RevokedAtUtc == null &&
            db.CustomerAccounts.Any(account => account.Id == value.AccountId && account.Email == email), cancellationToken))
            throw Problem(StatusCodes.Status409Conflict, "member_exists", "This account is already an organization member.");
        var now = DateTimeOffset.UtcNow;
        var rawToken = CustomerTokenService.IssueOpaqueToken();
        var invitation = new OrganizationInvitation(Guid.NewGuid(), organizationId, email, request.Role,
            CustomerTokenService.HashOpaqueToken(rawToken), actorId, now, now.AddMinutes(options.Value.InvitationMinutes));
        db.OrganizationInvitations.Add(invitation);
        AddEvent(actorId, organizationId, "organization.invitation_created", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
        var link = $"{options.Value.PortalBaseUrl.TrimEnd('/')}/invitations/accept?token={Uri.EscapeDataString(rawToken)}";
        await mail.SendAsync(email, "PeerOnQ organization invitation", $"Accept your invitation: {link}", cancellationToken);
        return new InvitationCreatedResult(invitation.Id, invitation.ExpiresAtUtc);
    }

    public async Task AcceptInvitationAsync(Guid accountId, string rawToken, CancellationToken cancellationToken)
    {
        var account = await RequireActiveAccountAsync(accountId, cancellationToken);
        var hash = CustomerTokenService.HashOpaqueToken(rawToken);
        var invitation = await db.OrganizationInvitations.SingleOrDefaultAsync(value => value.TokenHash.SequenceEqual(hash), cancellationToken)
            ?? throw Problem(StatusCodes.Status400BadRequest, "invitation_invalid", "The invitation is invalid, expired, revoked, already used, or belongs to another account.");
        if (!invitation.CanAccept(account.Email, hash, DateTimeOffset.UtcNow))
            throw Problem(StatusCodes.Status400BadRequest, "invitation_invalid", "The invitation is invalid, expired, revoked, already used, or belongs to another account.");
        if (await db.OrganizationMemberships.AnyAsync(value => value.OrganizationId == invitation.OrganizationId && value.AccountId == accountId && value.RevokedAtUtc == null, cancellationToken))
            throw Problem(StatusCodes.Status409Conflict, "member_exists", "The account is already an organization member.");
        invitation.Accept(account.Email, hash, DateTimeOffset.UtcNow);
        db.OrganizationMemberships.Add(new OrganizationMembership(invitation.OrganizationId, accountId, invitation.Role, DateTimeOffset.UtcNow));
        AddEvent(accountId, invitation.OrganizationId, "organization.invitation_accepted", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InvitationResult>> ListInvitationsAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        return await db.OrganizationInvitations.AsNoTracking().Where(value => value.OrganizationId == organizationId)
            .OrderByDescending(value => value.CreatedAtUtc)
            .Select(value => new InvitationResult(value.Id, value.Email, value.Role, value.CreatedAtUtc, value.ExpiresAtUtc, value.AcceptedAtUtc, value.RevokedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task RevokeInvitationAsync(Guid actorId, Guid organizationId, Guid invitationId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        var invitation = await db.OrganizationInvitations.SingleOrDefaultAsync(value => value.Id == invitationId && value.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException();
        invitation.Revoke(DateTimeOffset.UtcNow);
        AddEvent(actorId, organizationId, "organization.invitation_revoked", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TeamResult>> ListTeamsAsync(Guid accountId, Guid organizationId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(accountId, organizationId, cancellationToken);
        return await db.Teams.AsNoTracking().Where(value => value.OrganizationId == organizationId)
            .OrderBy(value => value.Name).Select(value => new TeamResult(value.Id, value.Name, value.CreatedAtUtc)).ToListAsync(cancellationToken);
    }

    public async Task<TeamResult> CreateTeamAsync(Guid actorId, Guid organizationId, string name, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        var team = new Team(Guid.NewGuid(), organizationId, name, DateTimeOffset.UtcNow);
        db.Teams.Add(team);
        AddEvent(actorId, organizationId, "organization.team_created", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
        return new TeamResult(team.Id, team.Name, team.CreatedAtUtc);
    }

    public async Task AddTeamMemberAsync(Guid actorId, Guid organizationId, Guid teamId, Guid memberAccountId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        _ = await RequireMembershipAsync(memberAccountId, organizationId, cancellationToken);
        if (!await db.Teams.AnyAsync(value => value.Id == teamId && value.OrganizationId == organizationId, cancellationToken)) throw new KeyNotFoundException();
        if (!await db.TeamMemberships.AnyAsync(value => value.TeamId == teamId && value.AccountId == memberAccountId, cancellationToken))
            db.TeamMemberships.Add(new TeamMembership(teamId, memberAccountId, DateTimeOffset.UtcNow));
        AddEvent(actorId, organizationId, "organization.team_member_added", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveTeamMemberAsync(Guid actorId, Guid organizationId, Guid teamId, Guid memberAccountId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        var membership = await (from tm in db.TeamMemberships join team in db.Teams on tm.TeamId equals team.Id
                                where team.OrganizationId == organizationId && tm.TeamId == teamId && tm.AccountId == memberAccountId select tm)
            .SingleOrDefaultAsync(cancellationToken) ?? throw new KeyNotFoundException();
        db.TeamMemberships.Remove(membership);
        AddEvent(actorId, organizationId, "organization.team_member_removed", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<OrganizationPolicyResult> GetPolicyAsync(Guid accountId, Guid organizationId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(accountId, organizationId, cancellationToken);
        var policy = await db.OrganizationPolicies.AsNoTracking().SingleAsync(value => value.OrganizationId == organizationId, cancellationToken);
        return ToResult(policy);
    }

    public async Task UpdatePolicyAsync(Guid actorId, Guid organizationId, UpdateOrganizationPolicyRequest request, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        var policy = await db.OrganizationPolicies.SingleAsync(value => value.OrganizationId == organizationId, cancellationToken);
        if (!options.Value.EnableMfa && request.MfaRequired)
            throw Problem(StatusCodes.Status400BadRequest, "customer_mfa_disabled", "Customer MFA is not available.");
        policy.Update(request.ViewOnlyAllowed, request.FullControlAllowed, request.FileTransferAllowed, request.ClipboardAllowed,
            request.UnattendedAccessAllowed, options.Value.EnableMfa ? request.MfaRequired : policy.MfaRequired, request.TrustedDeviceLifetimeDays, request.AuditRetentionDays,
            request.ApprovedRelayRegionsCsv, request.MinimumClientVersion, request.HybridSecurityRequired);
        AddEvent(actorId, organizationId, "organization.policy_updated", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PolicyEvaluationResult> EvaluatePolicyAsync(Guid accountId, Guid organizationId, EvaluateOrganizationPolicyRequest request, CancellationToken cancellationToken)
    {
        var membership = await RequireMembershipAsync(accountId, organizationId, cancellationToken);
        var account = await RequireActiveAccountAsync(accountId, cancellationToken);
        var policy = await db.OrganizationPolicies.AsNoTracking().SingleAsync(value => value.OrganizationId == organizationId, cancellationToken);
        var denied = new List<string>();
        if (!policy.Allows(request.Mode)) denied.Add("connection_mode_denied");
        if (request.Unattended && !policy.UnattendedAccessAllowed) denied.Add("unattended_access_denied");
        if (options.Value.EnableMfa && policy.MfaRequired && !account.MfaEnabled) denied.Add("mfa_required");
        if (policy.HybridSecurityRequired && !request.HybridSecurityActive) denied.Add("hybrid_security_required");
        if (!string.IsNullOrWhiteSpace(policy.ApprovedRelayRegionsCsv) && !string.IsNullOrWhiteSpace(request.RelayRegion) &&
            !policy.ApprovedRelayRegionsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(request.RelayRegion, StringComparer.OrdinalIgnoreCase))
            denied.Add("relay_region_denied");
        if (!string.IsNullOrWhiteSpace(policy.MinimumClientVersion) && Version.TryParse(policy.MinimumClientVersion, out var minimum) &&
            (!Version.TryParse(request.ClientVersion, out var actual) || actual < minimum)) denied.Add("client_version_denied");
        if (membership.Role == CustomerRoleKind.Auditor && request.Mode != PermissionMode.ViewOnly) denied.Add("role_denied");
        return new PolicyEvaluationResult(denied.Count == 0, denied);
    }

    public async Task<IReadOnlyList<OrganizationDeviceResult>> ListDevicesAsync(Guid accountId, Guid organizationId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(accountId, organizationId, cancellationToken);
        return await db.Devices.AsNoTracking().Where(value => value.OwnerOrganizationId == organizationId)
            .OrderBy(value => value.DisplayName)
            .Select(value => new OrganizationDeviceResult(value.Id, value.MaskedPublicDeviceId, value.DisplayName, value.LastSeenAtUtc, value.IsRevoked))
            .ToListAsync(cancellationToken);
    }

    public async Task ClaimDeviceAsync(Guid actorId, Guid organizationId, Guid deviceId, string deviceAccessToken, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(actorId, organizationId, cancellationToken, Managers);
        var principal = await deviceAccessTokens.ValidateAsync(deviceAccessToken, cancellationToken);
        if (principal is null || principal.DeviceId != deviceId)
            throw Problem(StatusCodes.Status401Unauthorized, "device_proof_required", "A current access token for this device is required.");
        var device = await db.Devices.SingleOrDefaultAsync(value => value.Id == deviceId, cancellationToken) ?? throw new KeyNotFoundException();
        if (device.OwnerOrganizationId is not null && device.OwnerOrganizationId != organizationId)
            throw new UnauthorizedAccessException();
        device.AssignOwner(actorId, organizationId, DateTimeOffset.UtcNow);
        AddEvent(actorId, organizationId, "organization.device_claimed", AuditResult.Succeeded);
        await db.SaveChangesAsync(cancellationToken);
        await deviceAccessTokens.RevokeAsync(deviceAccessToken, cancellationToken);
    }

    public async Task<IReadOnlyList<OrganizationSessionResult>> ListSessionsAsync(Guid accountId, Guid organizationId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(accountId, organizationId, cancellationToken);
        return await (from session in db.RemoteSessions.AsNoTracking()
                      join device in db.Devices.AsNoTracking() on session.HostDeviceId equals device.Id
                      where device.OwnerOrganizationId == organizationId
                      orderby session.StartedAtUtc descending
                      select new OrganizationSessionResult(session.Id, session.PermissionMode, session.ConnectionPath, session.Lifecycle, session.StartedAtUtc, session.EndedAtUtc))
            .Take(500).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OrganizationAuditResult>> ListAuditAsync(Guid accountId, Guid organizationId, CancellationToken cancellationToken)
    {
        _ = await RequireMembershipAsync(accountId, organizationId, cancellationToken);
        return await db.CustomerSecurityEvents.AsNoTracking().Where(value => value.OrganizationId == organizationId)
            .OrderByDescending(value => value.TimestampUtc)
            .Select(value => new OrganizationAuditResult(value.Id, value.Action, value.Result, value.TimestampUtc, value.CorrelationId, value.SafeMetadata))
            .Take(1000).ToListAsync(cancellationToken);
    }

    private async Task<OrganizationMembership> RequireMembershipAsync(Guid accountId, Guid organizationId, CancellationToken cancellationToken, params CustomerRoleKind[] allowed)
    {
        var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(value =>
            value.AccountId == accountId && value.OrganizationId == organizationId && value.RevokedAtUtc == null, cancellationToken);
        if (membership is null || (allowed.Length > 0 && !allowed.Contains(membership.Role))) throw new UnauthorizedAccessException();
        return membership;
    }

    private async Task<CustomerAccount> RequireActiveAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.CustomerAccounts.SingleOrDefaultAsync(value => value.Id == accountId && value.Status == CustomerAccountStatus.Active, cancellationToken);
        return account ?? throw new UnauthorizedAccessException();
    }

    private async Task<Organization> RequireOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await db.Organizations.SingleOrDefaultAsync(value => value.Id == organizationId && value.DeletionRequestedAtUtc == null, cancellationToken)
        ?? throw new KeyNotFoundException();

    private void AddEvent(Guid accountId, Guid organizationId, string action, AuditResult result) =>
        db.CustomerSecurityEvents.Add(new CustomerSecurityEvent(Guid.NewGuid(), accountId, organizationId, action, result,
            DateTimeOffset.UtcNow, httpContextAccessor.HttpContext?.TraceIdentifier ?? Guid.NewGuid().ToString("N"), null));

    private OrganizationPolicyResult ToResult(OrganizationPolicy value) => new(value.OrganizationId, value.ViewOnlyAllowed,
        value.FullControlAllowed, value.FileTransferAllowed, value.ClipboardAllowed, value.UnattendedAccessAllowed, options.Value.EnableMfa && value.MfaRequired,
        value.TrustedDeviceLifetimeDays, value.AuditRetentionDays, value.ApprovedRelayRegionsCsv, value.MinimumClientVersion, value.HybridSecurityRequired);
    private static ApiProblemException Problem(int status, string code, string title) => new(status, code, title);
}

public sealed record OrganizationSummary(Guid Id, string Name, CustomerRoleKind Role, Guid OwnerAccountId);
public sealed record CreateOrganizationRequest(string Name);
public sealed record OrganizationMemberResult(Guid AccountId, string DisplayName, string Email, CustomerRoleKind Role, DateTimeOffset JoinedAtUtc);
public sealed record ChangeOrganizationRoleRequest(CustomerRoleKind Role);
public sealed record TransferOrganizationOwnershipRequest(Guid NewOwnerAccountId);
public sealed record InviteOrganizationMemberRequest(string Email, CustomerRoleKind Role);
public sealed record InvitationCreatedResult(Guid Id, DateTimeOffset ExpiresAtUtc);
public sealed record AcceptOrganizationInvitationRequest(string Token);
public sealed record InvitationResult(Guid Id, string Email, CustomerRoleKind Role, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? AcceptedAtUtc, DateTimeOffset? RevokedAtUtc);
public sealed record TeamResult(Guid Id, string Name, DateTimeOffset CreatedAtUtc);
public sealed record CreateTeamRequest(string Name);
public sealed record TeamMemberRequest(Guid AccountId);
public sealed record OrganizationPolicyResult(Guid OrganizationId, bool ViewOnlyAllowed, bool FullControlAllowed, bool FileTransferAllowed,
    bool ClipboardAllowed, bool UnattendedAccessAllowed, bool MfaRequired, int TrustedDeviceLifetimeDays, int AuditRetentionDays,
    string ApprovedRelayRegionsCsv, string MinimumClientVersion, bool HybridSecurityRequired);
public sealed record UpdateOrganizationPolicyRequest(bool ViewOnlyAllowed, bool FullControlAllowed, bool FileTransferAllowed,
    bool ClipboardAllowed, bool UnattendedAccessAllowed, bool MfaRequired, int TrustedDeviceLifetimeDays, int AuditRetentionDays,
    string? ApprovedRelayRegionsCsv, string? MinimumClientVersion, bool HybridSecurityRequired);
public sealed record EvaluateOrganizationPolicyRequest(PermissionMode Mode, bool Unattended, string? RelayRegion, string? ClientVersion, bool HybridSecurityActive);
public sealed record PolicyEvaluationResult(bool Allowed, IReadOnlyList<string> Denials);
public sealed record OrganizationDeviceResult(Guid Id, string MaskedPublicDeviceId, string DisplayName, DateTimeOffset LastSeenAtUtc, bool IsRevoked);
public sealed record ClaimOrganizationDeviceRequest(string DeviceAccessToken);
public sealed record OrganizationSessionResult(Guid Id, PermissionMode PermissionMode, ConnectionPath ConnectionPath, SessionLifecycle Lifecycle, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc);
public sealed record OrganizationAuditResult(Guid Id, string Action, AuditResult Result, DateTimeOffset TimestampUtc, string CorrelationId, string? SafeMetadata);
