namespace PeerOnQ.Cloud.Api;

public static class CustomerOrganizationEndpoints
{
    public static RouteGroupBuilder MapCustomerOrganizations(this RouteGroupBuilder portal)
    {
        var organizations = portal.MapGroup("/organizations").RequireAuthorization(CustomerPortalAuthentication.Policy);
        organizations.MapGet("/", async (HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(context.User.RequireCustomerAccountId(), cancellationToken)));
        organizations.MapPost("/", CreateOrganizationAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapGet("/{organizationId:guid}/members", async (Guid organizationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListMembersAsync(context.User.RequireCustomerAccountId(), organizationId, cancellationToken)));
        organizations.MapPut("/{organizationId:guid}/members/{memberAccountId:guid}/role", ChangeRoleAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapDelete("/{organizationId:guid}/members/{memberAccountId:guid}", RemoveMemberAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapPost("/{organizationId:guid}/transfer-ownership", TransferOwnershipAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapDelete("/{organizationId:guid}", DeleteOrganizationAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapGet("/{organizationId:guid}/invitations", async (Guid organizationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListInvitationsAsync(context.User.RequireCustomerAccountId(), organizationId, cancellationToken)));
        organizations.MapPost("/{organizationId:guid}/invitations", InviteAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapDelete("/{organizationId:guid}/invitations/{invitationId:guid}", RevokeInvitationAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapPost("/invitations/accept", AcceptInvitationAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapGet("/{organizationId:guid}/teams", async (Guid organizationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListTeamsAsync(context.User.RequireCustomerAccountId(), organizationId, cancellationToken)));
        organizations.MapPost("/{organizationId:guid}/teams", CreateTeamAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapPost("/{organizationId:guid}/teams/{teamId:guid}/members", AddTeamMemberAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapDelete("/{organizationId:guid}/teams/{teamId:guid}/members/{memberAccountId:guid}", RemoveTeamMemberAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapGet("/{organizationId:guid}/policy", async (Guid organizationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetPolicyAsync(context.User.RequireCustomerAccountId(), organizationId, cancellationToken)));
        organizations.MapPut("/{organizationId:guid}/policy", UpdatePolicyAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapPost("/{organizationId:guid}/policy/evaluate", EvaluatePolicyAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapGet("/{organizationId:guid}/devices", async (Guid organizationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListDevicesAsync(context.User.RequireCustomerAccountId(), organizationId, cancellationToken)));
        organizations.MapPost("/{organizationId:guid}/devices/{deviceId:guid}/claim", ClaimDeviceAsync).RequireRateLimiting("customer-sensitive");
        organizations.MapGet("/{organizationId:guid}/sessions", async (Guid organizationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListSessionsAsync(context.User.RequireCustomerAccountId(), organizationId, cancellationToken)));
        organizations.MapGet("/{organizationId:guid}/audit", async (Guid organizationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAuditAsync(context.User.RequireCustomerAccountId(), organizationId, cancellationToken)));
        organizations.MapGet("/{organizationId:guid}/audit/export", ExportAuditAsync).RequireRateLimiting("customer-sensitive");
        return portal;
    }

    private static async Task<IResult> CreateOrganizationAsync(CreateOrganizationRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        var result = await service.CreateAsync(context.User.RequireCustomerAccountId(), request.Name, cancellationToken);
        return Results.Created($"/portal/v1/organizations/{result.Id:D}", result);
    }

    private static async Task<IResult> ChangeRoleAsync(Guid organizationId, Guid memberAccountId, ChangeOrganizationRoleRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.ChangeRoleAsync(context.User.RequireCustomerAccountId(), organizationId, memberAccountId, request.Role, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveMemberAsync(Guid organizationId, Guid memberAccountId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.RemoveMemberAsync(context.User.RequireCustomerAccountId(), organizationId, memberAccountId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> TransferOwnershipAsync(Guid organizationId, TransferOrganizationOwnershipRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.TransferOwnershipAsync(context.User.RequireCustomerAccountId(), organizationId, request.NewOwnerAccountId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteOrganizationAsync(Guid organizationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.RequestDeletionAsync(context.User.RequireCustomerAccountId(), organizationId, cancellationToken);
        return Results.Accepted();
    }

    private static async Task<IResult> InviteAsync(Guid organizationId, InviteOrganizationMemberRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        return Results.Accepted(value: await service.InviteAsync(context.User.RequireCustomerAccountId(), organizationId, request, cancellationToken));
    }

    private static async Task<IResult> RevokeInvitationAsync(Guid organizationId, Guid invitationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.RevokeInvitationAsync(context.User.RequireCustomerAccountId(), organizationId, invitationId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AcceptInvitationAsync(AcceptOrganizationInvitationRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.AcceptInvitationAsync(context.User.RequireCustomerAccountId(), request.Token, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateTeamAsync(Guid organizationId, CreateTeamRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        var team = await service.CreateTeamAsync(context.User.RequireCustomerAccountId(), organizationId, request.Name, cancellationToken);
        return Results.Created($"/portal/v1/organizations/{organizationId:D}/teams/{team.Id:D}", team);
    }

    private static async Task<IResult> AddTeamMemberAsync(Guid organizationId, Guid teamId, TeamMemberRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.AddTeamMemberAsync(context.User.RequireCustomerAccountId(), organizationId, teamId, request.AccountId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveTeamMemberAsync(Guid organizationId, Guid teamId, Guid memberAccountId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.RemoveTeamMemberAsync(context.User.RequireCustomerAccountId(), organizationId, teamId, memberAccountId, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdatePolicyAsync(Guid organizationId, UpdateOrganizationPolicyRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.UpdatePolicyAsync(context.User.RequireCustomerAccountId(), organizationId, request, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> EvaluatePolicyAsync(Guid organizationId, EvaluateOrganizationPolicyRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.EvaluatePolicyAsync(context.User.RequireCustomerAccountId(), organizationId, request, cancellationToken));

    private static async Task<IResult> ExportAuditAsync(Guid organizationId, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        var result = await service.ListAuditAsync(context.User.RequireCustomerAccountId(), organizationId, cancellationToken);
        return Results.Json(new { organizationId, exportedAtUtc = DateTimeOffset.UtcNow, events = result }, contentType: "application/json");
    }

    private static async Task<IResult> ClaimDeviceAsync(Guid organizationId, Guid deviceId, ClaimOrganizationDeviceRequest request, HttpContext context, CustomerOrganizationService service, CancellationToken cancellationToken)
    {
        Mutating(context);
        await service.ClaimDeviceAsync(context.User.RequireCustomerAccountId(), organizationId, deviceId, request.DeviceAccessToken, cancellationToken);
        return Results.NoContent();
    }

    private static void Mutating(HttpContext context) => CustomerPortalAuthentication.ValidateCsrf(context);
}
