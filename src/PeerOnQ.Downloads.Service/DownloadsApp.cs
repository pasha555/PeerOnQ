using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Infrastructure;
using PeerOnQ.Observability;

namespace PeerOnQ.Downloads.Service;

public static class DownloadsApp
{
    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddPeerOnQWebSecurity(64 * 1024);
        builder.AddPeerOnQServiceDefaults("PeerOnQ.Downloads.Service");
        builder.Services.AddPeerOnQCloudInfrastructure(builder.Configuration, builder.Environment);
        builder.Services.AddPeerOnQCloudDependencyHealthChecks();

        builder.Services.AddOptions<DownloadsOptions>()
            .Bind(builder.Configuration.GetSection(DownloadsOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<DownloadsOptions>, DownloadsOptionsValidator>();
        builder.Services.AddSingleton<DownloadCompletionTokenService>();
        builder.Services.AddSingleton<DownloadStreamGate>();
        builder.Services.AddSingleton<VerifiedArtifactCache>();
        builder.Services.AddScoped<DownloadStreamingService>();
        builder.Services.AddHttpClient("release-artifacts", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PeerOnQ-Downloads/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        });
        AddRateLimiting(builder.Services);

        var app = builder.Build();
        app.UsePeerOnQServiceDefaults();
        app.UsePeerOnQWebSecurity();
        app.UseRateLimiter();
        app.MapDownloadEndpoints();
        return app;
    }

    private static void AddRateLimiting(IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests.",
                    Type = "https://docs.peeronq.com/problems/rate_limited",
                    Extensions = { ["code"] = "rate_limited" },
                }, cancellationToken);
            };
            options.AddPolicy("downloads", context => Fixed(context, 30, TimeSpan.FromMinutes(1)));
            options.AddPolicy("tracking", context => Fixed(context, 120, TimeSpan.FromMinutes(1)));
        });
    }

    private static RateLimitPartition<string> Fixed(HttpContext context, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
}
