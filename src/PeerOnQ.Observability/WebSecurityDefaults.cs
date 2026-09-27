using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PeerOnQ.Observability;

public static class WebSecurityDefaults
{
    public static WebApplicationBuilder AddPeerOnQWebSecurity(this WebApplicationBuilder builder, long maximumRequestBodyBytes)
    {
        if (maximumRequestBodyBytes is < 1024 or > 100L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumRequestBodyBytes));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = maximumRequestBodyBytes;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            options.Limits.MaxRequestHeaderCount = 64;
            options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
        });

        var proxyValue = builder.Configuration["ReverseProxy:TrustedProxyIp"];
        if (!string.IsNullOrWhiteSpace(proxyValue))
        {
            if (!IPAddress.TryParse(proxyValue, out var proxy))
                throw new InvalidOperationException("ReverseProxy:TrustedProxyIp must be one exact IP address.");
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                options.ForwardLimit = 1;
                options.RequireHeaderSymmetry = true;
                options.KnownIPNetworks.Clear();
                options.KnownProxies.Clear();
                options.KnownProxies.Add(proxy);
            });
        }

        return builder;
    }

    public static WebApplication UsePeerOnQWebSecurity(this WebApplication app)
    {
        if (!string.IsNullOrWhiteSpace(app.Configuration["ReverseProxy:TrustedProxyIp"]))
            app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
                context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
                context.Response.Headers.TryAdd("Referrer-Policy", "no-referrer");
                context.Response.Headers.TryAdd("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
                return Task.CompletedTask;
            });
            await next();
        });
        return app;
    }
}
