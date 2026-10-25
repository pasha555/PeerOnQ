using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Observability;
using PeerOnQ.Presence.Server;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Presence.Tests;

public sealed class PresenceHubTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T07:00:00Z");

    [Theory]
    [InlineData(PresenceStateV1.InSession, PresenceState.InSession)]
    [InlineData(PresenceStateV1.Busy, PresenceState.Busy)]
    [InlineData(PresenceStateV1.Online, PresenceState.Online)]
    public async Task A_device_reconnecting_during_a_session_registers_and_heartbeats_at_once(
        PresenceStateV1 state, PresenceState stored)
    {
        var leases = new RecordingLeaseStore();
        var hub = CreateHub(leases, new PresenceConnectionTracker(), "connection-1");

        // 0.9.81 clients register with their session state and heartbeat immediately after.
        await hub.Register(Heartbeat(state));
        await hub.Heartbeat(Heartbeat(state));

        Assert.Equal([stored, stored], leases.States);
        var limited = await Assert.ThrowsAsync<HubException>(() => hub.Heartbeat(Heartbeat(state)));
        Assert.Equal("heartbeat_rate_limited", limited.Message);
    }

    [Theory]
    [InlineData(PresenceStateV1.Offline)]
    [InlineData(PresenceStateV1.Blocked)]
    [InlineData(PresenceStateV1.UnsupportedVersion)]
    public async Task Register_still_rejects_states_a_device_cannot_claim(PresenceStateV1 state)
    {
        var leases = new RecordingLeaseStore();
        var hub = CreateHub(leases, new PresenceConnectionTracker(), "connection-1");

        var rejected = await Assert.ThrowsAsync<HubException>(() => hub.Register(Heartbeat(state)));

        Assert.Equal("invalid_presence_state", rejected.Message);
        Assert.Empty(leases.States);
    }

    [Fact]
    public void Connection_rate_limit_is_per_installation_when_authenticated_and_per_address_otherwise()
    {
        var installationId = Guid.NewGuid();
        var authenticated = new DefaultHttpContext { User = Device(installationId) };
        authenticated.Connection.RemoteIpAddress = IPAddress.Parse("31.171.38.28");
        var anonymous = new DefaultHttpContext();
        anonymous.Connection.RemoteIpAddress = IPAddress.Parse("31.171.38.28");

        Assert.Equal($"installation:{installationId:N}", PresenceApp.ConnectPartitionKey(authenticated));
        Assert.Equal("ip:31.171.38.28", PresenceApp.ConnectPartitionKey(anonymous));
    }

    private static PresenceHub CreateHub(
        RecordingLeaseStore leases, PresenceConnectionTracker tracker, string connectionId) =>
        new(
            leases,
            new NoHistory(),
            new ActiveAccess(),
            tracker,
            // Only used to notify a displaced connection, which the recording store never reports.
            null!,
            Options.Create(new PresenceOptions { ServerId = "presence-test", Region = "test-region" }),
            new FixedTime(Now),
            NullLogger<PresenceHub>.Instance)
        {
            Context = new TestCallerContext(connectionId, Device(Guid.NewGuid())),
        };

    private static PresenceHeartbeatV1 Heartbeat(PresenceStateV1 state) => new(state, "0.9.81", "test-region", Now);

    private static ClaimsPrincipal Device(Guid installationId) => new(new ClaimsIdentity(
    [
        new Claim(DeviceAccessAuthentication.DeviceIdClaim, Guid.NewGuid().ToString("N")),
        new Claim(DeviceAccessAuthentication.InstallationIdClaim, installationId.ToString("N")),
        new Claim("exp", Now.AddMinutes(10).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ], "test"));

    private sealed class RecordingLeaseStore : IPresenceLeaseStore
    {
        public List<PresenceState> States { get; } = [];

        public Task<PresenceLeaseAcquireResult> AcquireAsync(PresenceLeaseRequest request, CancellationToken cancellationToken)
        {
            States.Add(request.State);
            var lease = new PresenceLease(request.InstallationId, request.DeviceId, request.ConnectionId,
                request.ServerId, request.State, request.AppVersion, request.Region, request.NowUtc,
                request.NowUtc, request.NowUtc + request.LeaseDuration);
            return Task.FromResult(new PresenceLeaseAcquireResult(lease, null, null, null));
        }

        public Task<PresenceLease?> RefreshAsync(Guid installationId, string connectionId, PresenceState state,
            DateTimeOffset nowUtc, TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            States.Add(state);
            return Task.FromResult<PresenceLease?>(new PresenceLease(installationId, Guid.Empty, connectionId,
                "presence-test", state, "0.9.81", "test-region", nowUtc, nowUtc, nowUtc + leaseDuration));
        }

        public Task<PresenceLease?> ReleaseAsync(Guid installationId, string connectionId, DateTimeOffset nowUtc,
            CancellationToken cancellationToken) => Task.FromResult<PresenceLease?>(null);

        public Task<PresenceLease?> GetAsync(Guid installationId, CancellationToken cancellationToken) =>
            Task.FromResult<PresenceLease?>(null);

        public Task<long> CountActiveAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(0L);

        public Task<long> CountActiveDevicesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(0L);

        public Task<IReadOnlyList<PresenceLease>> QueryActiveAsync(DateTimeOffset nowUtc, int offset, int limit,
            bool descending, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PresenceLease>>([]);

        public Task<IReadOnlyList<PresenceLease>> CollectExpiredAsync(DateTimeOffset nowUtc, int maximumCount,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PresenceLease>>([]);
    }

    private sealed class NoHistory : IPresenceHistoryWriter
    {
        public Task RecordEndedLeaseAsync(PresenceLease lease, DateTimeOffset endedAtUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ActiveAccess : IDeviceAccessStateValidator
    {
        public Task<bool> IsActiveAsync(DeviceAccessPrincipal principal, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestCallerContext(string connectionId, ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => connectionId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
