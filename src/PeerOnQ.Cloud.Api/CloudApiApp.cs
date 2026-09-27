using System.Threading.RateLimiting;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using PeerOnQ.Cloud.Infrastructure;
using PeerOnQ.Cloud.Infrastructure.Security;
using PeerOnQ.Observability;

namespace PeerOnQ.Cloud.Api;

public static class CloudApiApp
{
    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddPeerOnQWebSecurity(DiagnosticStorageOptions.MaximumArchiveBytes + (256 * 1024));
        builder.Services.AddSingleton<CloudMetricGaugeState>();
        builder.AddPeerOnQServiceDefaults(
            "PeerOnQ.Cloud.Api",
            services => services.GetRequiredService<CloudMetricGaugeState>().Metrics);
        builder.Services.AddPeerOnQCloudInfrastructure(builder.Configuration, builder.Environment);
        builder.Services.AddPeerOnQSignalingAttestationIssuer(builder.Configuration);
        builder.Services.AddPeerOnQCloudDependencyHealthChecks();
        builder.Services.AddPeerOnQDeviceAuthentication();
        builder.Services.AddCustomerPortalAuthentication(builder.Configuration, builder.Environment);
        AddDiagnosticStorage(builder);
        builder.Services.AddSingleton<DiagnosticUploadGate>();
        AddRateLimiting(builder.Services);
        builder.Services.AddHostedService<CloudMetricGaugeWorker>();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.MaxDepth = 32);
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = DiagnosticStorageOptions.MaximumArchiveBytes + (256 * 1024);
            options.MemoryBufferThreshold = 64 * 1024;
            options.ValueLengthLimit = 8 * 1024;
            options.ValueCountLimit = 16;
            options.MultipartHeadersCountLimit = 16;
            options.MultipartHeadersLengthLimit = 8 * 1024;
        });

        var app = builder.Build();
        app.UsePeerOnQServiceDefaults();
        app.UsePeerOnQWebSecurity();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapCloudApiV1();
        app.MapCustomerPortalV1();
        return app;
    }

    private static void AddDiagnosticStorage(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<DiagnosticStorageOptions>()
            .Bind(builder.Configuration.GetSection(DiagnosticStorageOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<DiagnosticStorageOptions>, DiagnosticStorageOptionsValidator>();
        builder.Services.AddHttpClient("diagnostic-storage", client => client.Timeout = TimeSpan.FromMinutes(3))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
            });
        builder.Services.AddScoped<DiagnosticUploadProcessor>();
        builder.Services.AddSingleton<IDiagnosticBlobStore>(services =>
        {
            var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DiagnosticStorageOptions>>().Value;
            return options.Provider.Equals("FileSystem", StringComparison.OrdinalIgnoreCase)
                ? ActivatorUtilities.CreateInstance<FileSystemDiagnosticBlobStore>(services)
                : ActivatorUtilities.CreateInstance<HttpDiagnosticBlobStore>(services);
        });
        builder.Services.AddSingleton<PeerOnQ.Cloud.Application.Abstractions.IDiagnosticBlobLifecycleStore>(services =>
            services.GetRequiredService<IDiagnosticBlobStore>());
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
            options.AddPolicy("registration", context => Fixed(context, 10, TimeSpan.FromMinutes(1)));
            options.AddPolicy("authentication", context => Fixed(context, 8, TimeSpan.FromMinutes(1)));
            options.AddPolicy("device", context => Fixed(context, 120, TimeSpan.FromMinutes(1)));
            options.AddPolicy("telemetry", context => Fixed(context, 300, TimeSpan.FromMinutes(1)));
            options.AddPolicy("diagnostics", context => Fixed(context, 12, TimeSpan.FromMinutes(1)));
            options.AddPolicy("customer-registration", context => Fixed(context, 5, TimeSpan.FromMinutes(10)));
            options.AddPolicy("customer-authentication", context => Fixed(context, 10, TimeSpan.FromMinutes(5)));
            options.AddPolicy("customer-sensitive", context => Fixed(context, 30, TimeSpan.FromMinutes(1)));
        });
    }

    private static RateLimitPartition<string> Fixed(HttpContext context, int permitLimit, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.IsAuthenticated == true
                ? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? context.User.FindFirst(DeviceAccessAuthentication.InstallationIdClaim)?.Value
                    ?? "authenticated"
                : context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
}

public sealed class DiagnosticUploadGate : IDisposable
{
    private const int MaximumConcurrentUploads = 2;
    private readonly SemaphoreSlim _slots = new(MaximumConcurrentUploads, MaximumConcurrentUploads);

    public async Task<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _slots.WaitAsync(TimeSpan.Zero, cancellationToken)) return null;
        return new Lease(_slots);
    }

    public void Dispose() => _slots.Dispose();

    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;
        public void Dispose() => Interlocked.Exchange(ref _slots, null)?.Release();
    }
}
