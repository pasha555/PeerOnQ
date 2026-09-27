using PeerOnQ.Signaling.Server.Registry;
using PeerOnQ.Signaling.Server.Diagnostics;
using PeerOnQ.Signaling.Server.Security;
using PeerOnQ.Signaling.Server.Sessions;
using PeerOnQ.Transport.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace PeerOnQ.Signaling.Server;

/// <summary>
/// Builds the signaling host. Program.cs and the integration tests both use this, so the
/// tests exercise the real pipeline over a real socket rather than a stand-in.
/// </summary>
public static class SignalingApp
{
    public const string WebSocketPath = "/ws";

    public static WebApplication Create(string[] args, Action<SignalingOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        var startupOptions = new SignalingOptions();
        builder.Configuration.GetSection("Signaling").Bind(startupOptions);

        configure?.Invoke(startupOptions);
        builder.Services.Configure<SignalingOptions>(o => CopyOptions(startupOptions, o));
        builder.Services.AddSingleton<IValidateOptions<SignalingOptions>, SignalingOptionsValidator>();
        builder.Services.AddOptions<SignalingOptions>().ValidateOnStart();

        builder.Services.AddSingleton(TimeProvider.System);
        var cluster = startupOptions.Cluster;
        if (cluster.Enabled)
        {
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(cluster.RedisConnectionString));
            builder.Services.AddSingleton<RedisSignalingBackplane>();
            builder.Services.AddSingleton<ISignalingBackplane>(provider =>
                provider.GetRequiredService<RedisSignalingBackplane>());
            builder.Services.AddSingleton<IHostedService>(provider =>
                provider.GetRequiredService<RedisSignalingBackplane>());
            builder.Services.AddSingleton<ISessionStore, RedisSessionStore>();
            builder.Services.AddSingleton<IUnattendedChallengeStore, RedisUnattendedChallengeStore>();
            builder.Services.AddSingleton<ISessionRequestReplayGuard, RedisSessionRequestReplayGuard>();
        }
        else
        {
            builder.Services.AddSingleton<ISignalingBackplane, LocalSignalingBackplane>();
            builder.Services.AddSingleton<SessionRegistry>();
            builder.Services.AddSingleton<ISessionStore>(provider => provider.GetRequiredService<SessionRegistry>());
            builder.Services.AddSingleton<UnattendedChallengeRegistry>();
            builder.Services.AddSingleton<IUnattendedChallengeStore>(provider =>
                provider.GetRequiredService<UnattendedChallengeRegistry>());
            builder.Services.AddSingleton<ReplayGuard>();
            builder.Services.AddSingleton<ISessionRequestReplayGuard>(provider =>
                provider.GetRequiredService<ReplayGuard>());
        }
        builder.Services.AddSingleton<DeviceRegistry>();
        builder.Services.AddSingleton<TokenService>();
        builder.Services.AddSingleton<TurnCredentialService>();
        builder.Services.AddSingleton<SignalingMetrics>();
        builder.Services.AddSingleton<IDevicePinStore>(provider =>
        {
            var path = provider.GetRequiredService<IOptions<SignalingOptions>>().Value.PinStorePath;

            return string.IsNullOrWhiteSpace(path)
                ? new InMemoryDevicePinStore()
                : new FileDevicePinStore(path, provider.GetService<ILogger<FileDevicePinStore>>());
        });

        builder.Services.AddSingleton(provider =>
            new DevicePublicKeyRegistry(provider.GetRequiredService<IDevicePinStore>()));
        builder.Services.AddSingleton<CloudSignalingAttestationValidator>();
        builder.Services.AddSingleton<SignalingConnectionHandler>();
        builder.Services.AddHostedService<SessionJanitor>();
        builder.Services.AddHostedService<DeviceOwnershipRefresher>();
        builder.Services.AddHostedService<GracefulSignalingShutdown>();

        var app = builder.Build();

        var options = app.Services.GetRequiredService<IOptions<SignalingOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(options.TrustedProxyIp)
            && System.Net.IPAddress.TryParse(options.TrustedProxyIp, out var trustedProxy))
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                ForwardLimit = 1,
                RequireHeaderSymmetry = true,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            forwarded.KnownProxies.Add(trustedProxy);
            app.UseForwardedHeaders(forwarded);
        }

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = options.HeartbeatInterval,
        });

        // Signaling carries session setup, so plain HTTP is only acceptable on loopback.
        app.Use(async (context, next) =>
        {
            if (options.RequireTlsOutsideLoopback
                && !context.Request.IsHttps
                && !IsLoopback(context))
            {
                app.Logger.LogWarning(
                    "Refused a plain HTTP signaling request from {Remote}. Serve wss:// (HTTPS) " +
                    "or set Signaling:RequireTlsOutsideLoopback=false for development only",
                    context.Connection.RemoteIpAddress);

                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                await context.Response.WriteAsync("PeerOnQ signaling requires TLS outside loopback.");
                return;
            }

            await next();
        });

        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/health/startup", () => Results.Ok(new { status = "started" }));

        app.MapGet("/health/ready", (TurnCredentialService turn, ISignalingBackplane backplane) =>
            turn.IsReady && backplane.IsReady
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new
                {
                    status = "not_ready",
                    reason = !turn.IsReady ? "turn_configuration" : "distributed_state",
                }, statusCode: 503));

        app.MapGet("/health", async (DeviceRegistry devices, ISessionStore sessions, TurnCredentialService turn) => Results.Ok(new
        {
            status = turn.IsReady ? "ok" : "degraded",
            devicesOnline = devices.OnlineCount,
            activeSessions = await sessions.CountAsync(),
        }));

        app.MapGet("/metrics", async (DeviceRegistry devices, ISessionStore sessions, SignalingMetrics metrics) =>
            Results.Text(
                metrics.ExportPrometheus(devices.OnlineCount, await sessions.CountAsync()),
                "text/plain; version=0.0.4; charset=utf-8"));

        app.Map(WebSocketPath, async (HttpContext context, SignalingConnectionHandler handler) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("This endpoint accepts WebSocket connections only.");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await handler.HandleAsync(socket, context.TraceIdentifier, context.RequestAborted);
        });

        return app;
    }

    private static void CopyOptions(SignalingOptions source, SignalingOptions target)
    {
        target.ServerId = source.ServerId;
        target.ChallengeLifetime = source.ChallengeLifetime;
        target.ConnectionTokenLifetime = source.ConnectionTokenLifetime;
        target.HeartbeatTimeout = source.HeartbeatTimeout;
        target.HeartbeatInterval = source.HeartbeatInterval;
        target.PermissionTimeout = source.PermissionTimeout;
        target.NegotiationTimeout = source.NegotiationTimeout;
        target.NonceLifetime = source.NonceLifetime;
        target.MaxRequestClockSkew = source.MaxRequestClockSkew;
        target.MessagesPerSecond = source.MessagesPerSecond;
        target.MessageBurst = source.MessageBurst;
        target.MaxMessageBytes = source.MaxMessageBytes;
        target.MaxPendingSendsPerConnection = source.MaxPendingSendsPerConnection;
        target.MaxConcurrentSessionsPerDevice = source.MaxConcurrentSessionsPerDevice;
        target.SweepInterval = source.SweepInterval;
        target.PinStorePath = source.PinStorePath;
        target.RequireTlsOutsideLoopback = source.RequireTlsOutsideLoopback;
        target.TrustedProxyIp = source.TrustedProxyIp;
        target.DisconnectGracePeriod = source.DisconnectGracePeriod;
        target.ResumeTokenLifetime = source.ResumeTokenLifetime;
        target.Cluster = source.Cluster;
        target.Attestation = source.Attestation;
        target.Turn = source.Turn;
    }

    private static bool IsLoopback(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        return remote is null || System.Net.IPAddress.IsLoopback(remote);
    }
}

/// <summary>Expires abandoned sessions and notifies whoever is still connected.</summary>
public sealed class SessionJanitor(
    ISessionStore sessions,
    DeviceRegistry devices,
    ISignalingBackplane backplane,
    IOptions<SignalingOptions> options,
    TimeProvider timeProvider,
    ILogger<SessionJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = timeProvider.GetUtcNow();
                foreach (var expiredOwner in await backplane.TakeExpiredOwnersAsync(256, stoppingToken))
                {
                    var currentOwner = await backplane.ResolveOwnerAsync(expiredOwner.DeviceId, stoppingToken);
                    if (currentOwner is not null) continue;

                    if (!PeerOnQ.Domain.Identity.PeerOnQId.TryParse(expiredOwner.DeviceId, out var deviceId))
                        continue;

                    await sessions.MarkDisconnectedAsync(deviceId, expiredOwner.ConnectionId, stoppingToken);
                    logger.LogInformation(
                        "Marked sessions disconnected after signaling ownership expired for {Device}",
                        deviceId.Masked);
                }

                foreach (var connection in devices.All()
                             .Where(item => now - item.LastSeen >= options.Value.HeartbeatTimeout))
                {
                    logger.LogInformation(
                        "Closing stale signaling connection {ConnectionId} for {Device}",
                        connection.ConnectionId,
                        connection.MaskedId);

                    await connection.CloseAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation,
                        "heartbeat_timeout");
                    connection.Socket.Abort();
                    var removedCurrentOwner = await devices.RemoveAsync(connection, stoppingToken);

                    if (removedCurrentOwner && connection.DeviceId is { } deviceId)
                    {
                        await sessions.MarkDisconnectedAsync(deviceId, connection.ConnectionId, stoppingToken);
                    }
                }

                foreach (var expired in await sessions.RemoveExpiredAsync(stoppingToken))
                {
                    var session = expired.Session;
                    var reason = expired.Reason;

                    foreach (var participant in new[] { session.RequesterId, session.TargetId })
                    {
                        await devices.SendAsync(participant, new SessionEndMessage
                        {
                            SessionId = session.SessionId,
                            Reason = reason,
                        }, stoppingToken);
                    }

                    logger.LogInformation("Swept session {SessionId} ({Reason})", session.SessionId, reason);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }
}

public sealed class DeviceOwnershipRefresher(
    DeviceRegistry devices,
    IOptions<SignalingOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Cluster.Enabled) return;
        using var timer = new PeriodicTimer(options.Value.Cluster.DeviceLeaseRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await devices.RefreshDistributedOwnershipAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }
}

/// <summary>Notifies clients before a rolling deployment closes their sockets.</summary>
public sealed class GracefulSignalingShutdown(DeviceRegistry devices) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var closes = devices.All().Select(connection =>
            connection.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, "server_restarting"));
        await Task.WhenAll(closes);
    }
}
