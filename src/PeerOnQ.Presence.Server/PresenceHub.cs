using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Observability;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Presence.Server;

[Authorize(Policy = DeviceAccessAuthentication.Policy)]
public sealed class PresenceHub(
    IPresenceLeaseStore leases,
    IPresenceHistoryWriter history,
    IDeviceAccessStateValidator accessState,
    PresenceConnectionTracker connections,
    IHubContext<PresenceHub> hubContext,
    IOptions<PresenceOptions> options,
    TimeProvider timeProvider,
    ILogger<PresenceHub> logger) : Hub
{
    public const string Path = "/presence/v1/hub";
    private const string RegisteredItem = "peeronq.presence.registered";

    public async Task<PresenceConnectionAcceptedV1> Register(PresenceHeartbeatV1 request)
    {
        if (Context.Items.ContainsKey(RegisteredItem)) throw new HubException("presence_already_registered");
        var now = timeProvider.GetUtcNow();
        ValidateHeartbeat(request, now, initial: true);
        var principal = Context.User ?? throw new HubException("device_authentication_required");
        var deviceId = principal.RequireDeviceId();
        var installationId = principal.RequireInstallationId();
        await EnsureAccessActiveAsync(principal, accessState, Context.ConnectionAborted);
        var configured = options.Value;

        var acquired = await leases.AcquireAsync(new PresenceLeaseRequest(
            installationId,
            deviceId,
            Context.ConnectionId,
            configured.ServerId,
            MapState(request.State),
            NormalizeVersion(request.AppVersion),
            configured.Region,
            now,
            TimeSpan.FromSeconds(configured.LeaseSeconds)), Context.ConnectionAborted);

        Context.Items[RegisteredItem] = true;
        connections.Register(Context.ConnectionId, installationId, now);
        if (!string.IsNullOrEmpty(acquired.DisplacedConnectionId)
            && acquired.DisplacedConnectionId != Context.ConnectionId)
        {
            await hubContext.Clients.Client(acquired.DisplacedConnectionId)
                .SendAsync("ConnectionReplaced", new { reason = "newer_connection" }, Context.ConnectionAborted);
        }
        if (acquired.DisplacedLease is not null)
        {
            await history.RecordEndedLeaseAsync(acquired.DisplacedLease, now, Context.ConnectionAborted);
        }

        return new PresenceConnectionAcceptedV1(now, configured.HeartbeatSeconds, configured.Region);
    }

    public async Task<PresenceHeartbeatResultV1> Heartbeat(PresenceHeartbeatV1 request)
    {
        EnsureRegistered();
        var now = timeProvider.GetUtcNow();
        ValidateHeartbeat(request, now, initial: false);
        if (!connections.AllowHeartbeat(Context.ConnectionId, now))
            throw new HubException("heartbeat_rate_limited");

        var principal = Context.User ?? throw new HubException("device_authentication_required");
        var installationId = principal.RequireInstallationId();
        await EnsureAccessActiveAsync(principal, accessState, Context.ConnectionAborted);
        var configured = options.Value;
        var lease = await leases.RefreshAsync(
            installationId,
            Context.ConnectionId,
            MapState(request.State),
            now,
            TimeSpan.FromSeconds(configured.LeaseSeconds),
            Context.ConnectionAborted);
        if (lease is null)
        {
            Context.Abort();
            throw new HubException("presence_lease_lost");
        }

        return new PresenceHeartbeatResultV1(now, configured.HeartbeatSeconds, lease.ExpiresAtUtc);
    }

    public async Task<PresenceHeartbeatResultV1> SetSessionState(PresenceSessionStateRequestV1 request)
    {
        EnsureRegistered();
        if (request.State is not (PresenceStateV1.Online or PresenceStateV1.Busy or PresenceStateV1.InSession or PresenceStateV1.Reconnecting))
            throw new HubException("invalid_presence_state");

        var now = timeProvider.GetUtcNow();
        var principal = Context.User ?? throw new HubException("device_authentication_required");
        await EnsureAccessActiveAsync(principal, accessState, Context.ConnectionAborted);
        var configured = options.Value;
        var lease = await leases.RefreshAsync(
            principal.RequireInstallationId(),
            Context.ConnectionId,
            MapState(request.State),
            now,
            TimeSpan.FromSeconds(configured.LeaseSeconds),
            Context.ConnectionAborted);
        if (lease is null)
        {
            Context.Abort();
            throw new HubException("presence_lease_lost");
        }

        return new PresenceHeartbeatResultV1(now, configured.HeartbeatSeconds, lease.ExpiresAtUtc);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            if (Context.Items.ContainsKey(RegisteredItem) && Context.User is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var released = await leases.ReleaseAsync(
                    Context.User.RequireInstallationId(),
                    Context.ConnectionId,
                    timeProvider.GetUtcNow(),
                    timeout.Token);
                if (released is not null)
                {
                    await history.RecordEndedLeaseAsync(released, timeProvider.GetUtcNow(), timeout.Token);
                }
            }
        }
        catch (Exception releaseException)
        {
            logger.LogWarning(
                "Presence disconnect cleanup failed with {EventName} and {ExceptionType}",
                "presence.disconnect.cleanup_failed",
                releaseException.GetType().Name);
        }
        finally
        {
            connections.Remove(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }

    private void EnsureRegistered()
    {
        if (!Context.Items.ContainsKey(RegisteredItem)) throw new HubException("presence_not_registered");
    }

    private static async Task EnsureAccessActiveAsync(
        System.Security.Claims.ClaimsPrincipal principal,
        IDeviceAccessStateValidator validator,
        CancellationToken cancellationToken)
    {
        var access = principal.RequireDeviceAccessPrincipal();
        if (await validator.IsActiveAsync(access, cancellationToken)) return;

        throw new HubException("device_access_revoked");
    }

    private static void ValidateHeartbeat(PresenceHeartbeatV1 request, DateTimeOffset now, bool initial)
    {
        _ = NormalizeVersion(request.AppVersion);
        if (string.IsNullOrWhiteSpace(request.Region) || request.Region.Length > 64)
            throw new HubException("invalid_region");
        if ((now - request.SentAtUtc).Duration() > TimeSpan.FromMinutes(5))
            throw new HubException("heartbeat_clock_skew");
        if (request.State is PresenceStateV1.Offline or PresenceStateV1.Blocked or PresenceStateV1.UnsupportedVersion)
            throw new HubException("invalid_presence_state");
        if (initial && request.State is not (PresenceStateV1.Connecting or PresenceStateV1.Online or PresenceStateV1.Reconnecting))
            throw new HubException("invalid_initial_presence_state");
    }

    private static string NormalizeVersion(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 64 || normalized.Any(char.IsControl))
            throw new HubException("invalid_app_version");
        return normalized;
    }

    private static PresenceState MapState(PresenceStateV1 value) => value switch
    {
        PresenceStateV1.Connecting => PresenceState.Connecting,
        PresenceStateV1.Online => PresenceState.Online,
        PresenceStateV1.Busy => PresenceState.Busy,
        PresenceStateV1.InSession => PresenceState.InSession,
        PresenceStateV1.Reconnecting => PresenceState.Reconnecting,
        _ => throw new HubException("invalid_presence_state"),
    };
}
