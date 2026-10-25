using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PeerOnQ.Observability;

public sealed class RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> logger)
{
    public const string CorrelationHeader = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context, PeerOnQMetrics metrics)
    {
        var correlationId = GetCorrelationId(context.Request.Headers[CorrelationHeader]);
        context.Response.Headers[CorrelationHeader] = correlationId;

        var started = Stopwatch.GetTimestamp();
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = Activity.Current?.TraceId.ToString() ?? string.Empty,
            ["RequestId"] = context.TraceIdentifier,
        });

        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var route = context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.RouteEndpoint>()?.RoutePattern.RawText
                ?? "unmatched";
            metrics.RequestCompleted(elapsed, context.Request.Method, route, context.Response.StatusCode);
        }
    }

    private static string GetCorrelationId(string? proposed)
    {
        if (!string.IsNullOrWhiteSpace(proposed)
            && proposed.Length is >= 16 and <= 64
            && proposed.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            return proposed;
        }

        return Guid.NewGuid().ToString("N");
    }
}
