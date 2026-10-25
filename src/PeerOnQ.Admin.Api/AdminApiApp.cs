using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Infrastructure;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Observability;
using StackExchange.Redis;

namespace PeerOnQ.Admin.Api;

public static class AdminApiApp
{
    private const long DefaultRequestBodyBytes = 256 * 1024;

    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddPeerOnQWebSecurity(ReleasePublicationOptions.MaximumRequestBodyBytes);
        builder.WebHost.ConfigureKestrel(ConfigureAdminKestrelRequestLimit);
        builder.AddPeerOnQServiceDefaults("PeerOnQ.Admin.Api");
        builder.Services.AddPeerOnQCloudInfrastructure(builder.Configuration, builder.Environment);
        builder.Services.AddPeerOnQCloudDependencyHealthChecks();
        AddAdminAuthentication(builder);
        AddDataProtection(builder);
        AddAdminServices(builder.Services, builder.Configuration);
        AddInfrastructureMetrics(builder);
        AddAlertIngestion(builder);
        AddReleasePublication(builder);
        AddWebsitePublication(builder);
        AddPlatformUpgrade(builder);
        AddRateLimiting(builder.Services);
        AddCors(builder.Services, builder.Configuration, builder.Environment);

        var app = builder.Build();
        app.UsePeerOnQServiceDefaults();
        app.UsePeerOnQWebSecurity();
        if (app.Configuration.GetSection("AdminCors:AllowedOrigins").Get<string[]>() is { Length: > 0 })
            app.UseCors("admin");
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapAdminApi();
        return app;
    }

    internal static void ConfigureAdminKestrelRequestLimit(KestrelServerOptions options) =>
        options.Limits.MaxRequestBodySize = PlatformUpgradeOptions.MaximumRequestBodyBytes;

    private static void AddReleasePublication(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<ReleasePublicationOptions>()
            .Bind(builder.Configuration.GetSection(ReleasePublicationOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ReleasePublicationOptions>, ReleasePublicationOptionsValidator>();
        builder.Services.AddSingleton<ReleasePublicationVerifier>();
        builder.Services.AddSingleton<ReleaseUploadGate>();
        builder.Services.AddScoped<ReleasePublicationService>();
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = ReleasePublicationOptions.MaximumRequestBodyBytes;
            options.MemoryBufferThreshold = 64 * 1024;
            options.ValueLengthLimit = 8 * 1024;
            options.ValueCountLimit = 8;
            options.MultipartHeadersCountLimit = 16;
            options.MultipartHeadersLengthLimit = 8 * 1024;
        });
    }

    private static void AddWebsitePublication(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<WebsitePublicationOptions>()
            .Bind(builder.Configuration.GetSection(WebsitePublicationOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<WebsitePublicationOptions>, WebsitePublicationOptionsValidator>();
        builder.Services.AddSingleton<WebsitePublicationVerifier>();
        builder.Services.AddSingleton<WebsiteUploadGate>();
        builder.Services.AddScoped<WebsitePublicationService>();
    }

    private static void AddPlatformUpgrade(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<PlatformUpgradeOptions>()
            .Bind(builder.Configuration.GetSection(PlatformUpgradeOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<PlatformUpgradeOptions>, PlatformUpgradeOptionsValidator>();
        builder.Services.AddSingleton<PlatformUpgradeRequestSpool>();
        builder.Services.AddSingleton<IPlatformUpgradeStatusReader, PlatformUpgradeStatusReader>();
        builder.Services.AddSingleton<PlatformUpgradeUploadGate>();
        builder.Services.AddScoped<PlatformUpgradeService>();
    }

    private static void AddAlertIngestion(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AlertIngestionOptions>()
            .Bind(builder.Configuration.GetSection(AlertIngestionOptions.SectionName))
            .PostConfigure(options => options.DefaultRegion = builder.Configuration["Region:Id"] ?? options.DefaultRegion)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<AlertIngestionOptions>, AlertIngestionOptionsValidator>();
        builder.Services.AddSingleton<AlertIngestionTokenProvider>();
    }

    private static void AddAdminAuthentication(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AdminAuthenticationOptions>()
            .Bind(builder.Configuration.GetSection(AdminAuthenticationOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<AdminAuthenticationOptions>, AdminAuthenticationOptionsValidator>();
        var options = builder.Configuration.GetSection(AdminAuthenticationOptions.SectionName).Get<AdminAuthenticationOptions>()
            ?? new AdminAuthenticationOptions();
        AdminAuthenticationConfiguration.EnsureMfaBypassAllowed(builder.Environment, options);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.RequireHttpsMetadata = !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing");
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey ?? string.Empty)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha512],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = System.Security.Claims.ClaimTypes.Role,
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ValidateAdminSessionAsync,
                };
            });

        AddAdminAuthorizationPolicies(builder.Services.AddAuthorizationBuilder());
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AdminAuthorizationResultHandler>();
    }

    internal static void AddAdminAuthorizationPolicies(AuthorizationBuilder authorization)
    {
        var allRoles = Enum.GetNames<AdminRoleKind>();
        authorization
            .AddPolicy("admin.read", policy => policy.RequireAuthenticatedUser().RequireRole(allRoles))
            .AddPolicy("admin.diagnostics", policy => policy.RequireClaim("mfa", "true").RequireRole(
                nameof(AdminRoleKind.Owner), nameof(AdminRoleKind.SecurityAdministrator), nameof(AdminRoleKind.SupportAgent)))
            .AddPolicy("admin.operations", policy => policy.RequireClaim("mfa", "true").RequireRole(
                nameof(AdminRoleKind.Owner), nameof(AdminRoleKind.OperationsAdministrator)))
            .AddPolicy("admin.security", policy => policy.RequireClaim("mfa", "true").RequireRole(
                nameof(AdminRoleKind.Owner), nameof(AdminRoleKind.SecurityAdministrator)))
            .AddPolicy("admin.security-or-operations", policy => policy.RequireClaim("mfa", "true").RequireRole(
                nameof(AdminRoleKind.Owner), nameof(AdminRoleKind.SecurityAdministrator), nameof(AdminRoleKind.OperationsAdministrator)))
            .AddPolicy("admin.release", policy => policy.RequireClaim("mfa", "true").RequireRole(
                nameof(AdminRoleKind.Owner), nameof(AdminRoleKind.ReleaseManager)))
            .AddPolicy("admin.platform-upgrade", policy => policy.RequireClaim("mfa", "true").RequireRole(
                nameof(AdminRoleKind.Owner)));
    }

    private static void AddDataProtection(WebApplicationBuilder builder)
    {
        var redisConnection = builder.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is required for shared admin data-protection keys.");
        var configuration = ConfigurationOptions.Parse(redisConnection);
        configuration.AbortOnConnectFail = false;
        var connection = new AdminDataProtectionConnection(ConnectionMultiplexer.Connect(configuration));
        builder.Services.AddSingleton(connection);
        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName("PeerOnQ.Admin")
            .PersistKeysToStackExchangeRedis(connection.Connection, "peeronq:{admin}:data-protection-keys");

        var protectionOptions = builder.Configuration
            .GetSection(AdminDataProtectionOptions.SectionName)
            .Get<AdminDataProtectionOptions>() ?? new AdminDataProtectionOptions();
        if (string.IsNullOrWhiteSpace(protectionOptions.CertificatePath))
        {
            AdminDataProtectionConfiguration.EnsureUnprotectedModeAllowed(builder.Environment, protectionOptions);
            return;
        }

        var certificate = AdminDataProtectionConfiguration.LoadCertificate(protectionOptions);
        builder.Services.AddSingleton(certificate);
        dataProtection.ProtectKeysWithCertificate(certificate);
    }

    private static void AddAdminServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<AdminPasswordService>();
        services.AddSingleton<AdminTokenService>();
        services.AddScoped<IAdminSessionAccessValidator, AdminSessionAccessValidator>();
        services.AddSingleton<IAdminMfaChallengeStore, RedisAdminMfaChallengeStore>();
        services.AddScoped<AdminRequestContext>();
        services.AddScoped<AdminAuditWriter>();
        services.AddScoped<AdminAuthService>();
        services.Configure<AdminBootstrapOptions>(configuration.GetSection("AdminBootstrap"));
        services.AddHostedService<AdminBootstrapService>();
    }

    internal static async Task ValidateAdminSessionAsync(TokenValidatedContext context)
    {
        try
        {
            var principal = context.Principal ?? throw new UnauthorizedAccessException();
            var userId = AdminTokenService.RequireAdminUserId(principal);
            var sessionId = AdminTokenService.RequireAdminSessionId(principal);
            var validator = context.HttpContext.RequestServices.GetRequiredService<IAdminSessionAccessValidator>();
            if (!await validator.IsActiveAsync(userId, sessionId, context.HttpContext.RequestAborted))
                context.Fail("The admin session is no longer active.");
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.Fail("The admin session validation was canceled.");
        }
        catch
        {
            context.Fail("The admin session could not be validated.");
        }
    }

    private static void AddInfrastructureMetrics(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<AdminInfrastructureMetricsOptions>()
            .Bind(builder.Configuration.GetSection(AdminInfrastructureMetricsOptions.SectionName))
            .PostConfigure(options => options.Region = builder.Configuration["Region:Id"] ?? options.Region)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<AdminInfrastructureMetricsOptions>, AdminInfrastructureMetricsOptionsValidator>();
        builder.Services.AddSingleton<AdminInfrastructureMetricsService>();
        builder.Services.AddScoped<AdminInfrastructureSnapshotStore>();
        builder.Services.AddHostedService<AdminInfrastructureSnapshotWorker>();
        builder.Services.AddHttpClient("admin-prometheus", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PeerOnQ-Admin/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        });
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
                    Title = "Too many admin requests.",
                    Type = "https://docs.peeronq.com/problems/rate_limited",
                    Extensions = { ["code"] = "rate_limited" },
                }, cancellationToken);
            };
            options.AddPolicy("admin-login", context => Fixed(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", 5));
            options.AddPolicy("admin-mfa", context => Fixed(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", 8));
            options.AddPolicy("admin-refresh", context => Fixed(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", 20));
            options.AddPolicy("admin-api", context => Fixed(context.User.FindFirst("sub")?.Value ?? "anonymous", 300));
            options.AddPolicy("internal-alerts", context => Fixed(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", 120));
        });
    }

    private static RateLimitPartition<string> Fixed(string key, int limit) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    private static void AddCors(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = configuration.GetSection("AdminCors:AllowedOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0) return;
        if (origins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !environment.IsDevelopment())))
            throw new InvalidOperationException("Admin CORS origins must be absolute HTTPS origins.");
        services.AddCors(options => options.AddPolicy("admin", policy => policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT")
            .WithHeaders("Authorization", "Content-Type", AdminAuthService.CsrfHeader, RequestContextMiddleware.CorrelationHeader)
            .WithExposedHeaders(RequestContextMiddleware.CorrelationHeader)
            .AllowCredentials()
            .SetPreflightMaxAge(TimeSpan.FromHours(1))));
    }
}

internal interface IAdminSessionAccessValidator
{
    Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);
}

internal sealed class AdminSessionAccessValidator(CloudDbContext db, TimeProvider timeProvider) : IAdminSessionAccessValidator
{
    public Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return db.AdminSessions.AsNoTracking().AnyAsync(
            value => value.Id == sessionId
                && value.AdminUserId == userId
                && value.RevokedAtUtc == null
                && value.ExpiresAtUtc > now,
            cancellationToken);
    }
}
