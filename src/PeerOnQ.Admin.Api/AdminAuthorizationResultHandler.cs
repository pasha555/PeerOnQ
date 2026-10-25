using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace PeerOnQ.Admin.Api;

public sealed class AdminAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged || authorizeResult.Forbidden)
        {
            var status = authorizeResult.Challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = status,
                Title = authorizeResult.Challenged ? "Admin authentication is required." : "The admin role is not authorized.",
                Type = authorizeResult.Challenged
                    ? "https://docs.peeronq.com/problems/admin_authentication_required"
                    : "https://docs.peeronq.com/problems/admin_forbidden",
                Extensions = { ["code"] = authorizeResult.Challenged ? "admin_authentication_required" : "admin_forbidden" },
            });
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
