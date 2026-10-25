using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Identity;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Infrastructure;
using PeerOnQ.Infrastructure.Cloud;
using PeerOnQ.Infrastructure.Configuration;
using PeerOnQ.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerOnQ.Shared.Contracts.V1;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

public sealed class Phase6CloudPlatformClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "peeronq-cloud-client",
        Guid.NewGuid().ToString("N"));
    private readonly List<string> _databaseConnectionStrings = [];

    [Fact]
    public async Task Device_link_is_explicit_authenticated_and_refreshes_policy_only_after_status_confirmation()
    {
        var installationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var authentications = 0;
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] authentication =
        [
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, Challenge("ownership-challenge", "opaque", DateTimeOffset.UtcNow.AddSeconds(60)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new DeviceAuthenticationResultV1(
                "memory-only-token", DateTimeOffset.UtcNow.AddMinutes(10), deviceId, installationId,
                "246-802-468-024", $"policy-attestation-{++authentications}", DateTimeOffset.UtcNow.AddMinutes(5)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new InstallationRegistrationResultV1(
                installationId, DateTimeOffset.UtcNow, false, false, null))),
        ];
        var expiry = DateTimeOffset.UtcNow.AddMinutes(2);
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] linking =
        [
            (request, _) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/v1/devices/ownership-links", request.RequestUri!.AbsolutePath);
                Assert.Null(request.Content);
                Assert.Equal("memory-only-token", request.Headers.Authorization?.Parameter);
                return Task.FromResult(Json(HttpStatusCode.OK, new CreateDeviceOwnershipLinkResponse(new string('a', 43), expiry)));
            },
            (request, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/v1/devices/ownership-links/status", request.RequestUri!.AbsolutePath);
                Assert.Equal("memory-only-token", request.Headers.Authorization?.Parameter);
                return Task.FromResult(Json(HttpStatusCode.OK, new DeviceOwnershipLinkStatusResponse(false)));
            },
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new DeviceOwnershipLinkStatusResponse(true))),
        ];
        var handler = new SequenceHandler([.. authentication, .. linking, .. authentication]);
        var fixture = CreateClient(handler, new RecordingProofProvider(), installationId,
            timeProvider: new AdjustableTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-4)));
        await using var client = fixture.Client;
        Assert.Equal(0, handler.CallCount);
        var code = await client.CreateDeviceOwnershipLinkAsync();
        Assert.Equal(new string('a', 43), code.Code);
        Assert.Equal(expiry, code.ExpiresAtUtc);
        Assert.False((await client.GetDeviceOwnershipLinkStatusAsync()).Linked);
        Assert.Equal(1, authentications);
        Assert.True((await client.GetDeviceOwnershipLinkStatusAsync()).Linked);
        await client.RefreshOwnershipPolicyAsync();
        Assert.Equal("policy-attestation-2", await client.GetSignalingAttestationAsync());
        Assert.Equal(9, handler.CallCount);
    }

    [Theory]
    [InlineData(PlatformKindV1.Windows)]
    [InlineData(PlatformKindV1.Linux)]
    public async Task ProofFirstRegistrationPersistsAliasBeforeAuthenticatedConfirmation(PlatformKindV1 platform)
    {
        var installationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var proof = new RecordingProofProvider();
        var challenge = Challenge(
            "challenge-1",
            "opaque",
            DateTimeOffset.UtcNow.AddSeconds(30));
        SqliteDeviceIdentityRepository? repository = null;
        var handler = new SequenceHandler(
            async (request, cancellationToken) =>
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal("/v1/devices/register", request.RequestUri!.AbsolutePath);
                var body = await request.Content!.ReadFromJsonAsync<DeviceRegistrationRequestV1>(cancellationToken);
                Assert.Equal(installationId, body!.InstallationId);
                Assert.NotEmpty(body.PublicKeySpkiBase64);
                Assert.Equal(platform, body.Platform);
                return Json(HttpStatusCode.OK, challenge);
            },
            async (request, cancellationToken) =>
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal("/v1/devices/authenticate", request.RequestUri!.AbsolutePath);
                var body = await request.Content!.ReadFromJsonAsync<DeviceAuthenticationRequestV1>(cancellationToken);
                Assert.Equal(installationId, body!.InstallationId);
                return Json(HttpStatusCode.OK, new DeviceAuthenticationResultV1(
                    "short-lived-token", DateTimeOffset.UtcNow.AddMinutes(10), deviceId,
                    installationId, "246-802-468-024", "test-signaling-attestation",
                    DateTimeOffset.UtcNow.AddMinutes(5)));
            },
            async (request, cancellationToken) =>
            {
                Assert.Equal("/v1/installations/register", request.RequestUri!.AbsolutePath);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("short-lived-token", request.Headers.Authorization?.Parameter);
                var persisted = await repository!.LoadAsync(cancellationToken);
                Assert.True(persisted!.PublicIdServerAssigned);
                Assert.Equal("246-802-468-024", persisted.PublicId.Display);
                var body = await request.Content!.ReadFromJsonAsync<InstallationRegistrationRequestV1>(cancellationToken);
                Assert.Equal(installationId, body!.InstallationId);
                Assert.Equal(platform, body.Platform);
                return Json(HttpStatusCode.OK, new InstallationRegistrationResultV1(
                    installationId, DateTimeOffset.UtcNow, false, false, null));
            });
        var fixture = CreateClient(handler, proof, installationId,
            platform: platform == PlatformKindV1.Windows ? null : platform);
        repository = fixture.Repository;
        await using var client = fixture.Client;

        await client.RegisterInstallationAsync();

        Assert.Equal(challenge.CanonicalPayload, proof.Payload);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(CloudPlatformState.Authenticated, client.State);
        Assert.True(client.RoutingIdentityReady);
        Assert.Equal("246-802-468-024", client.Identity.PublicId.Display);
    }

    [Fact]
    public async Task RegistrationAcceptsBoundedChallengeLifetimeDespiteClientClockSkew()
    {
        var now = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        var serverIssuedAt = now.AddMinutes(4);
        var time = new AdjustableTimeProvider(now);
        var installationId = Guid.NewGuid();
        var handler = new SequenceHandler(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, Challenge(
                "challenge-clock-skew",
                "bounded-clock-skew",
                serverIssuedAt.AddSeconds(90),
                serverIssuedAt))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new DeviceAuthenticationResultV1(
                "short-lived-token", now.AddMinutes(14), Guid.NewGuid(), installationId,
                "246-802-468-024", "test-signaling-attestation", now.AddMinutes(9)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new InstallationRegistrationResultV1(
                installationId, now, false, false, null))));
        var fixture = CreateClient(
            handler, new RecordingProofProvider(), installationId, timeProvider: time);
        await using var client = fixture.Client;

        await client.RegisterInstallationAsync();

        Assert.Equal(CloudPlatformState.Authenticated, client.State);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task RegistrationRejectsChallengeBeyondCanonicalLifetimeBound()
    {
        var now = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        var installationId = Guid.NewGuid();
        var handler = new SequenceHandler(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, Challenge(
                "challenge-too-long",
                "unbounded-lifetime",
                now.AddMinutes(2).AddSeconds(1),
                now))));
        var fixture = CreateClient(
            handler,
            new RecordingProofProvider(),
            installationId,
            timeProvider: new AdjustableTimeProvider(now));
        await using var client = fixture.Client;

        var error = await Assert.ThrowsAsync<CloudPlatformException>(() =>
            client.RegisterInstallationAsync());

        Assert.Equal("invalid_challenge", error.Code);
        Assert.True(error.Permanent);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RegistrationRejectsChallengeWhenResponseAndCanonicalExpiryDiffer()
    {
        var now = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        var installationId = Guid.NewGuid();
        var challenge = Challenge(
            "challenge-expiry-mismatch",
            "expiry-mismatch",
            now.AddSeconds(90),
            now) with
        {
            ExpiresAtUtc = now.AddSeconds(60),
        };
        var handler = new SequenceHandler(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, challenge)));
        var fixture = CreateClient(
            handler,
            new RecordingProofProvider(),
            installationId,
            timeProvider: new AdjustableTimeProvider(now));
        await using var client = fixture.Client;

        var error = await Assert.ThrowsAsync<CloudPlatformException>(() =>
            client.RegisterInstallationAsync());

        Assert.Equal("invalid_challenge", error.Code);
        Assert.True(error.Permanent);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FingerprintConflictIsPermanentAndDoesNotPersistOrConfirm()
    {
        var installationId = Guid.NewGuid();
        var handler = new SequenceHandler(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, Challenge(
                "challenge-1", "opaque", DateTimeOffset.UtcNow.AddSeconds(30)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.Conflict,
                new { code = "identity_fingerprint_mismatch" })));
        var fixture = CreateClient(handler, new RecordingProofProvider(), installationId);
        await using var client = fixture.Client;

        var error = await Assert.ThrowsAsync<CloudPlatformException>(() =>
            client.RegisterInstallationAsync());

        Assert.True(error.Permanent);
        Assert.Equal("identity_fingerprint_mismatch", error.Code);
        Assert.Equal(2, handler.CallCount);
        Assert.False((await fixture.Repository.LoadAsync())!.PublicIdServerAssigned);
        Assert.False(client.RoutingIdentityReady);
    }

    [Fact]
    public async Task PersistedAliasCannotSilentlyRotate()
    {
        var installationId = Guid.NewGuid();
        var handler = new SequenceHandler(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, Challenge(
                "challenge-1", "rotation-check", DateTimeOffset.UtcNow.AddSeconds(30)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new DeviceAuthenticationResultV1(
                "short-lived-token", DateTimeOffset.UtcNow.AddMinutes(10), Guid.NewGuid(),
                installationId, "999-888-777-666", "test-signaling-attestation",
                DateTimeOffset.UtcNow.AddMinutes(5)))));
        var fixture = CreateClient(handler, new RecordingProofProvider(), installationId,
            initialAlias: "111-222-333-444");
        await using var client = fixture.Client;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RegisterInstallationAsync());

        var persisted = await fixture.Repository.LoadAsync();
        Assert.Equal("111-222-333-444", persisted!.PublicId.Display);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Transient_registration_failure_retries_after_bounded_window_and_assigns_id()
    {
        var installationId = Guid.NewGuid();
        var enrolled = new TaskCompletionSource<DeviceIdentity>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new SequenceHandler(
            (_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, new { code = "temporarily_unavailable" })),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, Challenge(
                "challenge-after-outage", "after-outage", DateTimeOffset.UtcNow.AddSeconds(30)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new DeviceAuthenticationResultV1(
                "short-lived-token", DateTimeOffset.UtcNow.AddMinutes(10), Guid.NewGuid(),
                installationId, "357-913-579-135", "test-signaling-attestation",
                DateTimeOffset.UtcNow.AddMinutes(5)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new InstallationRegistrationResultV1(
                installationId, DateTimeOffset.UtcNow, false, false, null))));
        var fixture = CreateClient(
            handler,
            new RecordingProofProvider(),
            installationId,
            maximumReconnectAttempts: 0,
            reconnectCooldown: TimeSpan.FromMilliseconds(10));
        await using var client = fixture.Client;
        client.EnrollmentCompleted += (_, identity) => enrolled.TrySetResult(identity);

        client.Start();
        var identity = await enrolled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 100 && handler.CallCount < 4; attempt++)
            await Task.Delay(10);

        Assert.True(identity.PublicIdServerAssigned);
        Assert.Equal("357-913-579-135", identity.PublicId.Display);
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task Reconnect_provider_refreshes_expired_attestation_even_when_access_token_is_current()
    {
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        var time = new AdjustableTimeProvider(now);
        var installationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var handler = new SequenceHandler(
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, Challenge(
                "challenge-1", "first-refresh", time.GetUtcNow().AddSeconds(30)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new DeviceAuthenticationResultV1(
                "access-token-1", time.GetUtcNow().AddMinutes(10), deviceId,
                installationId, "246-802-468-024", "signaling-attestation-1",
                time.GetUtcNow().AddMinutes(5)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new InstallationRegistrationResultV1(
                installationId, time.GetUtcNow(), false, false, null))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, Challenge(
                "challenge-2", "second-refresh", time.GetUtcNow().AddSeconds(30)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new DeviceAuthenticationResultV1(
                "access-token-2", time.GetUtcNow().AddMinutes(10), deviceId,
                installationId, "246-802-468-024", "signaling-attestation-2",
                time.GetUtcNow().AddMinutes(5)))),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, new InstallationRegistrationResultV1(
                installationId, time.GetUtcNow(), false, false, null))));
        var fixture = CreateClient(
            handler, new RecordingProofProvider(), installationId, timeProvider: time);
        await using var client = fixture.Client;

        await client.RegisterInstallationAsync();
        time.Advance(TimeSpan.FromMinutes(6));
        var refreshed = await client.GetSignalingAttestationAsync();

        Assert.Equal("signaling-attestation-2", refreshed);
        Assert.Equal(6, handler.CallCount);
    }

    [Fact]
    public async Task Presence_registration_payload_contains_current_state_version_region_and_time()
    {
        var now = new DateTimeOffset(2026, 8, 11, 13, 0, 0, TimeSpan.Zero);
        var time = new AdjustableTimeProvider(now);
        var fixture = CreateClient(
            new SequenceHandler(),
            new RecordingProofProvider(),
            Guid.NewGuid(),
            timeProvider: time);
        await using var client = fixture.Client;
        await client.SetPresenceStateAsync(PresenceStateV1.Busy);

        var factory = typeof(CloudPlatformClient).GetMethod(
            "CreatePresenceHeartbeat",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var payload = Assert.IsType<PresenceHeartbeatV1>(factory?.Invoke(client, null));

        Assert.Equal(PresenceStateV1.Busy, payload.State);
        Assert.Equal("0.6.0", payload.AppVersion);
        Assert.Equal("test-region", payload.Region);
        Assert.Equal(now, payload.SentAtUtc);
    }

    [Fact]
    public async Task ConnectedSessionHeartbeatUsesAuthenticatedVersionedEndpoint()
    {
        var now = new DateTimeOffset(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);
        var time = new AdjustableTimeProvider(now);
        var sessionId = SessionId.New();
        var handler = new SequenceHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/sessions/heartbeat", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-access-token", request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadFromJsonAsync<ClientSessionHeartbeatV1>(cancellationToken);
            Assert.NotNull(body);
            Assert.Equal(sessionId.Value, body.SessionId);
            Assert.Equal(now, body.SentAtUtc);
            Assert.Equal(body.EventId.ToString("N"), request.Headers.GetValues("Idempotency-Key").Single());
            return Json(HttpStatusCode.OK,
                new SessionHeartbeatResultV1(body.SessionId, now, IsDuplicate: false));
        });
        var fixture = CreateClient(handler, new RecordingProofProvider(), Guid.NewGuid(), timeProvider: time);
        await using var client = fixture.Client;
        await fixture.Outbox.EnqueueAsync(new ClientSessionTelemetryEvent
        {
            EventId = Guid.NewGuid(),
            Kind = ClientSessionEventKind.Connected,
            SessionId = sessionId,
            Role = SessionRole.Viewer,
            PeerPublicDeviceId = "222-222-222-222",
            PermissionMode = SessionMode.ViewOnly,
            Permissions = SessionPermission.ViewScreen,
            StartedAtUtc = now.AddMinutes(-12),
            OccurredAtUtc = now,
        });
        // Heartbeats start once the cloud accepted Connected (0.9.88).
        fixture.Outbox.MarkSessionConnectedAccepted(sessionId.Value);
        typeof(CloudPlatformClient).GetField(
            "_accessToken",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(client, "test-access-token");
        var method = typeof(CloudPlatformClient).GetMethod(
            "SendSessionHeartbeatsAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await Assert.IsAssignableFrom<Task>(method.Invoke(client, [CancellationToken.None]));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Presence_reconnect_during_a_session_registers_online_restores_the_state_and_survives_a_drop()
    {
        await using var server = await FakePresenceServer.StartAsync();
        var installationId = Guid.NewGuid();
        var fixture = CreateClient(
            new CloudApiHandler(installationId), new RecordingProofProvider(), installationId,
            presenceUri: server.BaseUri);
        await using var client = fixture.Client;
        await client.SetPresenceStateAsync(PresenceStateV1.InSession);

        client.Start();
        await server.WaitUntilAsync(() => server.SessionStates.Count >= 1);
        var first = Assert.Single(server.Registrations);
        Assert.Equal(PresenceStateV1.Online, first.State);
        Assert.Equal(PresenceStateV1.InSession, Assert.Single(server.SessionStates).State);

        // A network drop or presence restart while the remote session continues.
        server.AbortConnections();
        await server.WaitUntilAsync(() => server.SessionStates.Count >= 2);

        Assert.Equal(2, server.Registrations.Count);
        Assert.NotEqual(first.ConnectionId, server.Registrations[1].ConnectionId);
        Assert.All(server.Registrations, registration => Assert.Equal(PresenceStateV1.Online, registration.State));
        Assert.Equal(PresenceStateV1.InSession, server.SessionStates[1].State);
        Assert.Equal(server.Registrations[1].ConnectionId, server.SessionStates[1].ConnectionId);
        Assert.Empty(server.Errors);
    }

    [Fact]
    public async Task First_presence_heartbeat_is_not_sent_inside_the_server_minimum_interval_after_register()
    {
        await using var server = await FakePresenceServer.StartAsync();
        var installationId = Guid.NewGuid();
        var fixture = CreateClient(
            new CloudApiHandler(installationId), new RecordingProofProvider(), installationId,
            presenceUri: server.BaseUri);
        await using var client = fixture.Client;

        client.Start();
        await server.WaitUntilAsync(() => server.Registrations.Count >= 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Empty(server.Errors);
        Assert.Single(server.Registrations);
        Assert.Empty(server.Heartbeats);
        Assert.Equal(CloudPlatformState.Online, client.State);
    }

    [Fact]
    public async Task Rejected_session_telemetry_keeps_presence_connected_and_stops_that_heartbeat()
    {
        await using var server = await FakePresenceServer.StartAsync();
        var installationId = Guid.NewGuid();
        var heartbeats = 0;
        var lifecycleRejections = 0;
        // What production returned: lowercase codes, 400 for lifecycle events, 404 for heartbeats.
        var fixture = CreateClient(
            new CloudApiHandler(installationId, request => request.RequestUri!.AbsolutePath switch
            {
                "/v1/sessions/heartbeat" when Interlocked.Increment(ref heartbeats) > 0 =>
                    Json(HttpStatusCode.NotFound, new { code = "session_not_found" }),
                "/v1/sessions/start" or "/v1/sessions/connected" when Interlocked.Increment(ref lifecycleRejections) > 0 =>
                    Json(HttpStatusCode.BadRequest, new { code = "invalid_request" }),
                _ => null,
            }),
            new RecordingProofProvider(), installationId, presenceUri: server.BaseUri);
        await using var client = fixture.Client;
        var sessionId = SessionId.New();
        await fixture.Outbox.EnqueueAsync(new ClientSessionTelemetryEvent
        {
            EventId = Guid.NewGuid(),
            Kind = ClientSessionEventKind.Connected,
            SessionId = sessionId,
            Role = SessionRole.Viewer,
            PeerPublicDeviceId = "222-222-222-222",
            PermissionMode = SessionMode.ViewOnly,
            Permissions = SessionPermission.ViewScreen,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });

        client.Start();
        await server.WaitUntilAsync(() => Volatile.Read(ref lifecycleRejections) >= 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        Assert.Single(server.Registrations);
        Assert.Empty(server.Errors);
        Assert.Equal(CloudPlatformState.Online, client.State);
        // A session whose Connected the cloud never accepted gets no heartbeat at all (0.9.87
        // sent one first and it was rejected with invalid_request).
        Assert.Equal(0, Volatile.Read(ref heartbeats));
        Assert.Empty(fixture.Outbox.GetActiveConnectedSessionIds());
    }

    [Fact]
    public async Task Session_heartbeats_wait_until_the_cloud_accepted_connected()
    {
        await using var server = await FakePresenceServer.StartAsync();
        var installationId = Guid.NewGuid();
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var fixture = CreateClient(
            new CloudApiHandler(installationId, request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (!path.StartsWith("/v1/sessions/", StringComparison.Ordinal)) return null;
                calls.Enqueue(path);
                return Json(HttpStatusCode.OK, new { });
            }),
            new RecordingProofProvider(), installationId, presenceUri: server.BaseUri);
        await using var client = fixture.Client;
        var sessionId = SessionId.New();
        await fixture.Outbox.EnqueueAsync(SessionEvent(sessionId, ClientSessionEventKind.Started, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await fixture.Outbox.EnqueueAsync(SessionEvent(sessionId, ClientSessionEventKind.Connected, DateTimeOffset.UtcNow));
        Assert.Empty(fixture.Outbox.GetActiveConnectedSessionIds());

        client.Start();
        await WaitUntilAsync(() => calls.Contains("/v1/sessions/heartbeat"));

        var ordered = calls.ToArray();
        Assert.True(Array.IndexOf(ordered, "/v1/sessions/connected") < Array.IndexOf(ordered, "/v1/sessions/heartbeat"));
        Assert.Equal([sessionId.Value], fixture.Outbox.GetActiveConnectedSessionIds());
    }

    [Fact]
    public async Task A_never_started_session_is_dropped_once_and_does_not_block_later_sessions()
    {
        // 0.9.85 production: /v1/sessions/end answered 404 "The session must be started before
        // later lifecycle events." for sessions whose start never reached the cloud; the client
        // retried them on every heartbeat and the events queued behind them waited.
        await using var server = await FakePresenceServer.StartAsync();
        var installationId = Guid.NewGuid();
        var calls = new ConcurrentQueue<string>();
        var fixture = CreateClient(
            new CloudApiHandler(installationId, request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (!path.StartsWith("/v1/sessions/", StringComparison.Ordinal)) return null;
                calls.Enqueue(path);
                return path == "/v1/sessions/end"
                    ? Json(HttpStatusCode.NotFound, new { code = "session_not_found" })
                    : Json(HttpStatusCode.OK, new { });
            }),
            new RecordingProofProvider(), installationId, presenceUri: server.BaseUri);
        await using var client = fixture.Client;
        var orphan = SessionId.New();
        var live = SessionId.New();
        await fixture.Outbox.EnqueueAsync(SessionEvent(orphan, ClientSessionEventKind.Ended, DateTimeOffset.UtcNow.AddMinutes(-5)));
        await fixture.Outbox.EnqueueAsync(SessionEvent(live, ClientSessionEventKind.Started, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await fixture.Outbox.EnqueueAsync(SessionEvent(live, ClientSessionEventKind.Connected, DateTimeOffset.UtcNow));

        client.Start();
        await WaitUntilAsync(() => calls.Count(c => c == "/v1/sessions/connected") >= 1);

        // One attempt at the orphan, then the live session goes through (its heartbeat aside).
        Assert.Equal(
            ["/v1/sessions/end", "/v1/sessions/start", "/v1/sessions/connected"],
            calls.Where(c => c != "/v1/sessions/heartbeat").ToArray());
        Assert.Empty(await fixture.Outbox.ReadPendingAsync());
        Assert.Equal(CloudPlatformState.Online, client.State);
    }

    [Fact]
    public async Task A_queued_start_in_backoff_is_sent_before_the_later_event_that_overtook_it()
    {
        await using var server = await FakePresenceServer.StartAsync();
        var installationId = Guid.NewGuid();
        var calls = new ConcurrentQueue<string>();
        var started = 0;
        var fixture = CreateClient(
            new CloudApiHandler(installationId, request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path is not ("/v1/sessions/start" or "/v1/sessions/connected")) return null;
                calls.Enqueue(path);
                if (path == "/v1/sessions/start") Interlocked.Exchange(ref started, 1);
                return path == "/v1/sessions/connected" && Volatile.Read(ref started) == 0
                    ? Json(HttpStatusCode.NotFound, new { code = "session_not_found" })
                    : Json(HttpStatusCode.OK, new { });
            }),
            new RecordingProofProvider(), installationId, presenceUri: server.BaseUri);
        await using var client = fixture.Client;
        var sessionId = SessionId.New();
        var start = SessionEvent(sessionId, ClientSessionEventKind.Started, DateTimeOffset.UtcNow.AddMinutes(-1));
        await fixture.Outbox.EnqueueAsync(start);
        // The start failed once during a cloud outage and now waits out its backoff.
        await fixture.Outbox.MarkFailedAsync(start.EventId);
        await fixture.Outbox.EnqueueAsync(SessionEvent(sessionId, ClientSessionEventKind.Connected, DateTimeOffset.UtcNow));

        client.Start();
        await WaitUntilAsync(() => calls.Count >= 3);

        Assert.Equal(["/v1/sessions/connected", "/v1/sessions/start", "/v1/sessions/connected"], calls.ToArray());
        Assert.Null(await fixture.Outbox.FindUndeliveredStartAsync(sessionId.ToString()));
        Assert.Empty(await fixture.Outbox.ReadPendingAsync());
    }

    [Fact]
    public async Task A_live_session_the_cloud_lost_is_started_again_instead_of_losing_its_heartbeat()
    {
        await using var server = await FakePresenceServer.StartAsync();
        var installationId = Guid.NewGuid();
        var calls = new ConcurrentQueue<string>();
        var fixture = CreateClient(
            new CloudApiHandler(installationId, request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (!path.StartsWith("/v1/sessions/", StringComparison.Ordinal)) return null;
                calls.Enqueue(path);
                // The cloud was redeployed: it accepted the start and connection, then forgot them.
                return path == "/v1/sessions/heartbeat" && calls.Count(c => c == "/v1/sessions/start") < 2
                    ? Json(HttpStatusCode.NotFound, new { code = "session_not_found" })
                    : Json(HttpStatusCode.OK, new { });
            }),
            new RecordingProofProvider(), installationId, presenceUri: server.BaseUri);
        await using var client = fixture.Client;
        var sessionId = SessionId.New();
        await fixture.Outbox.EnqueueAsync(SessionEvent(sessionId, ClientSessionEventKind.Started, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await fixture.Outbox.EnqueueAsync(SessionEvent(sessionId, ClientSessionEventKind.Connected, DateTimeOffset.UtcNow));

        client.Start();
        await WaitUntilAsync(() => calls.Count >= 5);
        await Task.Delay(TimeSpan.FromSeconds(0.5));

        Assert.Equal(
            ["/v1/sessions/start", "/v1/sessions/connected", "/v1/sessions/heartbeat", "/v1/sessions/start", "/v1/sessions/connected"],
            calls.ToArray());
        Assert.Equal([sessionId.Value], fixture.Outbox.GetActiveConnectedSessionIds());
        Assert.Equal(CloudPlatformState.Online, client.State);
    }

    private static ClientSessionTelemetryEvent SessionEvent(
        SessionId sessionId, ClientSessionEventKind kind, DateTimeOffset occurredAtUtc) => new()
    {
        EventId = Guid.NewGuid(),
        Kind = kind,
        SessionId = sessionId,
        Role = SessionRole.Viewer,
        PeerPublicDeviceId = "222-222-222-222",
        PermissionMode = SessionMode.ViewOnly,
        Permissions = SessionPermission.ViewScreen,
        StartedAtUtc = occurredAtUtc.AddSeconds(-1),
        OccurredAtUtc = occurredAtUtc,
        EndReason = kind == ClientSessionEventKind.Ended ? "EndedByViewer" : null,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("Cloud condition not met.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Presence_rate_limit_waits_for_the_cooldown_instead_of_the_fast_retry_burst()
    {
        await using var server = await FakePresenceServer.StartAsync(rejectedNegotiations: 1);
        var installationId = Guid.NewGuid();
        var fixture = CreateClient(
            new CloudApiHandler(installationId), new RecordingProofProvider(), installationId,
            reconnectCooldown: TimeSpan.FromSeconds(2), presenceUri: server.BaseUri);
        await using var client = fixture.Client;

        client.Start();
        await server.WaitUntilAsync(() => server.Registrations.Count >= 1);

        Assert.Equal(2, server.Negotiations.Count);
        Assert.True(server.Negotiations[1] - server.Negotiations[0] >= TimeSpan.FromSeconds(1.8),
            $"Retried after {(server.Negotiations[1] - server.Negotiations[0]).TotalMilliseconds} ms.");
        Assert.Empty(server.Errors);
    }

    private ClientFixture CreateClient(
        HttpMessageHandler handler,
        IRegistrationProofProvider proof,
        Guid installationId,
        string? initialAlias = null,
        TimeProvider? timeProvider = null,
        int? maximumReconnectAttempts = null,
        TimeSpan? reconnectCooldown = null,
        PlatformKindV1? platform = null,
        Uri? presenceUri = null)
    {
        Directory.CreateDirectory(_root);
        var database = new PeerOnQDatabase(Path.Combine(_root, $"{Guid.NewGuid():N}.db"));
        _databaseConnectionStrings.Add(database.ConnectionString);
        database.Migrate();
        var repository = new SqliteDeviceIdentityRepository(database);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = DeviceIdentity.Create("Test Device") with
        {
            InternalId = installationId,
            PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            PublicId = initialAlias is null ? PeerOnQId.NewId() : PeerOnQId.Parse(initialAlias),
            PublicIdServerAssigned = initialAlias is not null,
        };
        repository.SaveAsync(identity).GetAwaiter().GetResult();
        var provisioning = new DeviceProvisioningService(repository, new NoOpSecretStore(),
            NullLogger<DeviceProvisioningService>.Instance);
        var outbox = new SqliteClientTelemetryOutbox(database);
        var options = new CloudPlatformClientOptions
        {
            Endpoints = new CloudServiceEndpoints
            {
                Environment = PeerOnQDeploymentEnvironment.Development,
                ApiBaseUri = new Uri("https://api.example.test/"),
                PresenceUri = presenceUri ?? new Uri("https://presence.example.test/"),
                SignalingUri = new Uri("wss://signal.example.test/ws"),
                UpdatesBaseUri = new Uri("https://updates.example.test/"),
                DownloadsBaseUri = new Uri("https://download.example.test/"),
                DiagnosticsBaseUri = new Uri("https://diagnostics.example.test/"),
                Region = "test-region",
            },
            InstallationId = installationId,
            AppVersion = "0.6.0",
            OsVersion = "Windows test",
            Architecture = ArchitectureKindV1.X64,
            InstallChannel = InstallChannelV1.Development,
        };
        if (maximumReconnectAttempts is not null || reconnectCooldown is not null)
        {
            options = options with
            {
                MaximumReconnectAttempts = maximumReconnectAttempts ?? options.MaximumReconnectAttempts,
                ReconnectCooldown = reconnectCooldown ?? options.ReconnectCooldown,
            };
        }
        if (platform is not null) options = options with { Platform = platform.Value };
        var client = new CloudPlatformClient(
            new HttpClient(handler),
            options,
            identity,
            proof,
            provisioning.AssignServerPublicIdAsync,
            outbox,
            NullLogger<CloudPlatformClient>.Instance,
            timeProvider: timeProvider);
        return new ClientFixture(client, repository, outbox);
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) => new(status)
    {
        Content = JsonContent.Create(value),
    };

    private static DeviceRegistrationChallengeV1 Challenge(
        string challengeId,
        string nonce,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset? issuedAtUtc = null)
    {
        var issued = issuedAtUtc ?? expiresAtUtc.AddSeconds(-90);
        var payload = string.Join('\n',
            "peeronq-device-auth-v1",
            $"challenge_id={challengeId}",
            $"nonce={nonce}",
            $"issued_at={issued.ToUnixTimeSeconds()}",
            $"expires_at={expiresAtUtc.ToUnixTimeSeconds()}");
        return new DeviceRegistrationChallengeV1(challengeId, payload, expiresAtUtc);
    }

    public void Dispose()
    {
        foreach (var connectionString in _databaseConnectionStrings)
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record ClientFixture(
        CloudPlatformClient Client,
        SqliteDeviceIdentityRepository Repository,
        SqliteClientTelemetryOutbox Outbox);

    private sealed class RecordingProofProvider : IRegistrationProofProvider
    {
        public string? Payload { get; private set; }

        public Task<string> ComputeRegistrationProofAsync(
            string challenge,
            CancellationToken cancellationToken = default)
        {
            Payload = challenge;
            return Task.FromResult("signed-proof");
        }
    }

    private sealed class NoOpSecretStore : IDeviceSecretStore
    {
        public Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task SetAsync(string name, byte[] secret, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(string name, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class SequenceHandler(
        params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] responses)
        : HttpMessageHandler
    {
        private int _index;
        public int CallCount => _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            if (index >= responses.Length) throw new InvalidOperationException("Unexpected HTTP call.");
            return responses[index](request, cancellationToken);
        }
    }

    /// <summary>Answers the Cloud API calls the presence loop makes, as often as it makes them.</summary>
    private sealed class CloudApiHandler(
        Guid installationId,
        Func<HttpRequestMessage, HttpResponseMessage?>? sessions = null) : HttpMessageHandler
    {
        private readonly Guid _deviceId = Guid.NewGuid();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/v1/devices/register" => Json(HttpStatusCode.OK, Challenge(
                    "presence-challenge", "presence", now.AddSeconds(30))),
                "/v1/devices/authenticate" => Json(HttpStatusCode.OK, new DeviceAuthenticationResultV1(
                    "presence-access-token", now.AddMinutes(10), _deviceId, installationId,
                    "357-913-579-135", "presence-attestation", now.AddMinutes(5))),
                "/v1/installations/register" => Json(HttpStatusCode.OK, new InstallationRegistrationResultV1(
                    installationId, now, false, false, null)),
                "/v1/installations/heartbeat" => Json(HttpStatusCode.OK, new InstallationHeartbeatResultV1(
                    now, 25, false, null)),
                _ when sessions?.Invoke(request) is { } response => response,
                var path => throw new InvalidOperationException($"Unexpected HTTP call to {path}."),
            });
        }
    }

    private sealed record PresenceInvocation(string ConnectionId, PresenceStateV1 State);

    /// <summary>
    /// A presence hub on loopback with the rules of the deployed 0.9.81 server, which a new
    /// client must work with: Register only as Connecting/Online/Reconnecting, Register counts
    /// as a heartbeat, heartbeats at least 3 s apart, one registration per connection id.
    /// </summary>
    private sealed class FakePresenceServer : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<PresenceInvocation> _registrations = [];
        private readonly List<PresenceInvocation> _sessionStates = [];
        private readonly List<PresenceInvocation> _heartbeats = [];
        private readonly List<DateTimeOffset> _negotiations = [];
        private readonly List<string> _errors = [];
        private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new();
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastHeartbeat = new();
        private int _rejectedNegotiations;
        private WebApplication? _app;

        private FakePresenceServer(int rejectedNegotiations) => _rejectedNegotiations = rejectedNegotiations;

        public Uri BaseUri { get; private set; } = null!;
        public IReadOnlyList<PresenceInvocation> Registrations => Snapshot(_registrations);
        public IReadOnlyList<PresenceInvocation> SessionStates => Snapshot(_sessionStates);
        public IReadOnlyList<PresenceInvocation> Heartbeats => Snapshot(_heartbeats);
        public IReadOnlyList<DateTimeOffset> Negotiations => Snapshot(_negotiations);
        public IReadOnlyList<string> Errors => Snapshot(_errors);

        public static async Task<FakePresenceServer> StartAsync(int rejectedNegotiations = 0)
        {
            var server = new FakePresenceServer(rejectedNegotiations);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(server);
            builder.Services.AddSignalR();
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.Value?.EndsWith("/negotiate", StringComparison.Ordinal) == true)
                {
                    lock (server._gate) server._negotiations.Add(DateTimeOffset.UtcNow);
                    if (Interlocked.Decrement(ref server._rejectedNegotiations) >= 0)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                        context.Response.Headers.RetryAfter = "2";
                        return;
                    }
                }
                await next(context);
            });
            app.MapHub<FakePresenceHub>("/presence/v1/hub");
            await app.StartAsync();
            server._app = app;
            server.BaseUri = new Uri(app.Urls.Single().TrimEnd('/') + "/");
            return server;
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (!condition())
            {
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException($"Presence condition not met; errors: {string.Join(", ", Errors)}.");
                await Task.Delay(20);
            }
        }

        public void AbortConnections()
        {
            foreach (var connection in _connections.Values) connection.Abort();
        }

        public void Connected(HubCallerContext context) =>
            _connections[context.ConnectionId] = context;

        public void Disconnected(string connectionId)
        {
            _connections.TryRemove(connectionId, out _);
            _lastHeartbeat.TryRemove(connectionId, out _);
        }

        public void Register(HubCallerContext context, PresenceHeartbeatV1 request)
        {
            if (!context.Items.TryAdd("registered", true)) Fail("presence_already_registered");
            if (request.State is not (PresenceStateV1.Connecting or PresenceStateV1.Online or PresenceStateV1.Reconnecting))
                Fail("invalid_initial_presence_state");
            _lastHeartbeat[context.ConnectionId] = DateTimeOffset.UtcNow;
            lock (_gate) _registrations.Add(new PresenceInvocation(context.ConnectionId, request.State));
        }

        public void Heartbeat(HubCallerContext context, PresenceHeartbeatV1 request)
        {
            if (!context.Items.ContainsKey("registered")) Fail("presence_not_registered");
            var now = DateTimeOffset.UtcNow;
            if (_lastHeartbeat.TryGetValue(context.ConnectionId, out var last) && now - last < TimeSpan.FromSeconds(3))
                Fail("heartbeat_rate_limited");
            _lastHeartbeat[context.ConnectionId] = now;
            lock (_gate) _heartbeats.Add(new PresenceInvocation(context.ConnectionId, request.State));
        }

        public void SetSessionState(HubCallerContext context, PresenceSessionStateRequestV1 request)
        {
            if (!context.Items.ContainsKey("registered")) Fail("presence_not_registered");
            lock (_gate) _sessionStates.Add(new PresenceInvocation(context.ConnectionId, request.State));
        }

        private void Fail(string code)
        {
            lock (_gate) _errors.Add(code);
            throw new HubException(code);
        }

        private IReadOnlyList<T> Snapshot<T>(List<T> source)
        {
            lock (_gate) return source.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null) await _app.DisposeAsync();
        }
    }

    private sealed class FakePresenceHub(FakePresenceServer server) : Hub
    {
        public override Task OnConnectedAsync()
        {
            server.Connected(Context);
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            server.Disconnected(Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }

        public PresenceConnectionAcceptedV1 Register(PresenceHeartbeatV1 request)
        {
            server.Register(Context, request);
            return new PresenceConnectionAcceptedV1(DateTimeOffset.UtcNow, 25, "test-region");
        }

        public PresenceHeartbeatResultV1 Heartbeat(PresenceHeartbeatV1 request)
        {
            server.Heartbeat(Context, request);
            return new PresenceHeartbeatResultV1(DateTimeOffset.UtcNow, 25, DateTimeOffset.UtcNow.AddSeconds(75));
        }

        public PresenceHeartbeatResultV1 SetSessionState(PresenceSessionStateRequestV1 request)
        {
            server.SetSessionState(Context, request);
            return new PresenceHeartbeatResultV1(DateTimeOffset.UtcNow, 25, DateTimeOffset.UtcNow.AddSeconds(75));
        }
    }
}
