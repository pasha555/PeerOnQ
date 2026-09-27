using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Infrastructure;
using PeerOnQ.Observability;

namespace PeerOnQ.Presence.Server;

public static class PresenceApp
{
    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddPeerOnQWebSecurity(32 * 1024);
        builder.Services.AddSingleton<PresenceConnectionTracker>();
        builder.AddPeerOnQServiceDefaults("PeerOnQ.Presence.Server", services =>
        {
            var tracker = services.GetRequiredService<PresenceConnectionTracker>();
            return new PeerOnQMetricState
            {
                OnlineDevices = () => tracker.Count,
                PresenceConnections = () => tracker.Count,
            };
        });
        builder.Services.AddPeerOnQCloudInfrastructure(builder.Configuration, builder.Environment);
        builder.Services.AddPeerOnQDedicatedDeviceTokenValidation(builder.Configuration);
        builder.Services.AddPeerOnQCloudDependencyHealthChecks();
        builder.Services.AddPeerOnQDeviceTokenValidationHealthCheck();
        builder.Services.AddOptions<PresenceOptions>()
            .Bind(builder.Configuration.GetSection(PresenceOptions.SectionName))
            .PostConfigure(options => options.Region = builder.Configuration["Region:Id"] ?? options.Region)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<PresenceOptions>, PresenceOptionsValidator>();
        builder.Services.AddPeerOnQDeviceAuthentication(options =>
        {
            options.AllowQueryToken = true;
            options.QueryTokenPath = PresenceHub.Path;
            options.RequireHttpsForQueryToken = !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing");
        });

        var redis = builder.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is required for the presence backplane.");
        builder.Services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumReceiveMessageSize = 16 * 1024;
            options.StreamBufferCapacity = 4;
            options.MaximumParallelInvocationsPerClient = 1;
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(75);
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.HandshakeTimeout = TimeSpan.FromSeconds(10);
        }).AddStackExchangeRedis(redis);
        builder.Services.AddHostedService<PresenceGracefulShutdown>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.ContentType = "application/problem+json";
                await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many presence connection attempts.",
                    Type = "https://docs.peeronq.com/problems/rate_limited",
                    Extensions = { ["code"] = "rate_limited" },
                }, cancellationToken);
            };
            options.AddPolicy("presence-connect", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });

        var app = builder.Build();
        app.UsePeerOnQServiceDefaults();
        app.UsePeerOnQWebSecurity();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapHub<PresenceHub>(PresenceHub.Path).RequireRateLimiting("presence-connect");
        return app;
    }
}
