using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Observability;

namespace PeerOnQ.Admin.Api;

public sealed class AdminRequestContext(IPrivacyHasher privacyHasher)
{
    public string ChallengeContext(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userAgent = UserAgentSummary(context);
        return Convert.ToHexString(privacyHasher.ComputeHash("admin-mfa-context", $"{address}|{userAgent}"));
    }

    public string IpRiskMetadata(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var hash = Convert.ToHexString(privacyHasher.ComputeHash("admin-ip-risk", address));
        return $"ip_hash={hash[..16]}";
    }

    public static string UserAgentSummary(HttpContext context)
    {
        var agent = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(agent)) return "Unknown";
        var known = new[] { "Edge", "Edg/", "Chrome/", "Firefox/", "Safari/", "PeerOnQ-Admin/" };
        var family = known.FirstOrDefault(item => agent.Contains(item, StringComparison.OrdinalIgnoreCase));
        return family?.TrimEnd('/') ?? "Other";
    }

    public static string CorrelationId(HttpContext context) =>
        context.Response.Headers[RequestContextMiddleware.CorrelationHeader].FirstOrDefault()
        ?? context.Request.Headers[RequestContextMiddleware.CorrelationHeader].FirstOrDefault()
        ?? context.TraceIdentifier;
}

public sealed class AdminAuditWriter(IAuditRepository audit)
{
    public void Add(
        HttpContext context,
        string actorId,
        string action,
        string targetType,
        string? targetId,
        AuditResult result,
        string? reason,
        AdminRequestContext requestContext,
        DateTimeOffset now)
    {
        audit.Add(new AuditEvent(
            Guid.NewGuid(),
            actorId,
            "AdminUser",
            action,
            targetType,
            targetId,
            result,
            now,
            requestContext.IpRiskMetadata(context),
            AdminRequestContext.UserAgentSummary(context),
            AdminRequestContext.CorrelationId(context),
            reason));
    }
}
