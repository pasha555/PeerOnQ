using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Signaling.Server;
using PeerOnQ.Signaling.Server.Registry;
using PeerOnQ.Signaling.Server.Sessions;
using PeerOnQ.Transport;
using PeerOnQ.Transport.Protocol;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public class RegistrationTests
{
    [Fact]
    public async Task A_device_registers_and_becomes_online()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var device = new TestDevice("Sharer PC");
        await using var client = device.CreateClient(harness.WebSocketUri);

        await client.ConnectAsync(device.Identity);

        Assert.Equal(SignalingConnectionState.Registered, client.State);
        Assert.Equal(device.Id, client.RegisteredId);
        Assert.NotNull(client.TokenExpiresAt);
        Assert.True(client.TokenExpiresAt > DateTimeOffset.UtcNow);
        Assert.Contains(SignalingServerCapabilityNames.SessionCapabilityGate, client.ServerCapabilities);
        Assert.Contains(OptionalProtocolFeatureNames.SafeUnknownMessages, client.NegotiatedOptionalFeatures);
    }

    [Fact]
    public async Task A_live_session_reauthenticates_before_token_expiry_without_reconnecting()
    {
        await using var harness = await SignalingHarness.StartAsync(options =>
            options.ConnectionTokenLifetime = TimeSpan.FromSeconds(2));
        using var viewer = new TestDevice("Long-running viewer");
        using var sharer = new TestDevice("Long-running sharer");
        await using var viewerClient = viewer.CreateClient(
            harness.WebSocketUri,
            heartbeatInterval: TimeSpan.FromMilliseconds(50));
        await using var sharerClient = sharer.CreateClient(
            harness.WebSocketUri,
            heartbeatInterval: TimeSpan.FromMilliseconds(50));
        var states = new ConcurrentQueue<SignalingConnectionState>();
        var errors = new ConcurrentQueue<SignalingErrorNotification>();
        viewerClient.StateChanged += (_, state) => states.Enqueue(state);
        sharerClient.StateChanged += (_, state) => states.Enqueue(state);
        viewerClient.ErrorReceived += (_, error) => errors.Enqueue(error);
        sharerClient.ErrorReceived += (_, error) => errors.Enqueue(error);

        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var viewerInitialExpiry = viewerClient.TokenExpiresAt!.Value;
        var sharerInitialExpiry = sharerClient.TokenExpiresAt!.Value;
        var registry = harness.Service<DeviceRegistry>();
        Assert.True(registry.TryGet(viewer.Id, out var viewerConnection));
        Assert.True(registry.TryGet(sharer.Id, out var sharerConnection));

        var incoming = new TaskCompletionSource<IncomingSessionNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new TaskCompletionSource<PermissionResultNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        viewerClient.PermissionResolved += (_, notification) => permission.TrySetResult(notification);
        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        await Wait.ForAsync(incoming);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        Assert.Equal(PermissionDecision.Accept, (await Wait.ForAsync(permission)).Decision);

        Assert.True(await Wait.UntilAsync(
            () => viewerClient.TokenExpiresAt > viewerInitialExpiry
                  && sharerClient.TokenExpiresAt > sharerInitialExpiry
                  && viewer.SignalingAttestationRequests >= 2
                  && sharer.SignalingAttestationRequests >= 2,
            TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromSeconds(2.2));

        var relayed = new TaskCompletionSource<SdpNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SdpReceived += (_, notification) => relayed.TrySetResult(notification);
        await viewerClient.SendSdpAsync(sessionId, "offer", "v=0 long-running-session");
        Assert.Equal("v=0 long-running-session", (await Wait.ForAsync(relayed)).Sdp);

        Assert.Equal(SignalingConnectionState.Registered, viewerClient.State);
        Assert.Equal(SignalingConnectionState.Registered, sharerClient.State);
        Assert.True(viewerClient.TokenExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(sharerClient.TokenExpiresAt > DateTimeOffset.UtcNow);
        Assert.True(registry.TryGet(viewer.Id, out var currentViewerConnection));
        Assert.True(registry.TryGet(sharer.Id, out var currentSharerConnection));
        Assert.Same(viewerConnection, currentViewerConnection);
        Assert.Same(sharerConnection, currentSharerConnection);
        Assert.DoesNotContain(SignalingConnectionState.Reconnecting, states);
        Assert.DoesNotContain(errors, error => error.Code == SignalingErrorCodes.TokenExpired);
    }

    [Fact]
    public async Task Registration_is_rejected_when_the_key_does_not_match_the_pinned_one()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var real = new TestDevice("Sharer PC");
        await using var realClient = real.CreateClient(harness.WebSocketUri);
        await realClient.ConnectAsync(real.Identity);

        // Same PeerOnQ ID, different key pair.
        using var impostor = real.WithNewKeyPair();
        await using var impostorClient = impostor.CreateClient(harness.WebSocketUri);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => impostorClient.ConnectAsync(impostor.Identity));

        Assert.Contains("invalid_proof", error.Message);
    }

    [Fact]
    public async Task A_second_connection_for_the_same_device_replaces_the_first()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var device = new TestDevice("Sharer PC");

        await using var first = device.CreateClient(
            harness.WebSocketUri,
            heartbeatInterval: TimeSpan.FromMilliseconds(50),
            autoReconnect: true);
        await first.ConnectAsync(device.Identity);

        await using var second = device.CreateClient(harness.WebSocketUri);
        await second.ConnectAsync(device.Identity);

        Assert.Equal(SignalingConnectionState.Registered, second.State);
        Assert.True(await Wait.UntilAsync(() => first.State == SignalingConnectionState.Disconnected));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        Assert.Equal(SignalingConnectionState.Disconnected, first.State);
        Assert.Equal(SignalingConnectionState.Registered, second.State);
    }

    [Fact]
    public async Task Removing_a_displaced_connection_does_not_remove_the_new_device_owner()
    {
        using var device = new TestDevice("Sharer PC");
        using var firstSocket = new ClientWebSocket();
        using var secondSocket = new ClientWebSocket();
        var registry = new DeviceRegistry(
            new LocalSignalingBackplane(),
            Options.Create(new SignalingOptions()),
            NullLogger<DeviceRegistry>.Instance);
        var first = new DeviceConnection("first", firstSocket, TimeProvider.System);
        var second = new DeviceConnection("second", secondSocket, TimeProvider.System);
        registry.Add(first);
        registry.Add(second);

        await registry.RegisterAsync(first, device.Id);
        Assert.Same(first, registry.TryGet(device.Id, out var initial) ? initial : null);
        Assert.Same(first, await registry.RegisterAsync(second, device.Id));

        Assert.False(await registry.RemoveAsync(first));
        Assert.True(registry.TryGet(device.Id, out var current));
        Assert.Same(second, current);
        Assert.True(await registry.RemoveAsync(second));
    }

    [Fact]
    public async Task Connecting_to_a_dead_server_reports_a_real_error()
    {
        using var device = new TestDevice("Sharer PC");
        // Port 1 is reserved and never listening.
        await using var client = device.CreateClient(new Uri("ws://127.0.0.1:1/ws"));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(device.Identity));

        Assert.Contains("Cannot reach the signaling server", error.Message);
        Assert.Equal(SignalingConnectionState.Faulted, client.State);
    }

    [Fact]
    public async Task Health_endpoint_reports_online_devices()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var device = new TestDevice("Sharer PC");
        await using var client = device.CreateClient(harness.WebSocketUri);
        await client.ConnectAsync(device.Identity);

        using var http = new HttpClient();
        var body = await http.GetStringAsync(harness.HealthUri);

        Assert.Contains("\"devicesOnline\":1", body);
    }
}

public class PermissionFlowTests
{
    private static async Task<(TestDevice Viewer, WebSocketSignalingClient ViewerClient,
        TestDevice Sharer, WebSocketSignalingClient SharerClient)> ConnectPairAsync(SignalingHarness harness)
    {
        var viewer = new TestDevice("Viewer PC");
        var sharer = new TestDevice("Sharer PC");

        var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        var sharerClient = sharer.CreateClient(harness.WebSocketUri);

        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        return (viewer, viewerClient, sharer, sharerClient);
    }

    [Fact]
    public async Task Accepting_a_request_lets_both_sides_negotiate()
    {
        await using var harness = await SignalingHarness.StartAsync();
        var (viewer, viewerClient, sharer, sharerClient) = await ConnectPairAsync(harness);
        using var _1 = viewer;
        using var _2 = sharer;
        await using var _3 = viewerClient;
        await using var _4 = sharerClient;

        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, e) => incoming.TrySetResult(e);

        var result = new TaskCompletionSource<PermissionResultNotification>();
        viewerClient.PermissionResolved += (_, e) => result.TrySetResult(e);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);

        var request = await Wait.ForAsync(incoming);
        Assert.Equal(sessionId, request.SessionId);
        Assert.Equal(viewer.Id, request.FromDeviceId);
        Assert.Equal("Viewer PC", request.FromDisplayName);
        Assert.Equal(SessionMode.ViewOnly, request.Mode);
        Assert.Equal(QualityProfile.Automatic, request.Quality);
        Assert.True(request.ExpiresAt > DateTimeOffset.UtcNow);

        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);

        var decision = await Wait.ForAsync(result);
        Assert.Equal(PermissionDecision.Accept, decision.Decision);
        Assert.Equal("Sharer PC", decision.PeerDisplayName);

        // SDP and ICE now relay between the two peers.
        var sdpReceived = new TaskCompletionSource<SdpNotification>();
        sharerClient.SdpReceived += (_, e) => sdpReceived.TrySetResult(e);
        await viewerClient.SendSdpAsync(sessionId, "offer", "v=0 fake-offer");
        var sdp = await Wait.ForAsync(sdpReceived);
        Assert.Equal("offer", sdp.SdpType);
        Assert.Equal("v=0 fake-offer", sdp.Sdp);

        var iceReceived = new TaskCompletionSource<IceNotification>();
        viewerClient.IceReceived += (_, e) => iceReceived.TrySetResult(e);
        await sharerClient.SendIceAsync(sessionId, "candidate:1 1 udp 1 127.0.0.1 5000 typ host", "0", 0);
        var ice = await Wait.ForAsync(iceReceived);
        Assert.Contains("candidate:1", ice.Candidate);
    }

    [Fact]
    public async Task Remote_owner_can_reduce_an_attended_full_control_request_to_view_only()
    {
        await using var harness = await SignalingHarness.StartAsync();
        var (viewer, viewerClient, sharer, sharerClient) = await ConnectPairAsync(harness);
        using var _1 = viewer;
        using var _2 = sharer;
        await using var _3 = viewerClient;
        await using var _4 = sharerClient;

        var incoming = new TaskCompletionSource<IncomingSessionNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<PermissionResultNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        viewerClient.PermissionResolved += (_, notification) => result.TrySetResult(notification);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(
            sessionId,
            sharer.Id,
            SessionMode.FullControl,
            Phase1SessionScope.FullControlPermissions,
            SessionAccessKind.Attended,
            unattendedChallengeId: null,
            unattendedProof: null,
            QualityProfile.Quality,
            CaptureResolution.P2160,
            remoteScopeSelectionRequired: true);

        var request = await Wait.ForAsync(incoming);
        Assert.True(request.RemoteScopeSelectionRequired);
        Assert.Equal(Phase1SessionScope.FullControlPermissions, request.Permissions);

        await sharerClient.SendPermissionDecisionAsync(
            sessionId,
            PermissionDecision.Accept,
            SessionMode.ViewOnly,
            SessionPermission.ViewScreen);

        var decision = await Wait.ForAsync(result);
        Assert.Equal(PermissionDecision.Accept, decision.Decision);
        Assert.Equal(SessionMode.ViewOnly, decision.GrantedMode);
        Assert.Equal(SessionPermission.ViewScreen, decision.GrantedPermissions);

        var stored = await harness.Service<ISessionStore>().GetAsync(sessionId.ToString());
        Assert.NotNull(stored);
        Assert.Equal("view-only", stored!.Mode);
        Assert.Equal((int)SessionPermission.ViewScreen, stored.Permissions);
    }

    [Fact]
    public async Task Negotiated_file_relay_routes_opaque_binary_records_in_both_directions()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        var optionalFeatures = new[]
        {
            OptionalProtocolFeatureNames.FileRelay,
            OptionalProtocolFeatureNames.SafeUnknownMessages,
            OptionalProtocolFeatureNames.SessionCapabilityDetails,
        };
        await using var viewerClient = viewer.CreateClient(
            harness.WebSocketUri,
            TestClientCapabilities.All(optionalFeatures: optionalFeatures));
        await using var sharerClient = sharer.CreateClient(
            harness.WebSocketUri,
            TestClientCapabilities.All(optionalFeatures: optionalFeatures));
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var incoming = new TaskCompletionSource<IncomingSessionNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource<PermissionResultNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        viewerClient.PermissionResolved += (_, notification) => accepted.TrySetResult(notification);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(
            sessionId,
            sharer.Id,
            SessionMode.FileTransferOnly,
            SessionPermission.FileTransfer,
            SessionAccessKind.Attended);
        Assert.True((await Wait.ForAsync(incoming)).FileRelay);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        Assert.True((await Wait.ForAsync(accepted)).FileRelay);
        Assert.True(viewerClient.IsFileRelayAvailable);
        Assert.True(sharerClient.IsFileRelayAvailable);

        // Transition to active first, then verify that the server routes opaque records rather
        // than interpreting their content.
        var sdpReceived = new TaskCompletionSource<SdpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SdpReceived += (_, notification) => sdpReceived.TrySetResult(notification);
        await viewerClient.SendSdpAsync(sessionId, "offer", "v=0 file-relay-offer");
        await Wait.ForAsync(sdpReceived);

        var fromViewer = new TaskCompletionSource<FileRelayFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fromSharer = new TaskCompletionSource<FileRelayFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.FileRelayReceived += (_, frame) => fromViewer.TrySetResult(frame);
        viewerClient.FileRelayReceived += (_, frame) => fromSharer.TrySetResult(frame);
        var viewerPayload = Enumerable.Repeat((byte)0x5A, FileRelayFrameCodec.MaxPayloadBytes).ToArray();
        var sharerPayload = Enumerable.Repeat((byte)0xA5, FileRelayFrameCodec.MaxPayloadBytes).ToArray();

        await viewerClient.SendFileRelayAsync(sessionId, viewerPayload);
        await sharerClient.SendFileRelayAsync(sessionId, sharerPayload);

        Assert.Equal(viewerPayload, (await Wait.ForAsync(fromViewer)).Payload);
        Assert.Equal(sharerPayload, (await Wait.ForAsync(fromSharer)).Payload);
    }

    [Fact]
    public async Task File_relay_completion_work_does_not_expire_the_signaling_heartbeat()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        var optionalFeatures = new[]
        {
            OptionalProtocolFeatureNames.FileRelay,
            OptionalProtocolFeatureNames.SafeUnknownMessages,
            OptionalProtocolFeatureNames.SessionCapabilityDetails,
        };
        var options = new SignalingClientOptions
        {
            ServerUri = harness.WebSocketUri,
            ClientCapabilities = TestClientCapabilities.All(optionalFeatures: optionalFeatures),
            HeartbeatInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
            AutoReconnect = false,
        };
        await using var viewerClient = new WebSocketSignalingClient(options, viewer);
        await using var sharerClient = new WebSocketSignalingClient(options, sharer);
        var states = new ConcurrentQueue<SignalingConnectionState>();
        sharerClient.StateChanged += (_, state) => states.Enqueue(state);

        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var incoming = new TaskCompletionSource<IncomingSessionNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource<PermissionResultNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        viewerClient.PermissionResolved += (_, notification) => accepted.TrySetResult(notification);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(
            sessionId,
            sharer.Id,
            SessionMode.FileTransferOnly,
            SessionPermission.FileTransfer,
            SessionAccessKind.Attended);
        await Wait.ForAsync(incoming);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);
        await Wait.ForAsync(accepted);

        var sdpReceived = new TaskCompletionSource<SdpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SdpReceived += (_, notification) => sdpReceived.TrySetResult(notification);
        await viewerClient.SendSdpAsync(sessionId, "offer", "v=0 file-relay-heartbeat");
        await Wait.ForAsync(sdpReceived);

        var completionWorkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.FileRelayReceived += (_, _) =>
        {
            completionWorkStarted.TrySetResult();
            Thread.Sleep(300);
        };

        await viewerClient.SendFileRelayAsync(sessionId, new byte[] { 1, 2, 3 });
        await completionWorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(450);

        Assert.Equal(SignalingConnectionState.Registered, sharerClient.State);
        Assert.DoesNotContain(SignalingConnectionState.Reconnecting, states);
    }

    [Fact]
    public async Task Declining_a_request_tells_the_viewer_and_drops_the_session()
    {
        await using var harness = await SignalingHarness.StartAsync();
        var (viewer, viewerClient, sharer, sharerClient) = await ConnectPairAsync(harness);
        using var _1 = viewer;
        using var _2 = sharer;
        await using var _3 = viewerClient;
        await using var _4 = sharerClient;

        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, e) => incoming.TrySetResult(e);
        var result = new TaskCompletionSource<PermissionResultNotification>();
        viewerClient.PermissionResolved += (_, e) => result.TrySetResult(e);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        await Wait.ForAsync(incoming);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Decline);

        var decision = await Wait.ForAsync(result);
        Assert.Equal(PermissionDecision.Decline, decision.Decision);

        // Relaying into a declined session is refused.
        var error = new TaskCompletionSource<SignalingErrorNotification>();
        viewerClient.ErrorReceived += (_, e) => error.TrySetResult(e);
        await viewerClient.SendSdpAsync(sessionId, "offer", "v=0");

        Assert.Equal(SignalingErrorCodes.UnknownSession, (await Wait.ForAsync(error)).Code);
    }

    [Fact]
    public async Task No_answer_within_the_timeout_ends_the_session()
    {
        await using var harness = await SignalingHarness.StartAsync(o =>
        {
            o.PermissionTimeout = TimeSpan.FromMilliseconds(400);
            o.SweepInterval = TimeSpan.FromMilliseconds(100);
        });

        var (viewer, viewerClient, sharer, sharerClient) = await ConnectPairAsync(harness);
        using var _1 = viewer;
        using var _2 = sharer;
        await using var _3 = viewerClient;
        await using var _4 = sharerClient;

        var ended = new TaskCompletionSource<SessionEndedNotification>();
        viewerClient.SessionEnded += (_, e) => ended.TrySetResult(e);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);

        var end = await Wait.ForAsync(ended, TimeSpan.FromSeconds(5));

        Assert.Equal(sessionId, end.SessionId);
        Assert.Equal(SessionEndReason.PermissionTimeout, end.Reason);
    }

    [Fact]
    public async Task A_third_device_cannot_answer_someone_elses_permission_request()
    {
        await using var harness = await SignalingHarness.StartAsync();
        var (viewer, viewerClient, sharer, sharerClient) = await ConnectPairAsync(harness);
        using var _1 = viewer;
        using var _2 = sharer;
        await using var _3 = viewerClient;
        await using var _4 = sharerClient;

        using var stranger = new TestDevice("Stranger PC");
        await using var strangerClient = stranger.CreateClient(harness.WebSocketUri);
        await strangerClient.ConnectAsync(stranger.Identity);

        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, e) => incoming.TrySetResult(e);
        var strangerError = new TaskCompletionSource<SignalingErrorNotification>();
        strangerClient.ErrorReceived += (_, e) => strangerError.TrySetResult(e);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        await Wait.ForAsync(incoming);

        await strangerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);

        Assert.Equal(SignalingErrorCodes.NotSessionParticipant, (await Wait.ForAsync(strangerError)).Code);
    }

    [Fact]
    public async Task Either_side_can_end_the_session()
    {
        await using var harness = await SignalingHarness.StartAsync();
        var (viewer, viewerClient, sharer, sharerClient) = await ConnectPairAsync(harness);
        using var _1 = viewer;
        using var _2 = sharer;
        await using var _3 = viewerClient;
        await using var _4 = sharerClient;

        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, e) => incoming.TrySetResult(e);
        var viewerEnded = new TaskCompletionSource<SessionEndedNotification>();
        viewerClient.SessionEnded += (_, e) => viewerEnded.TrySetResult(e);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        await Wait.ForAsync(incoming);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);

        await sharerClient.EndSessionAsync(sessionId, SessionEndReason.EndedBySharer);

        var end = await Wait.ForAsync(viewerEnded);
        Assert.Equal(SessionEndReason.EndedBySharer, end.Reason);
    }

    [Fact]
    public async Task When_the_sharer_does_not_resume_the_viewer_is_told_after_the_grace_window()
    {
        await using var harness = await SignalingHarness.StartAsync(options =>
        {
            options.DisconnectGracePeriod = TimeSpan.FromMilliseconds(400);
            options.SweepInterval = TimeSpan.FromMilliseconds(50);
        });
        var (viewer, viewerClient, sharer, sharerClient) = await ConnectPairAsync(harness);
        using var _1 = viewer;
        using var _2 = sharer;
        await using var _3 = viewerClient;

        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, e) => incoming.TrySetResult(e);
        var ended = new TaskCompletionSource<SessionEndedNotification>();
        viewerClient.SessionEnded += (_, e) => ended.TrySetResult(e);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);
        await Wait.ForAsync(incoming);
        await sharerClient.SendPermissionDecisionAsync(sessionId, PermissionDecision.Accept);

        await sharerClient.DisposeAsync();

        var end = await Wait.ForAsync(ended, TimeSpan.FromSeconds(10));
        Assert.Equal(SessionEndReason.ReconnectFailed, end.Reason);
    }
}

public class Phase4SignalingTests
{
    [Fact]
    public async Task Support_invitation_credentials_reach_only_the_target_as_an_approval_bound_request()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Support technician");
        using var sharer = new TestDevice("Supported device");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await viewerClient.RequestSupportSessionAsync(
            SessionId.New(),
            sharer.Id,
            SessionMode.FullControl,
            SessionPermission.ViewScreen | SessionPermission.ControlInput,
            token,
            "separate-channel-password",
            QualityProfile.Automatic,
            CaptureResolution.Automatic);

        var request = await Wait.ForAsync(incoming);
        Assert.Equal(SessionAccessKind.SupportInvitation, request.AccessKind);
        Assert.Equal(SessionMode.FullControl, request.Mode);
        Assert.Equal(SessionPermission.ViewScreen | SessionPermission.ControlInput, request.Permissions);
        Assert.Equal(token, request.SupportInvitationToken);
        Assert.Equal("separate-channel-password", request.SupportInvitationPassword);
    }

    [Fact]
    public async Task Requested_quality_profile_reaches_the_sharer()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);

        await viewerClient.RequestSessionAsync(
            SessionId.New(),
            sharer.Id,
            SessionMode.ViewOnly,
            SessionPermission.ViewScreen,
            SessionAccessKind.Attended,
            unattendedChallengeId: null,
            unattendedProof: null,
            QualityProfile.Quality,
            CaptureResolution.P1440);

        var request = await Wait.ForAsync(incoming);
        Assert.Equal(QualityProfile.Quality, request.Quality);
        Assert.Equal(CaptureResolution.P1440, request.Resolution);
    }

    [Fact]
    public async Task Attended_full_control_scope_reaches_the_sharer()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);

        await viewerClient.RequestSessionAsync(
            SessionId.New(),
            sharer.Id,
            SessionMode.FullControl,
            Phase1SessionScope.FullControlPermissions,
            SessionAccessKind.Attended);

        var request = await Wait.ForAsync(incoming);
        Assert.Equal(SessionMode.FullControl, request.Mode);
        Assert.Equal(Phase1SessionScope.FullControlPermissions, request.Permissions);
        Assert.Equal(SessionAccessKind.Attended, request.AccessKind);
    }

    [Fact]
    public async Task Attended_file_transfer_scope_reaches_the_sharer()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);

        await viewerClient.RequestSessionAsync(
            SessionId.New(),
            sharer.Id,
            SessionMode.FileTransferOnly,
            Phase1SessionScope.FileTransferPermissions,
            SessionAccessKind.Attended);

        var request = await Wait.ForAsync(incoming);
        Assert.Equal(SessionMode.FileTransferOnly, request.Mode);
        Assert.Equal(Phase1SessionScope.FileTransferPermissions, request.Permissions);
        Assert.Equal(SessionAccessKind.Attended, request.AccessKind);
    }

    [Fact]
    public async Task Server_routes_an_explicit_phase4_custom_permission_scope()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        var permissions = SessionPermission.ViewScreen | SessionPermission.FileTransfer | SessionPermission.ClipboardText;
        await viewerClient.RequestSessionAsync(
            SessionId.New(),
            sharer.Id,
            SessionMode.Custom,
            permissions,
            SessionAccessKind.Attended);

        var request = await Wait.ForAsync(incoming);
        Assert.Equal(SessionMode.Custom, request.Mode);
        Assert.Equal(permissions, request.Permissions);
        Assert.Equal(SessionAccessKind.Attended, request.AccessKind);
    }

    [Fact]
    public async Task Server_rejects_a_permission_mask_that_does_not_match_the_profile()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var error = new TaskCompletionSource<SignalingErrorNotification>();
        viewerClient.ErrorReceived += (_, notification) => error.TrySetResult(notification);

        await viewerClient.RequestSessionAsync(
            SessionId.New(),
            sharer.Id,
            SessionMode.ViewOnly,
            SessionPermission.FileTransfer,
            SessionAccessKind.Attended);

        Assert.Equal(SignalingErrorCodes.UnsupportedMode, (await Wait.ForAsync(error)).Code);
    }

    [Fact]
    public async Task Address_book_presence_is_reported_from_the_live_registry_not_local_guesswork()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var queryingDevice = new TestDevice("Address Book PC");
        using var onlineDevice = new TestDevice("Online PC");
        using var offlineDevice = new TestDevice("Offline PC");
        await using var queryClient = queryingDevice.CreateClient(harness.WebSocketUri);
        await using var onlineClient = onlineDevice.CreateClient(harness.WebSocketUri);
        await queryClient.ConnectAsync(queryingDevice.Identity);
        await onlineClient.ConnectAsync(onlineDevice.Identity);

        var presence = await queryClient.QueryAsync([onlineDevice.Id, offlineDevice.Id]);
        Assert.Equal(DevicePresence.Online, presence[onlineDevice.Id]);
        Assert.Equal(DevicePresence.Offline, presence[offlineDevice.Id]);
    }

    [Fact]
    public async Task Address_book_presence_is_unknown_while_signaling_is_disconnected()
    {
        using var device = new TestDevice("Offline Address Book PC");
        await using var client = device.CreateClient(new Uri("wss://127.0.0.1:1/ws"));

        var presence = await client.QueryAsync([device.Id]);

        Assert.Equal(DevicePresence.Unknown, presence[device.Id]);
    }

    [Fact]
    public async Task Unattended_password_challenge_routes_only_between_registered_devices()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        sharerClient.UnattendedChallengeRequested += async (_, request) =>
            await sharerClient.SendUnattendedChallengeResponseAsync(
                request.RequestId,
                request.RequesterId,
                new UnattendedChallenge(
                    request.RequestId,
                    true,
                    Guid.NewGuid(),
                    Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
                     600_000,
                     DateTimeOffset.UtcNow.AddSeconds(20),
                     "ok",
                     SessionPermissionPolicy.ForMode(SessionMode.FullControl)));

        var challenge = await viewerClient.RequestUnattendedChallengeAsync(sharer.Id);
        Assert.True(challenge.Available);
        Assert.NotNull(challenge.ChallengeId);
        Assert.Equal(32, challenge.Salt!.Length);
        Assert.Equal(600_000, challenge.Iterations);
        Assert.Equal(
            SessionPermissionPolicy.ForMode(SessionMode.FullControl),
            challenge.AllowedPermissions);
    }
}

public class SignalingValidationTests
{
    [Theory]
    [InlineData("automatic", QualityProfile.Automatic)]
    [InlineData("performance", QualityProfile.Performance)]
    [InlineData("balanced", QualityProfile.Balanced)]
    [InlineData("quality", QualityProfile.Quality)]
    [InlineData("office", QualityProfile.Office)]
    [InlineData("low-bandwidth", QualityProfile.LowBandwidth)]
    public void Quality_profile_wire_values_are_exact_and_round_trip(
        string wireValue,
        QualityProfile expected)
    {
        Assert.True(QualityProfileWire.TryParse(wireValue, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(wireValue, QualityProfileWire.Format(parsed));
    }

    [Theory]
    [InlineData("automatic", CaptureResolution.Automatic)]
    [InlineData("720p", CaptureResolution.P720)]
    [InlineData("1080p", CaptureResolution.P1080)]
    [InlineData("1440p", CaptureResolution.P1440)]
    [InlineData("2160p", CaptureResolution.P2160)]
    public void Capture_resolution_wire_values_are_exact_and_round_trip(
        string wireValue,
        CaptureResolution expected)
    {
        Assert.True(CaptureResolutionWire.TryParse(wireValue, out var parsed));
        Assert.Equal(expected, parsed);
        Assert.Equal(wireValue, CaptureResolutionWire.Format(parsed));
    }

    [Fact]
    public async Task Missing_quality_field_defaults_to_automatic_for_legacy_clients()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Legacy Viewer");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var incoming = new TaskCompletionSource<IncomingSessionNotification>();
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        var request = new SessionRequestMessage
        {
            SessionId = SessionId.New().ToString(),
            TargetId = sharer.Id.Value,
            Mode = "view-only",
            Nonce = Guid.NewGuid().ToString("N"),
            SentAt = DateTimeOffset.UtcNow,
        };
        Assert.DoesNotContain("\"quality\"", SignalingCodec.EncodeToString(request), StringComparison.Ordinal);

        await viewerClient.SendAsync(request);

        var legacyIncoming = await Wait.ForAsync(incoming);
        Assert.Equal(QualityProfile.Automatic, legacyIncoming.Quality);
        Assert.Null(legacyIncoming.Resolution);
    }

    [Fact]
    public async Task Server_rejects_an_unknown_quality_profile_before_creating_a_session()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var error = new TaskCompletionSource<SignalingErrorNotification>();
        viewerClient.ErrorReceived += (_, notification) => error.TrySetResult(notification);

        await viewerClient.SendAsync(new SessionRequestMessage
        {
            SessionId = SessionId.New().ToString(),
            TargetId = sharer.Id.Value,
            Mode = "view-only",
            Quality = "ultra",
            Nonce = Guid.NewGuid().ToString("N"),
            SentAt = DateTimeOffset.UtcNow,
        });

        Assert.Equal(SignalingErrorCodes.MalformedMessage, (await Wait.ForAsync(error)).Code);
        Assert.Equal(0, await harness.Service<ISessionStore>().CountAsync());
    }

    [Fact]
    public async Task Requesting_an_offline_device_returns_target_offline()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);

        var error = new TaskCompletionSource<SignalingErrorNotification>();
        viewerClient.ErrorReceived += (_, e) => error.TrySetResult(e);

        await viewerClient.RequestSessionAsync(
            SessionId.New(), PeerOnQId.Parse("LNK-000-111-222-333"), SessionMode.ViewOnly);

        Assert.Equal(SignalingErrorCodes.TargetOffline, (await Wait.ForAsync(error)).Code);
    }

    [Fact]
    public async Task A_device_cannot_request_a_session_with_itself()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var device = new TestDevice("Viewer PC");
        await using var client = device.CreateClient(harness.WebSocketUri);
        await client.ConnectAsync(device.Identity);

        var error = new TaskCompletionSource<SignalingErrorNotification>();
        client.ErrorReceived += (_, e) => error.TrySetResult(e);

        await client.RequestSessionAsync(SessionId.New(), device.Id, SessionMode.ViewOnly);

        Assert.Equal(SignalingErrorCodes.TargetIsSelf, (await Wait.ForAsync(error)).Code);
    }

    [Fact]
    public async Task A_replayed_nonce_is_rejected()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var error = new TaskCompletionSource<SignalingErrorNotification>();
        viewerClient.ErrorReceived += (_, e) => error.TrySetResult(e);

        var replayed = new SessionRequestMessage
        {
            SessionId = SessionId.New().ToString(),
            TargetId = sharer.Id.Value,
            Mode = "view-only",
            Nonce = "fixed-nonce",
            SentAt = DateTimeOffset.UtcNow,
        };

        await viewerClient.SendAsync(replayed);
        await viewerClient.SendAsync(replayed with { SessionId = SessionId.New().ToString() });

        Assert.Equal(SignalingErrorCodes.ReplayDetected, (await Wait.ForAsync(error)).Code);
    }

    [Fact]
    public async Task A_stale_request_timestamp_is_rejected()
    {
        await using var harness = await SignalingHarness.StartAsync(o => o.MaxRequestClockSkew = TimeSpan.FromSeconds(5));
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var error = new TaskCompletionSource<SignalingErrorNotification>();
        viewerClient.ErrorReceived += (_, e) => error.TrySetResult(e);

        await viewerClient.SendAsync(new SessionRequestMessage
        {
            SessionId = SessionId.New().ToString(),
            TargetId = sharer.Id.Value,
            Mode = "view-only",
            Nonce = Guid.NewGuid().ToString("N"),
            SentAt = DateTimeOffset.UtcNow.AddHours(-1),
        });

        Assert.Equal(SignalingErrorCodes.ReplayDetected, (await Wait.ForAsync(error)).Code);
    }

    [Fact]
    public async Task Session_request_uses_server_time_when_the_local_clock_is_wrong()
    {
        await using var harness = await SignalingHarness.StartAsync(
            options => options.MaxRequestClockSkew = TimeSpan.FromSeconds(5));
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(
            harness.WebSocketUri,
            timeProvider: new OffsetTimeProvider(TimeSpan.FromDays(-1)));
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var incoming = new TaskCompletionSource<IncomingSessionNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SessionRequested += (_, request) => incoming.TrySetResult(request);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.ViewOnly);

        Assert.Equal(sessionId, (await Wait.ForAsync(incoming)).SessionId);
    }

    [Fact]
    public async Task Control_modes_are_refused_when_no_input_controller_is_available()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer PC");
        using var sharer = new TestDevice("Sharer PC");
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var error = new TaskCompletionSource<SignalingErrorNotification>();
        viewerClient.ErrorReceived += (_, e) => error.TrySetResult(e);

        await viewerClient.SendAsync(new SessionRequestMessage
        {
            SessionId = SessionId.New().ToString(),
            TargetId = sharer.Id.Value,
            Mode = "full-control",
            Nonce = Guid.NewGuid().ToString("N"),
            SentAt = DateTimeOffset.UtcNow,
        });

        Assert.Equal(SignalingErrorCodes.UnsupportedMode, (await Wait.ForAsync(error)).Code);
    }

    [Fact]
    public async Task Session_calls_before_registration_are_refused()
    {
        await using var harness = await SignalingHarness.StartAsync();

        using var raw = new ClientWebSocket();
        await raw.ConnectAsync(harness.WebSocketUri, CancellationToken.None);

        var frame = SignalingCodec.Encode(new SessionRequestMessage
        {
            SessionId = SessionId.New().ToString(),
            TargetId = "LNK-111-222-333-444",
            Mode = "view-only",
            Nonce = Guid.NewGuid().ToString("N"),
            SentAt = DateTimeOffset.UtcNow,
        });

        await raw.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);

        var error = await ReadErrorAsync(raw);
        Assert.Equal(SignalingErrorCodes.NotRegistered, error.Code);
    }

    [Fact]
    public async Task Malformed_frames_are_rejected_without_killing_the_connection()
    {
        await using var harness = await SignalingHarness.StartAsync();

        using var raw = new ClientWebSocket();
        await raw.ConnectAsync(harness.WebSocketUri, CancellationToken.None);

        foreach (var garbage in new[] { "not json at all", "{}", "[]" })
        {
            await raw.SendAsync(Encoding.UTF8.GetBytes(garbage), WebSocketMessageType.Text, true, CancellationToken.None);
            var error = await ReadErrorAsync(raw);
            Assert.Equal(SignalingErrorCodes.MalformedMessage, error.Code);
        }

        await raw.SendAsync(
            Encoding.UTF8.GetBytes("{\"type\":\"future.optional\",\"mid\":\"future-1\"}"),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
        var unsupported = await ReadErrorAsync(raw);
        Assert.Equal(SignalingErrorCodes.UnsupportedMessage, unsupported.Code);
        Assert.Equal("future-1", unsupported.InReplyTo);

        // The socket is still usable: a valid hello still gets a challenge.
        await raw.SendAsync(
            SignalingCodec.Encode(new HelloMessage
            {
                DeviceId = "LNK-483-921-756-204",
                DisplayName = "Viewer",
                ClientVersion = "test",
                ProtocolVersion = SignalingProtocol.CurrentVersion,
                ClientCapabilities = TestClientCapabilities.All(),
            }),
            WebSocketMessageType.Text, true, CancellationToken.None);

        Assert.IsType<ChallengeMessage>(await ReadMessageAsync(raw));
    }

    [Fact]
    public async Task An_oversized_frame_is_rejected()
    {
        await using var harness = await SignalingHarness.StartAsync();

        using var raw = new ClientWebSocket();
        await raw.ConnectAsync(harness.WebSocketUri, CancellationToken.None);

        var oversized = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"pad\":\"" + new string('x', 70 * 1024) + "\"}");
        await raw.SendAsync(oversized, WebSocketMessageType.Text, true, CancellationToken.None);

        var error = await ReadErrorAsync(raw);
        Assert.Equal(SignalingErrorCodes.MessageTooLarge, error.Code);
    }

    [Fact]
    public async Task A_flood_of_messages_is_rate_limited()
    {
        await using var harness = await SignalingHarness.StartAsync(o =>
        {
            o.MessagesPerSecond = 5;
            o.MessageBurst = 5;
        });

        using var raw = new ClientWebSocket();
        await raw.ConnectAsync(harness.WebSocketUri, CancellationToken.None);

        var hello = SignalingCodec.Encode(new HelloMessage
        {
            DeviceId = "LNK-483-921-756-204",
            DisplayName = "Flooder",
            ClientVersion = "test",
            ProtocolVersion = SignalingProtocol.CurrentVersion,
            ClientCapabilities = TestClientCapabilities.All(),
        });

        for (var i = 0; i < 40; i++)
        {
            await raw.SendAsync(hello, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        var sawRateLimit = false;
        for (var i = 0; i < 40 && !sawRateLimit; i++)
        {
            var message = await ReadMessageAsync(raw);
            if (message is ErrorMessage { Code: SignalingErrorCodes.RateLimited })
            {
                sawRateLimit = true;
            }
        }

        Assert.True(sawRateLimit, "Expected the server to rate limit a flood of messages.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SignalingProtocol.MinimumSupportedVersion - 1)]
    [InlineData(SignalingProtocol.MaximumSupportedVersion + 1)]
    public async Task An_incompatible_signaling_protocol_is_rejected_before_registration(int protocolVersion)
    {
        await using var harness = await SignalingHarness.StartAsync();

        using var raw = new ClientWebSocket();
        await raw.ConnectAsync(harness.WebSocketUri, CancellationToken.None);

        await raw.SendAsync(
            SignalingCodec.Encode(new HelloMessage
            {
                DeviceId = "LNK-483-921-756-204",
                DisplayName = "Old client",
                ClientVersion = "0.1.0",
                ProtocolVersion = protocolVersion,
            }),
            WebSocketMessageType.Text, true, CancellationToken.None);

        var error = await ReadErrorAsync(raw);
        Assert.Equal(SignalingErrorCodes.UnsupportedVersion, error.Code);
        Assert.Equal(protocolVersion, error.ReceivedProtocolVersion);
        Assert.Equal(SignalingProtocol.MinimumSupportedVersion, error.MinimumProtocolVersion);
        Assert.Equal(SignalingProtocol.MaximumSupportedVersion, error.MaximumProtocolVersion);
    }

    [Fact]
    public async Task Protocol_v3_rejects_an_internally_inconsistent_capability_manifest()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var raw = new ClientWebSocket();
        await raw.ConnectAsync(harness.WebSocketUri, CancellationToken.None);

        await raw.SendAsync(
            SignalingCodec.Encode(new HelloMessage
            {
                DeviceId = "LNK-483-921-756-204",
                DisplayName = "Invalid client",
                ClientVersion = "test",
                ProtocolVersion = SignalingProtocol.CurrentVersion,
                ClientCapabilities = TestClientCapabilities.All(
                    capabilities: [EndpointCapabilityNames.InputSend]),
            }),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        var error = await ReadErrorAsync(raw);
        Assert.Equal(SignalingErrorCodes.InvalidCapabilities, error.Code);
        Assert.Equal("client", error.CapabilitySide);
    }

    [Fact]
    public async Task Registering_with_an_invalid_device_id_is_refused()
    {
        await using var harness = await SignalingHarness.StartAsync();

        using var raw = new ClientWebSocket();
        await raw.ConnectAsync(harness.WebSocketUri, CancellationToken.None);

        await raw.SendAsync(
            SignalingCodec.Encode(new HelloMessage
            {
                DeviceId = "NOT-AN-ID",
                DisplayName = "Viewer",
                ClientVersion = "test",
                ProtocolVersion = SignalingProtocol.CurrentVersion,
                ClientCapabilities = TestClientCapabilities.All(),
            }),
            WebSocketMessageType.Text, true, CancellationToken.None);

        Assert.Equal(SignalingErrorCodes.InvalidDeviceId, (await ReadErrorAsync(raw)).Code);
    }

    private static async Task<ErrorMessage> ReadErrorAsync(ClientWebSocket socket) =>
        Assert.IsType<ErrorMessage>(await ReadMessageAsync(socket));

    private static async Task<SignalingMessage> ReadMessageAsync(ClientWebSocket socket)
    {
        var buffer = new byte[SignalingCodec.MaxFrameBytes];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var offset = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(offset), cts.Token);
            offset += result.Count;
        }
        while (!result.EndOfMessage);

        return SignalingCodec.Decode(buffer.AsSpan(0, offset));
    }
}
