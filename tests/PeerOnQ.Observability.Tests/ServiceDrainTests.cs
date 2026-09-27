using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PeerOnQ.Observability;

namespace PeerOnQ.Observability.Tests;

public sealed class ServiceDrainTests
{
    [Fact]
    public async Task Drain_FailsReadinessAndRejectsNewApplicationWork()
    {
        var state = new ServiceDrainState();
        var nextCalls = 0;
        var middleware = new ServiceDrainMiddleware(_ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        }, state);
        var before = new DefaultHttpContext();
        before.Response.Body = new MemoryStream();
        before.Request.Path = "/v1/devices";

        await middleware.InvokeAsync(before);
        state.BeginDrain();
        var after = new DefaultHttpContext();
        after.Response.Body = new MemoryStream();
        after.Request.Path = "/v1/devices";
        await middleware.InvokeAsync(after);

        var health = await new ServiceDrainHealthCheck(state)
            .CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(1, nextCalls);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, after.Response.StatusCode);
        Assert.Equal("5", after.Response.Headers.RetryAfter);
        Assert.Equal(HealthStatus.Unhealthy, health.Status);
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health/live")]
    [InlineData("/metrics")]
    public async Task Drain_KeepsOperationalProbesReachable(string path)
    {
        var state = new ServiceDrainState();
        state.BeginDrain();
        var reached = false;
        var middleware = new ServiceDrainMiddleware(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        }, state);
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.True(reached);
    }
}
