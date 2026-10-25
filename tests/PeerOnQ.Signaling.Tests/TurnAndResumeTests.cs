using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Signaling.Server;
using PeerOnQ.Signaling.Server.Registry;
using PeerOnQ.Signaling.Server.Security;
using PeerOnQ.Signaling.Server.Sessions;
using PeerOnQ.Transport;
using PeerOnQ.Transport.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

internal sealed class Phase3FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class ManualSignalingTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now += duration;
}

public sealed class TurnCredentialTests
{
    [Fact]
    public void Coturn_rest_credentials_are_time_limited_and_do_not_expose_the_secret()
    {
        const string secret = "phase3-test-secret-with-more-than-32-characters";
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var time = new Phase3FixedTimeProvider(now);
        var options = Options.Create(new SignalingOptions
        {
            Turn = new TurnOptions
            {
                SharedSecret = secret,
                StunUrls = ["stun:turn.example.com:3478"],
                TurnUrls =
                [
                    "turn:turn.example.com:3478?transport=udp",
                    "turn:turn.example.com:3478?transport=tcp",
                    "turns:turn.example.com:5349?transport=tcp",
                ],
                CredentialLifetime = TimeSpan.FromMinutes(5),
                ServerId = "turn-eu-1",
                Region = "eu-west",
            },
        });

        var issued = new TurnCredentialService(options, time)
            .Issue(SessionId.New().ToString(), PeerOnQId.Parse("LNK-111-222-333-444"));
        var relay = Assert.Single(issued.Servers, server => server.Username is not null);

        Assert.Equal(now + TimeSpan.FromMinutes(5), relay.ExpiresAt);
        Assert.StartsWith((now + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds().ToString(), relay.Username);

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(relay.Username!)));
        Assert.Equal(expected, relay.Credential);
        Assert.DoesNotContain(secret, SignalingCodec.EncodeToString(issued), StringComparison.Ordinal);
    }

    [Fact]
    public void Readiness_fails_when_turn_endpoints_have_no_server_side_secret()
    {
        var service = new TurnCredentialService(
            Options.Create(new SignalingOptions
            {
                Turn = new TurnOptions { TurnUrls = ["turn:turn.example.com:3478"] },
            }),
            TimeProvider.System);

        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task Docker_secret_file_is_read_without_embedding_a_static_client_credential()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"peeronq-turn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var secretFile = Path.Combine(directory, "turn-secret");

        try
        {
            await File.WriteAllTextAsync(secretFile, "file-backed-test-secret-with-32-characters");
            var service = new TurnCredentialService(
                Options.Create(new SignalingOptions
                {
                    Turn = new TurnOptions
                    {
                        SharedSecretFile = secretFile,
                        TurnUrls = ["turn:turn.example.net:3478?transport=udp"],
                    },
                }),
                TimeProvider.System);

            Assert.True(service.IsReady);
            var issued = service.Issue(SessionId.New().ToString(), PeerOnQId.Parse("LNK-111-222-333-444"));
            Assert.Contains(issued.Servers, server => server.Credential is { Length: > 0 });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class ProductionConfigurationTests
{
    [Fact]
    public void Signaling_app_refuses_incomplete_production_configuration_before_startup()
    {
        var exception = Assert.Throws<OptionsValidationException>(() =>
        {
            using var app = SignalingApp.Create(["--environment", Environments.Production]);
        });

        Assert.Contains(exception.Failures, failure =>
            failure.Contains("TURN endpoints", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_defaults_fail_closed()
    {
        var result = new SignalingOptionsValidator(new NamedEnvironment(Environments.Production))
            .Validate(null, new SignalingOptions());

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("TURN endpoints", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("TURN secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Configured_turn_rejects_secret_characters_not_accepted_by_coturn_entrypoint()
    {
        var options = new SignalingOptions
        {
            Turn = new TurnOptions
            {
                SharedSecret = $"{new string('a', 32)}!",
                TurnUrls = ["turn:turn.peeronq.app:3478?transport=udp"],
            },
        };

        var result = new SignalingOptionsValidator(new NamedEnvironment(Environments.Development))
            .Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("base64-compatible", StringComparison.Ordinal));
    }

    [Fact]
    public void Complete_production_configuration_is_accepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyFile = Path.Combine(Path.GetTempPath(), $"peeronq-attestation-{Guid.NewGuid():N}.pem");
        try
        {
            File.WriteAllText(publicKeyFile, key.ExportSubjectPublicKeyInfoPem());
            var result = new SignalingOptionsValidator(new NamedEnvironment(Environments.Production))
                .Validate(null, new SignalingOptions
                {
                    TrustedProxyIp = "172.30.0.10",
                    Attestation = new SignalingAttestationOptions
                    {
                        Required = true,
                        Issuer = "https://identity.peeronq.app",
                        Audience = "peeronq-signaling",
                        PublicKeyFiles = [publicKeyFile],
                    },
                    Turn = new TurnOptions
                    {
                        Realm = "turn.peeronq.app",
                        ServerId = "turn-eu-1",
                        Region = "eu-west",
                        SharedSecret = "production-validation-test-secret-32-chars",
                        StunUrls = ["stun:turn.peeronq.app:3478"],
                        TurnUrls =
                    [
                        "turn:turn.peeronq.app:3478?transport=udp",
                        "turn:turn.peeronq.app:3478?transport=tcp",
                        "turns:turn.peeronq.app:5349?transport=tcp",
                    ],
                    },
                });

            Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
        }
        finally
        {
            File.Delete(publicKeyFile);
        }
    }

    private sealed class NamedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PeerOnQ.Signaling.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

public sealed class TurnIssuanceAndResumeFlowTests
{
    [Fact]
    public void Resume_token_is_consumed_by_the_first_valid_claim()
    {
        var registry = new SessionRegistry(
            Options.Create(new SignalingOptions()),
            NullLogger<SessionRegistry>.Instance,
            TimeProvider.System);
        var viewer = PeerOnQId.Parse("LNK-111-222-333-444");
        var sharer = PeerOnQId.Parse("LNK-555-666-777-888");
        var sessionId = SessionId.New().ToString();

        Assert.True(registry.TryCreate(sessionId, viewer, sharer, "view-only", out _));
        registry.BindOwner(sessionId, viewer, "old-connection");
        var token = registry.IssueResumeToken(sessionId, viewer, SignalingProtocol.CurrentVersion);

        Assert.False(registry.TryResume(
            sessionId,
            viewer,
            token.Token,
            "wrong-version",
            SignalingProtocol.CurrentVersion + 1,
            out _));
        Assert.True(registry.TryResume(
            sessionId,
            viewer,
            token.Token,
            "new-connection",
            SignalingProtocol.CurrentVersion,
            out _));
        Assert.False(registry.TryResume(
            sessionId,
            viewer,
            token.Token,
            "replay-connection",
            SignalingProtocol.CurrentVersion,
            out _));
        Assert.True(registry.IsOwnedBy(sessionId, viewer, "new-connection"));
    }

    [Theory]
    [InlineData("awaiting_network_feedback")]
    [InlineData("viewer_video_stall")]
    [InlineData("capture_restart")]
    public async Task Turn_credentials_are_refused_before_acceptance_and_issued_after_acceptance(string qualityReason)
    {
        const string secret = "integration-secret-with-at-least-32-characters";
        await using var harness = await SignalingHarness.StartAsync(options =>
        {
            options.Turn.SharedSecret = secret;
            options.Turn.StunUrls = ["stun:turn.test:3478"];
            options.Turn.TurnUrls =
            [
                "turn:turn.test:3478?transport=udp",
                "turn:turn.test:3478?transport=tcp",
            ];
            options.Turn.ServerId = "turn-test-1";
            options.Turn.Region = "test";
        });

        using var viewer = new TestDevice("Viewer");
        using var sharer = new TestDevice("Sharer");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            viewerClient.GetIceConfigurationAsync(SessionId.New()));

        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        var permission = new TaskCompletionSource<PermissionResultNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        viewerClient.PermissionResolved += (_, notification) => permission.TrySetResult(notification);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        await Wait.ForAsync(incoming);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        await Wait.ForAsync(permission);

        var duplicateDecision = new TaskCompletionSource<SignalingErrorNotification>();
        sharerClient.ErrorReceived += (_, error) =>
        {
            if (error.Code == SignalingErrorCodes.InvalidSessionState)
                duplicateDecision.TrySetResult(error);
        };
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        Assert.Equal(
            SignalingErrorCodes.InvalidSessionState,
            (await Wait.ForAsync(duplicateDecision)).Code);

        var viewerIce = await viewerClient.GetIceConfigurationAsync(sessionId);
        var sharerIce = await sharerClient.GetIceConfigurationAsync(sessionId);

        Assert.Equal("turn-test-1", viewerIce.RelayServerId);
        Assert.Contains(viewerIce.Servers.SelectMany(server => server.Urls), url => url.Contains("transport=udp"));
        Assert.Contains(viewerIce.Servers.SelectMany(server => server.Urls), url => url.Contains("transport=tcp"));
        Assert.All(
            viewerIce.Servers.Where(server => server.Username is not null),
            server => Assert.True(server.CredentialExpiresAt > DateTimeOffset.UtcNow));
        Assert.NotEqual(
            viewerIce.Servers.Single(server => server.Username is not null).Username,
            sharerIce.Servers.Single(server => server.Username is not null).Username);

        var receivedQuality = new TaskCompletionSource<SessionQualityNotification>();
        viewerClient.QualityReceived += (_, quality) => receivedQuality.TrySetResult(quality);
        await sharerClient.SendQualityAsync(new SessionQualityNotification(
            sessionId,
            30,
            29,
            0.5,
            4,
            8_000,
            2,
            SourceWidth: 2560,
            SourceHeight: 1440,
            RequestedWidth: 1920,
            RequestedHeight: 1080,
            EncodedWidth: 1280,
            EncodedHeight: 720,
            EncoderName: "SoftwareVp8",
            EncoderHardwareAccelerated: false,
            QualityChangeReason: qualityReason));

        var quality = await Wait.ForAsync(receivedQuality);
        Assert.Equal(sessionId, quality.SessionId);
        Assert.Equal(30, quality.CaptureFps);
        Assert.Equal(8_000, quality.AvailableOutgoingBitrateKbps);
        Assert.Equal(2560, quality.SourceWidth);
        Assert.Equal(1440, quality.SourceHeight);
        Assert.Equal(1920, quality.RequestedWidth);
        Assert.Equal(1080, quality.RequestedHeight);
        Assert.Equal(1280, quality.EncodedWidth);
        Assert.Equal(720, quality.EncodedHeight);
        Assert.Equal("SoftwareVp8", quality.EncoderName);
        Assert.False(quality.EncoderHardwareAccelerated);
        Assert.Equal(qualityReason, quality.QualityChangeReason);

        var receivedPresentation = new TaskCompletionSource<SessionQualityNotification>();
        sharerClient.QualityReceived += (_, feedback) => receivedPresentation.TrySetResult(feedback);
        await viewerClient.SendQualityAsync(new SessionQualityNotification(
            sessionId,
            CaptureFps: 0,
            EncodeFps: 0,
            PacketLossPercent: 0,
            JitterMs: 0,
            AvailableOutgoingBitrateKbps: 0,
            FramesDropped: 0,
            DecodeFps: 29,
            RenderFps: 28,
            DecodeToRenderLatencyP95Ms: 12,
            CaptureToPresentLatencyP95Ms: 48,
            FrameAgeClockUncertaintyMs: 2,
            InputToInjectionLatencyP95Ms: 22,
            InputClockUncertaintyMs: 3));

        var presentation = await Wait.ForAsync(receivedPresentation);
        Assert.Equal(29, presentation.DecodeFps);
        Assert.Equal(28, presentation.RenderFps);
        Assert.Equal(12, presentation.DecodeToRenderLatencyP95Ms);
        Assert.Equal(48, presentation.CaptureToPresentLatencyP95Ms);
        Assert.Equal(2, presentation.FrameAgeClockUncertaintyMs);
        Assert.Equal(22, presentation.InputToInjectionLatencyP95Ms);
        Assert.Equal(3, presentation.InputClockUncertaintyMs);

        var malformedQuality = new TaskCompletionSource<SignalingErrorNotification>();
        sharerClient.ErrorReceived += (_, notification) =>
        {
            if (notification.Code == SignalingErrorCodes.MalformedMessage)
                malformedQuality.TrySetResult(notification);
        };
        await sharerClient.SendAsync(new SessionQualityMessage
        {
            SessionId = sessionId.ToString(),
            CaptureFps = 30,
            EncodeFps = 30,
            PacketLossPercent = 0,
            JitterMs = 0,
            AvailableOutgoingBitrateKbps = 8_000,
            FramesDropped = 0,
            EncodedWidth = 1280,
            EncodedHeight = 1080,
            EncoderName = "SoftwareVp8",
            InputToInjectionLatencyP95Ms = 60_001,
        });
        Assert.Equal(
            SignalingErrorCodes.MalformedMessage,
            (await Wait.ForAsync(malformedQuality)).Code);

        // 0.9.86: a reason code the server did not know (capture_restart) dropped the whole report
        // with malformed_message. A well-formed unknown code is relayed now, a malformed one is
        // dropped from the report, and the metrics arrive either way.
        malformedQuality = new TaskCompletionSource<SignalingErrorNotification>();
        var unknownReason = new TaskCompletionSource<SessionQualityNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewerClient.QualityReceived += (_, feedback) =>
        {
            if (feedback.CaptureFps == 21) unknownReason.TrySetResult(feedback);
        };
        await sharerClient.SendQualityAsync(new SessionQualityNotification(
            sessionId, 21, 20, 0.5, 4, 8_000, 2,
            QualityChangeReason: "a_reason_from_a_newer_client"));
        Assert.Equal("a_reason_from_a_newer_client", (await Wait.ForAsync(unknownReason)).QualityChangeReason);

        var malformedReason = new TaskCompletionSource<SessionQualityNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewerClient.QualityReceived += (_, feedback) =>
        {
            if (feedback.CaptureFps == 22) malformedReason.TrySetResult(feedback);
        };
        await sharerClient.SendQualityAsync(new SessionQualityNotification(
            sessionId, 22, 20, 0.5, 4, 8_000, 2,
            QualityChangeReason: "Not A Reason <script>"));
        var relayed = await Wait.ForAsync(malformedReason);
        Assert.Null(relayed.QualityChangeReason);
        Assert.Equal(8_000, relayed.AvailableOutgoingBitrateKbps);
        Assert.False(malformedQuality.Task.IsCompleted);
    }

    [Fact]
    public void A_rotated_resume_token_leaves_the_previous_one_valid_until_it_expires()
    {
        var time = new ManualSignalingTime(DateTimeOffset.Parse("2026-10-24T17:18:49Z"));
        var registry = new SessionRegistry(
            Options.Create(new SignalingOptions { ResumeTokenLifetime = TimeSpan.FromMinutes(2) }),
            NullLogger<SessionRegistry>.Instance,
            time);
        var viewer = PeerOnQId.Parse("LNK-111-222-333-444");
        var sharer = PeerOnQId.Parse("LNK-555-666-777-888");
        var sessionId = SessionId.New().ToString();
        Assert.True(registry.TryCreate(sessionId, viewer, sharer, "view-only", out _));
        registry.BindOwner(sessionId, viewer, "first-connection");

        var first = registry.IssueResumeToken(sessionId, viewer, SignalingProtocol.CurrentVersion);
        time.Advance(TimeSpan.FromSeconds(70));
        var rotated = registry.IssueResumeToken(sessionId, viewer, SignalingProtocol.CurrentVersion);

        // The rotated token may have been lost with the connection; the previous one still works.
        Assert.True(registry.TryResume(
            sessionId, viewer, first.Token, "second-connection", SignalingProtocol.CurrentVersion, out _));
        // Both are consumed by that resume.
        Assert.False(registry.TryResume(
            sessionId, viewer, rotated.Token, "third-connection", SignalingProtocol.CurrentVersion, out _));

        var renewed = registry.IssueResumeToken(sessionId, viewer, SignalingProtocol.CurrentVersion);
        time.Advance(TimeSpan.FromMinutes(1));
        var newest = registry.IssueResumeToken(sessionId, viewer, SignalingProtocol.CurrentVersion);
        time.Advance(TimeSpan.FromSeconds(61));
        // The replaced token keeps only its own lifetime.
        Assert.False(registry.TryResume(
            sessionId, viewer, renewed.Token, "late-connection", SignalingProtocol.CurrentVersion, out _));
        Assert.True(registry.TryResume(
            sessionId, viewer, newest.Token, "late-connection", SignalingProtocol.CurrentVersion, out _));
    }

    [Fact]
    public void Resume_refusals_name_their_reason_for_the_server_log()
    {
        var time = new ManualSignalingTime(DateTimeOffset.Parse("2026-10-24T17:18:49Z"));
        var registry = new SessionRegistry(
            Options.Create(new SignalingOptions()),
            NullLogger<SessionRegistry>.Instance,
            time);
        var viewer = PeerOnQId.Parse("LNK-111-222-333-444");
        var sharer = PeerOnQId.Parse("LNK-555-666-777-888");
        var sessionId = SessionId.New().ToString();
        Assert.True(registry.TryCreate(sessionId, viewer, sharer, "view-only", out var session));
        registry.BindOwner(sessionId, viewer, "old-connection");
        Assert.True(registry.TryAcceptScopeAsync(sessionId, "view-only", 1).AsTask().Result);

        string Explain(string? token) => SessionResumeDiagnostics.Explain(
            session, viewer, token, "new-connection", SignalingProtocol.CurrentVersion, time.GetUtcNow()).Reason;

        Assert.Equal("session_not_found", SessionResumeDiagnostics.Explain(
            null, viewer, "x", "new-connection", SignalingProtocol.CurrentVersion, time.GetUtcNow()).Reason);
        Assert.Equal("token_missing", Explain("AAAA"));

        var issued = registry.IssueResumeToken(sessionId, viewer, SignalingProtocol.CurrentVersion);
        Assert.Equal("token_mismatch", Explain(Convert.ToBase64String(new byte[32])));
        Assert.Equal("token_malformed", Explain("not base64!"));

        // The 0.9.86 field case: the token issued at accept had expired long before the restart.
        time.Advance(TimeSpan.FromMinutes(5));
        var expired = SessionResumeDiagnostics.Explain(
            session, viewer, issued.Token, "new-connection", SignalingProtocol.CurrentVersion, time.GetUtcNow());
        Assert.Equal("token_expired", expired.Reason);
        Assert.Equal("an earlier connection", expired.Owner);

        registry.MarkDisconnected(viewer, "old-connection");
        time.Advance(new SignalingOptions().DisconnectGracePeriod + TimeSpan.FromSeconds(1));
        Assert.Equal("disconnect_window_expired", Explain(issued.Token));
    }

    [Fact]
    public async Task A_live_session_keeps_a_resumable_token_after_the_first_token_expired()
    {
        // Stand-ins for the 2 min token and the 5.5 min between accept and the 0.9.86 restart.
        await using var harness = await SignalingHarness.StartAsync(options =>
        {
            options.DisconnectGracePeriod = TimeSpan.FromSeconds(1.5);
            options.ResumeTokenLifetime = TimeSpan.FromSeconds(2);
        });

        using var viewer = new TestDevice("Viewer");
        using var sharer = new TestDevice("Sharer");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = new WebSocketSignalingClient(
            new SignalingClientOptions
            {
                ServerUri = harness.WebSocketUri,
                ClientCapabilities = TestClientCapabilities.All(),
                AutoReconnect = true,
                ReconnectDelay = TimeSpan.FromMilliseconds(20),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectWindow = TimeSpan.FromSeconds(5),
                MaxReconnectAttempts = 10,
            },
            sharer);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        var permission = new TaskCompletionSource<PermissionResultNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        viewerClient.PermissionResolved += (_, notification) => permission.TrySetResult(notification);
        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        await Wait.ForAsync(incoming);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        await Wait.ForAsync(permission);

        // A live session sends quality reports four times a second; run well past the first token.
        var errors = new List<SignalingErrorNotification>();
        sharerClient.ErrorReceived += (_, error) => { lock (errors) errors.Add(error); };
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            await sharerClient.SendQualityAsync(new SessionQualityNotification(sessionId, 30, 30, 0, 2, 0, 0));
            await Task.Delay(250);
        }
        lock (errors) Assert.Empty(errors);

        var registry = harness.Service<DeviceRegistry>();
        Assert.True(registry.TryGet(sharer.Id, out var oldConnection));
        var oldConnectionId = oldConnection.ConnectionId;
        await oldConnection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "server_restarting");
        Assert.True(await Wait.UntilAsync(
            () => sharerClient.State == SignalingConnectionState.Registered
                  && registry.TryGet(sharer.Id, out var current)
                  && current.ConnectionId != oldConnectionId,
            TimeSpan.FromSeconds(10)));

        var resumed = await sharerClient.ResumeSessionAsync(sessionId);
        Assert.True(resumed.Resumed, resumed.Reason);
    }

    [Fact]
    public async Task A_refused_session_request_error_names_the_session_it_refused()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var device = new TestDevice("Self");
        await using var client = device.CreateClient(harness.WebSocketUri);
        await client.ConnectAsync(device.Identity);
        var refused = new TaskCompletionSource<SignalingErrorNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ErrorReceived += (_, error) => refused.TrySetResult(error);

        var sessionId = SessionId.New();
        await client.RequestSessionAsync(sessionId, device.Id, SessionMode.ViewOnly);

        var error = await Wait.ForAsync(refused);
        Assert.Equal(SignalingErrorCodes.TargetIsSelf, error.Code);
        Assert.Equal(sessionId, error.SessionId);
    }

    [Fact]
    public async Task Silent_connection_is_removed_after_the_heartbeat_deadline()
    {
        await using var harness = await SignalingHarness.StartAsync(options =>
        {
            options.HeartbeatInterval = TimeSpan.FromMilliseconds(50);
            options.HeartbeatTimeout = TimeSpan.FromMilliseconds(150);
            options.SweepInterval = TimeSpan.FromMilliseconds(25);
        });

        using var device = new TestDevice("Silent Viewer");
        await using var client = new WebSocketSignalingClient(
            new SignalingClientOptions
            {
                ServerUri = harness.WebSocketUri,
                ClientCapabilities = TestClientCapabilities.All(),
                HeartbeatInterval = TimeSpan.FromSeconds(10),
                AutoReconnect = false,
            },
            device);
        await client.ConnectAsync(device.Identity);

        var registry = harness.Service<DeviceRegistry>();
        Assert.True(registry.IsOnline(device.Id));
        Assert.True(await Wait.UntilAsync(
            () => !registry.IsOnline(device.Id),
            TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Reauthenticated_connection_can_resume_with_the_short_lived_token()
    {
        await using var harness = await SignalingHarness.StartAsync(options =>
        {
            options.DisconnectGracePeriod = TimeSpan.FromSeconds(5);
            options.ResumeTokenLifetime = TimeSpan.FromSeconds(20);
        });

        using var viewer = new TestDevice("Viewer");
        using var sharer = new TestDevice("Sharer");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = new WebSocketSignalingClient(
            new SignalingClientOptions
            {
                ServerUri = harness.WebSocketUri,
                ClientCapabilities = TestClientCapabilities.All(),
                AutoReconnect = true,
                ReconnectDelay = TimeSpan.FromMilliseconds(20),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectWindow = TimeSpan.FromSeconds(5),
                MaxReconnectAttempts = 10,
            },
            sharer);

        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        var permission = new TaskCompletionSource<PermissionResultNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        viewerClient.PermissionResolved += (_, notification) => permission.TrySetResult(notification);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        await Wait.ForAsync(incoming);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        await Wait.ForAsync(permission);

        var registry = harness.Service<DeviceRegistry>();
        Assert.True(registry.TryGet(sharer.Id, out var oldConnection));
        var oldConnectionId = oldConnection.ConnectionId;
        await oldConnection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "network_change_test");

        Assert.True(await Wait.UntilAsync(
            () => sharerClient.State == SignalingConnectionState.Registered
                  && registry.TryGet(sharer.Id, out var current)
                  && current.ConnectionId != oldConnectionId,
            TimeSpan.FromSeconds(10)));

        var peerResumed = new TaskCompletionSource<SessionPeerResumedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewerClient.PeerResumed += (_, notification) => peerResumed.TrySetResult(notification);
        var resumed = await sharerClient.ResumeSessionAsync(sessionId);
        Assert.True(resumed.Resumed, resumed.Reason);
        Assert.Equal(sessionId, (await Wait.ForAsync(peerResumed)).SessionId);

        var relayed = new TaskCompletionSource<SdpNotification>();
        viewerClient.SdpReceived += (_, notification) => relayed.TrySetResult(notification);
        await sharerClient.SendSdpAsync(sessionId, "offer", "v=0 resumed-offer");
        Assert.Equal("v=0 resumed-offer", (await Wait.ForAsync(relayed)).Sdp);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_resume_tells_the_online_peer_whether_the_resuming_media_stayed_connected(bool mediaConnected)
    {
        // 0.9.88: a peer whose media stayed up through a signaling restart says so, and the other
        // peer keeps its connection instead of restarting ICE.
        await using var harness = await SignalingHarness.StartAsync(options =>
        {
            options.DisconnectGracePeriod = TimeSpan.FromSeconds(5);
            options.ResumeTokenLifetime = TimeSpan.FromSeconds(20);
        });
        using var viewer = new TestDevice("Viewer");
        using var sharer = new TestDevice("Sharer");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = new WebSocketSignalingClient(
            new SignalingClientOptions
            {
                ServerUri = harness.WebSocketUri,
                ClientCapabilities = TestClientCapabilities.All(),
                AutoReconnect = true,
                ReconnectDelay = TimeSpan.FromMilliseconds(20),
                MaxReconnectDelay = TimeSpan.FromMilliseconds(100),
                MaxReconnectWindow = TimeSpan.FromSeconds(5),
                MaxReconnectAttempts = 10,
            },
            sharer);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        var permission = new TaskCompletionSource<PermissionResultNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        viewerClient.PermissionResolved += (_, notification) => permission.TrySetResult(notification);
        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        await Wait.ForAsync(incoming);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        await Wait.ForAsync(permission);

        var registry = harness.Service<DeviceRegistry>();
        Assert.True(registry.TryGet(sharer.Id, out var oldConnection));
        var oldConnectionId = oldConnection.ConnectionId;
        await oldConnection.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "signaling_restart_test");
        Assert.True(await Wait.UntilAsync(
            () => sharerClient.State == SignalingConnectionState.Registered
                  && registry.TryGet(sharer.Id, out var current)
                  && current.ConnectionId != oldConnectionId,
            TimeSpan.FromSeconds(10)));

        var peerResumed = new TaskCompletionSource<SessionPeerResumedNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewerClient.PeerResumed += (_, notification) => peerResumed.TrySetResult(notification);
        var resumed = await sharerClient.ResumeSessionAsync(sessionId, mediaConnected);
        Assert.True(resumed.Resumed, resumed.Reason);
        var notification = await Wait.ForAsync(peerResumed);
        Assert.Equal(sessionId, notification.SessionId);
        Assert.Equal(mediaConnected, notification.PeerMediaConnected);
    }
}
