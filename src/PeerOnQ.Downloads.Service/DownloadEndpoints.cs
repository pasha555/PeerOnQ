using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Observability;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Downloads.Service;

public static class DownloadEndpoints
{
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/windows/latest", StreamLatestAsync).RequireRateLimiting("downloads");
        endpoints.MapGet("/windows/{version}/{architecture}", StreamVersionAsync).RequireRateLimiting("downloads");

        var tracking = endpoints.MapGroup("/v1/downloads").RequireRateLimiting("tracking");
        tracking.MapPost("/start", StartAsync);
        tracking.MapPost("/complete", CompleteAsync);
        return endpoints;
    }

    private static Task StreamLatestAsync(
        HttpContext context,
        DownloadStreamingService streaming,
        string? architecture,
        string? channel,
        CancellationToken cancellationToken) =>
        streaming.StreamAsync(
            context,
            PlatformKindV1.Windows,
            ParseArchitecture(architecture),
            ParseChannel(channel),
            null,
            cancellationToken);

    private static Task StreamVersionAsync(
        HttpContext context,
        DownloadStreamingService streaming,
        string version,
        string architecture,
        CancellationToken cancellationToken) =>
        streaming.StreamAsync(
            context,
            PlatformKindV1.Windows,
            ParseArchitecture(architecture),
            ParseChannel(context.Request.Query["channel"].FirstOrDefault()),
            ValidateVersion(version),
            cancellationToken);

    private static async Task<IResult> StartAsync(
        DownloadStartRequestV1 request,
        HttpContext context,
        IDownloadTrackingService tracking,
        DownloadCompletionTokenService tokenService,
        TimeProvider timeProvider,
        PeerOnQMetrics metrics,
        CancellationToken cancellationToken)
    {
        var sanitized = request with
        {
            Source = DownloadRequestPrivacy.NormalizeSource(request.Source),
            Campaign = DownloadRequestPrivacy.NormalizeCampaign(request.Campaign),
            CountryCode = null,
            UserAgentFamily = DownloadRequestPrivacy.UserAgentFamily(context.Request.Headers.UserAgent),
        };
        var result = await tracking.StartAsync(sanitized, DownloadRequestPrivacy.UniquenessScope(context, timeProvider), cancellationToken);
        context.Response.Headers["X-PeerOnQ-Download-Token"] = tokenService.Issue(result.DownloadId);
        context.Response.Headers.CacheControl = "no-store";
        metrics.DownloadStarted(request.Platform.ToString(), request.Architecture.ToString());
        return Results.Ok(result);
    }

    private static async Task<IResult> CompleteAsync(
        DownloadCompleteRequestV1 request,
        HttpContext context,
        IDownloadTrackingService tracking,
        DownloadCompletionTokenService tokenService,
        PeerOnQMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (!tokenService.Validate(request.DownloadId, context.Request.Headers["X-PeerOnQ-Download-Token"].FirstOrDefault()))
            throw new UnauthorizedAccessException("The download completion token is invalid.");
        await tracking.CompleteAsync(request, cancellationToken);
        metrics.DownloadCompleted(request.Result switch
        {
            DownloadResultV1.Completed => "success",
            DownloadResultV1.Partial => "partial",
            _ => "failure",
        });
        return Results.NoContent();
    }

    private static ArchitectureKindV1 ParseArchitecture(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "x64" => ArchitectureKindV1.X64,
        "arm64" => ArchitectureKindV1.Arm64,
        _ => throw new ArgumentException("The requested architecture is unsupported."),
    };

    private static InstallChannelV1 ParseChannel(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "stable" => InstallChannelV1.Stable,
        "beta" => InstallChannelV1.Beta,
        _ => throw new ArgumentException("The requested release channel is unsupported."),
    };

    private static string ValidateVersion(string value) =>
        value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            ? value
            : throw new ArgumentException("The requested version is invalid.");
}
