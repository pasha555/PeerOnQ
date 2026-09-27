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
using Microsoft.Data.Sqlite;
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
    public async Task ProofFirstRegistrationPersistsAliasBeforeAuthenticatedConfirmation()
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
                Assert.Equal(PlatformKindV1.Windows, body.Platform);
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
                return Json(HttpStatusCode.OK, new InstallationRegistrationResultV1(
                    installationId, DateTimeOffset.UtcNow, false, false, null));
            });
        var fixture = CreateClient(handler, proof, installationId);
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

    private ClientFixture CreateClient(
        HttpMessageHandler handler,
        IRegistrationProofProvider proof,
        Guid installationId,
        string? initialAlias = null,
        TimeProvider? timeProvider = null,
        int? maximumReconnectAttempts = null,
        TimeSpan? reconnectCooldown = null)
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
                PresenceUri = new Uri("https://presence.example.test/"),
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
}
