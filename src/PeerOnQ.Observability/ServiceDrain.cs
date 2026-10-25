using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace PeerOnQ.Observability;

/// <summary>
/// Process-local admission state used to fail readiness and reject new work as soon as graceful
/// shutdown begins. Existing requests remain owned by Kestrel's bounded host shutdown path.
/// </summary>
public sealed class ServiceDrainState
{
    private int _draining;

    public bool IsDraining => Volatile.Read(ref _draining) != 0;

    public void BeginDrain() => Interlocked.Exchange(ref _draining, 1);
}

public sealed class ServiceDrainLifecycle(
    IHostApplicationLifetime lifetime,
    ServiceDrainState state) : IHostedService, IDisposable
{
    private IDisposable? _registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = lifetime.ApplicationStopping.Register(state.BeginDrain);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        state.BeginDrain();
        return Task.CompletedTask;
    }

    public void Dispose() => _registration?.Dispose();
}

public sealed class ServiceDrainHealthCheck(ServiceDrainState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsDraining
            ? HealthCheckResult.Unhealthy("The service is draining.")
            : HealthCheckResult.Healthy());
}

public sealed class ServiceDrainMiddleware(
    RequestDelegate next,
    ServiceDrainState state)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!state.IsDraining || IsOperationalProbe(context.Request.Path))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.RetryAfter = "5";
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://docs.peeronq.com/problems/service_draining",
            title = "Service is draining.",
            status = StatusCodes.Status503ServiceUnavailable,
            code = "service_draining",
        }, context.RequestAborted);
    }

    private static bool IsOperationalProbe(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/metrics");
}
