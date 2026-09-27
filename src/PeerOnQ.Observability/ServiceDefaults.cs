using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace PeerOnQ.Observability;

public static class ServiceDefaults
{
    public static WebApplicationBuilder AddPeerOnQServiceDefaults(
        this WebApplicationBuilder builder,
        string serviceName,
        Func<IServiceProvider, PeerOnQMetricState>? metricStateFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var region = builder.Configuration["Region:Id"]
            ?? RequiredNonSecret(builder.Configuration, "PeerOnQ:Region", "local");
        var environment = builder.Environment.EnvironmentName;

        builder.Host.UseSerilog((context, services, logging) => logging
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", serviceName)
            .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
            .Enrich.WithProperty("Region", region)
            .WriteTo.Console(new JsonFormatter(renderMessage: true)));

        if (metricStateFactory is null)
        {
            builder.Services.AddSingleton(new PeerOnQMetricState());
        }
        else
        {
            builder.Services.AddSingleton(serviceProvider => metricStateFactory(serviceProvider));
        }
        builder.Services.AddSingleton<PeerOnQMetrics>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ServiceDrainState>();
        builder.Services.AddHostedService<ServiceDrainLifecycle>();

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName, serviceVersion: PeerOnQTelemetry.Version)
                .AddAttributes([
                    new KeyValuePair<string, object>("deployment.environment.name", environment),
                    new KeyValuePair<string, object>("cloud.region", region),
                ]))
            .WithTracing(traces => traces
                .AddSource(PeerOnQTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health")
                        && !context.Request.Path.StartsWithSegments("/metrics")
                        && !context.Request.Query.ContainsKey("access_token");
                })
                .AddHttpClientInstrumentation(options => options.RecordException = false))
            .WithMetrics(metrics => metrics
                .AddMeter(PeerOnQTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        var otlpEndpoint = builder.Configuration["OpenTelemetry:Endpoint"]
            ?? builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            if (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("OpenTelemetry endpoint must be an absolute HTTP or HTTPS URI.");
            }

            var protocol = string.Equals(
                builder.Configuration["OpenTelemetry:Protocol"],
                "http/protobuf",
                StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.HttpProtobuf
                : OtlpExportProtocol.Grpc;
            telemetry.UseOtlpExporter(protocol, endpoint);
        }

        builder.Services.AddHealthChecks()
            .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live", "startup"])
            .AddCheck<ServiceDrainHealthCheck>("traffic-admission", tags: ["ready", "startup"]);
        builder.Services.AddExceptionHandler<PeerOnQExceptionHandler>();
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
            context.ProblemDetails.Extensions["errorId"] = Guid.NewGuid().ToString("N");
            context.ProblemDetails.Detail = null;
        });

        return builder;
    }

    public static WebApplication UsePeerOnQServiceDefaults(this WebApplication app)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (context, _, exception) =>
                exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Error
                    : context.Response.StatusCode >= StatusCodes.Status400BadRequest
                        ? LogEventLevel.Warning
                        : LogEventLevel.Information;
            options.EnrichDiagnosticContext = (diagnostic, context) =>
            {
                diagnostic.Set("RequestId", context.TraceIdentifier);
                diagnostic.Set("TraceId", Activity.Current?.TraceId.ToString() ?? string.Empty);
                diagnostic.Set("EventName", "http.request.completed");
            };
        });
        // Keep request logging outside the exception handler so handled 4xx failures are recorded
        // with their final response status instead of a synthetic 500 and exception stack trace.
        app.UseExceptionHandler();
        app.UseMiddleware<RequestContextMiddleware>();
        app.UseMiddleware<ServiceDrainMiddleware>();
        app.MapGet("/metrics", (PeerOnQMetrics metrics) => Results.Text(
            metrics.ExportPrometheus(),
            "text/plain; version=0.0.4; charset=utf-8"));
        app.MapPeerOnQHealthEndpoints();
        return app;
    }

    public static IEndpointRouteBuilder MapPeerOnQHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", Options(tag: "live"));
        endpoints.MapHealthChecks("/health/ready", Options(tag: "ready"));
        endpoints.MapHealthChecks("/health/startup", Options(tag: "startup"));
        return endpoints;
    }

    private static HealthCheckOptions Options(string tag) => new()
    {
        Predicate = check => check.Tags.Contains(tag),
        ResponseWriter = static async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                checks = report.Entries.OrderBy(item => item.Key).Select(item => new
                {
                    name = item.Key,
                    status = item.Value.Status.ToString().ToLowerInvariant(),
                }),
            });
            await context.Response.WriteAsync(payload);
        },
    };

    private static string RequiredNonSecret(IConfiguration configuration, string key, string developmentDefault) =>
        configuration[key] ?? developmentDefault;
}
