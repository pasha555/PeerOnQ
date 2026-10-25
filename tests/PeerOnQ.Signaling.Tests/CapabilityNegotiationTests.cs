using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Signaling.Server.Sessions;
using PeerOnQ.Transport.Protocol;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class CapabilityNegotiationTests
{
    [Fact]
    public void Audio_requires_explicit_abilities_and_exact_optional_format_on_both_endpoints()
    {
        var legacy = TestClientCapabilities.All(optionalFeatures: []);
        var current = legacy with
        {
            Capabilities = [.. legacy.Capabilities, EndpointCapabilityNames.SystemAudioCapture, EndpointCapabilityNames.SystemAudioPlayback],
            OptionalFeatures = [OptionalProtocolFeatureNames.SystemAudio],
        };
        var scope = SessionPermission.ViewScreen | SessionPermission.SystemAudio;
        var accepted = CapabilityNegotiator.NegotiateSession(current, current, scope, SessionAccessKind.Attended);
        Assert.True(accepted.IsCompatible);
        Assert.Contains(OptionalProtocolFeatureNames.SystemAudio, accepted.NegotiatedOptionalFeatures);
        var oldHost = CapabilityNegotiator.NegotiateSession(current, legacy, scope, SessionAccessKind.Attended);
        Assert.False(oldHost.IsCompatible);
        Assert.Contains(EndpointCapabilityNames.SystemAudioCapture, oldHost.MissingTargetCapabilities);
        var oldViewer = CapabilityNegotiator.NegotiateSession(legacy, current, scope, SessionAccessKind.Attended);
        Assert.False(oldViewer.IsCompatible);
        Assert.Contains(EndpointCapabilityNames.SystemAudioPlayback, oldViewer.MissingRequesterCapabilities);
        var missingFormat = current with { OptionalFeatures = [] };
        Assert.False(CapabilityNegotiator.NegotiateSession(current, missingFormat, scope, SessionAccessKind.Attended).IsCompatible);
        Assert.False(CapabilityNegotiator.NegotiateSession(missingFormat, current, scope, SessionAccessKind.Attended).IsCompatible);
        var ordinary = CapabilityNegotiator.NegotiateSession(current, legacy, SessionPermission.ViewScreen, SessionAccessKind.Attended);
        Assert.True(ordinary.IsCompatible);
        Assert.DoesNotContain(OptionalProtocolFeatureNames.SystemAudio, ordinary.NegotiatedOptionalFeatures);
    }

    [Theory]
    [InlineData(EndpointCapabilityNames.SystemAudioCapture)]
    [InlineData(EndpointCapabilityNames.SystemAudioPlayback)]
    public void Audio_ability_cannot_be_declared_without_its_screen_boundary(string capability)
    {
        var manifest = TestClientCapabilities.All(capabilities: [capability]);
        Assert.False(CapabilityNegotiator.TryNormalize(manifest, out _, out var error));
        Assert.Contains(capability, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_audio_hello_matches_the_shared_utf8_vector_and_old_vectors_remain_unchanged()
    {
        var vector = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestVectors", "hello-windows-audio.json"));
        var message = new HelloMessage
        {
            MessageId = "vector-audio-v3", DeviceId = "123-456-789-012", DisplayName = "Windows Audio",
            ClientVersion = "0.8.0", ProtocolVersion = SignalingProtocol.CurrentVersion,
            ClientCapabilities = new ClientCapabilityManifest
            {
                Platform = PeerOnQClientPlatforms.Windows,
                Capabilities = [EndpointCapabilityNames.SystemAudioCapture, EndpointCapabilityNames.SystemAudioPlayback,
                    EndpointCapabilityNames.ScreenCapture, EndpointCapabilityNames.ScreenRender,
                    EndpointCapabilityNames.SessionHost, EndpointCapabilityNames.SessionViewer],
                OptionalFeatures = [OptionalProtocolFeatureNames.SystemAudio],
                RequiredServerCapabilities = [SignalingServerCapabilityNames.SessionRouting],
            },
        };
        Assert.Equal(vector, SignalingCodec.Encode(message).Append((byte)'\n').ToArray());
        var decoded = Assert.IsType<HelloMessage>(SignalingCodec.Decode(vector.AsSpan(0, vector.Length - 1)));
        Assert.Contains(OptionalProtocolFeatureNames.SystemAudio, decoded.ClientCapabilities!.OptionalFeatures);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unicode_input_is_enabled_only_when_both_peers_advertise_it(bool sharerSupportsUnicode)
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Unicode viewer");
        using var sharer = new TestDevice("Unicode sharer");
        var supported = TestClientCapabilities.All(optionalFeatures: [OptionalProtocolFeatureNames.UnicodeInput]);
        var legacy = TestClientCapabilities.All(optionalFeatures: []);
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri, supported);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri, sharerSupportsUnicode ? supported : legacy);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);
        var incoming = new TaskCompletionSource<IncomingSessionNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var permission = new TaskCompletionSource<PermissionResultNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        sharerClient.SessionRequested += (_, notification) => incoming.TrySetResult(notification);
        viewerClient.PermissionResolved += (_, notification) => permission.TrySetResult(notification);
        var id = SessionId.New();
        await viewerClient.RequestSessionAsync(id, sharer.Id, SessionMode.FullControl);
        Assert.Equal(sharerSupportsUnicode, (await Wait.ForAsync(incoming)).UnicodeInput);
        await sharerClient.SendPermissionDecisionAsync(id, PermissionDecision.Accept);
        Assert.Equal(sharerSupportsUnicode, (await Wait.ForAsync(permission)).UnicodeInput);
        Assert.Equal(sharerSupportsUnicode, (await harness.Service<ISessionStore>().GetAsync(id.ToString()))!.UnicodeInputEnabled);
    }

    [Fact]
    public void Capability_manifest_is_bounded_canonical_and_dependency_checked()
    {
        var unsorted = TestClientCapabilities.All(
            capabilities:
            [
                EndpointCapabilityNames.ScreenRender,
                EndpointCapabilityNames.SessionViewer,
            ],
            optionalFeatures:
            [
                "future.optional.v9",
                OptionalProtocolFeatureNames.SafeUnknownMessages,
            ],
            requiredServerCapabilities: [SignalingServerCapabilityNames.SessionRouting]);

        Assert.True(CapabilityNegotiator.TryNormalize(unsorted, out var normalized, out var error), error);
        Assert.Equal(
            [EndpointCapabilityNames.ScreenRender, EndpointCapabilityNames.SessionViewer],
            normalized.Capabilities);

        var invalid = TestClientCapabilities.All(
            capabilities: [EndpointCapabilityNames.InputSend]);
        Assert.False(CapabilityNegotiator.TryNormalize(invalid, out _, out var invalidError));
        Assert.Contains(EndpointCapabilityNames.InputSend, invalidError, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_feature_negotiation_intersects_known_implemented_features_only()
    {
        var manifest = TestClientCapabilities.All(
            optionalFeatures:
            [
                "future.optional.v9",
                OptionalProtocolFeatureNames.SafeUnknownMessages,
            ]);
        Assert.True(CapabilityNegotiator.TryNormalize(manifest, out var normalized, out var error), error);

        Assert.Equal(
            [OptionalProtocolFeatureNames.SafeUnknownMessages],
            CapabilityNegotiator.NegotiateServerFeatures(normalized));
    }

    [Fact]
    public void Full_control_requires_send_and_inject_on_the_correct_endpoints()
    {
        var requester = TestClientCapabilities.All();
        var target = TestClientCapabilities.All(
            capabilities: TestClientCapabilities.All().Capabilities
                .Where(value => value != EndpointCapabilityNames.InputInject)
                .ToArray());

        var result = CapabilityNegotiator.NegotiateSession(
            requester,
            target,
            SessionPermission.ViewScreen | SessionPermission.ControlInput,
            SessionAccessKind.Attended);

        Assert.False(result.IsCompatible);
        Assert.Empty(result.MissingRequesterCapabilities);
        Assert.Equal([EndpointCapabilityNames.InputInject], result.MissingTargetCapabilities);
    }

    [Fact]
    public void Every_session_rejects_a_peer_without_mandatory_hybrid_post_quantum_security()
    {
        var current = TestClientCapabilities.All();
        var legacyTarget = TestClientCapabilities.All(
            capabilities: current.Capabilities
                .Where(value => value != EndpointCapabilityNames.HybridPostQuantumSecure)
                .ToArray());

        var result = CapabilityNegotiator.NegotiateSession(
            current,
            legacyTarget,
            SessionPermission.ViewScreen,
            SessionAccessKind.Attended);

        Assert.False(result.IsCompatible);
        Assert.Equal(
            [EndpointCapabilityNames.HybridPostQuantumSecure],
            result.MissingTargetCapabilities);
    }

    [Fact]
    public async Task Server_refuses_a_session_before_creation_when_target_did_not_advertise_input_injection()
    {
        await using var harness = await SignalingHarness.StartAsync();
        using var viewer = new TestDevice("Viewer");
        using var sharer = new TestDevice("Sharer");
        var sharerManifest = TestClientCapabilities.All(
            capabilities: TestClientCapabilities.All().Capabilities
                .Where(value => value != EndpointCapabilityNames.InputInject)
                .ToArray());
        await using var viewerClient = viewer.CreateClient(harness.WebSocketUri);
        await using var sharerClient = sharer.CreateClient(harness.WebSocketUri, sharerManifest);
        await viewerClient.ConnectAsync(viewer.Identity);
        await sharerClient.ConnectAsync(sharer.Identity);

        var error = new TaskCompletionSource<SignalingErrorNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewerClient.ErrorReceived += (_, notification) => error.TrySetResult(notification);

        var sessionId = SessionId.New();
        await viewerClient.RequestSessionAsync(sessionId, sharer.Id, SessionMode.FullControl);

        var notification = await Wait.ForAsync(error);
        Assert.Equal(SignalingErrorCodes.CapabilityMismatch, notification.Code);
        Assert.Equal("target", notification.CapabilitySide);
        Assert.Equal([EndpointCapabilityNames.InputInject], notification.RequiredCapabilities);
        Assert.Null(await harness.Service<ISessionStore>().GetAsync(sessionId.ToString()));
    }

    [Fact]
    public void Unknown_message_type_is_decoded_as_a_bounded_non_executable_frame()
    {
        var message = SignalingCodec.Decode(
            "{\"type\":\"future.optional\",\"mid\":\"m-1\",\"payload\":{\"ignored\":true}}"u8);

        var unknown = Assert.IsType<UnknownSignalingMessage>(message);
        Assert.Equal("future.optional", unknown.MessageType);
        Assert.Equal("m-1", unknown.MessageId);
    }

    [Fact]
    public void Canonical_linux_viewer_hello_matches_the_shared_utf8_vector_byte_for_byte()
    {
        var vector = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "TestVectors",
            "hello-linux-viewer.json"));
        var message = new HelloMessage
        {
            MessageId = "vector-hello-v3",
            DeviceId = "123-456-789-012",
            DisplayName = "Linux Viewer",
            ClientVersion = "0.8.0",
            ProtocolVersion = SignalingProtocol.CurrentVersion,
            ClientCapabilities = new ClientCapabilityManifest
            {
                Platform = PeerOnQClientPlatforms.Linux,
                Capabilities =
                [
                    EndpointCapabilityNames.ScreenRender,
                    EndpointCapabilityNames.SessionViewer,
                ],
                OptionalFeatures = [OptionalProtocolFeatureNames.SafeUnknownMessages],
                RequiredServerCapabilities = [SignalingServerCapabilityNames.SessionRouting],
            },
        };
        var encoded = SignalingCodec.Encode(message).Append((byte)'\n').ToArray();

        Assert.Equal(vector, encoded);
        Assert.IsType<HelloMessage>(SignalingCodec.Decode(vector.AsSpan(0, vector.Length - 1)));
    }

    [Fact]
    public void Canonical_v2_rejection_matches_the_shared_utf8_vector_byte_for_byte()
    {
        var vector = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "TestVectors",
            "unsupported-version-v2.json"));
        var message = new ErrorMessage
        {
            MessageId = "vector-error-v3",
            Code = SignalingErrorCodes.UnsupportedVersion,
            Message = "Signaling protocol 2 is unsupported; this server supports 3-3.",
            InReplyTo = "vector-hello-v2",
            ReceivedProtocolVersion = 2,
            MinimumProtocolVersion = SignalingProtocol.MinimumSupportedVersion,
            MaximumProtocolVersion = SignalingProtocol.MaximumSupportedVersion,
        };
        var encoded = SignalingCodec.Encode(message).Append((byte)'\n').ToArray();

        Assert.Equal(vector, encoded);
        var decoded = Assert.IsType<ErrorMessage>(
            SignalingCodec.Decode(vector.AsSpan(0, vector.Length - 1)));
        Assert.Equal(SignalingErrorCodes.UnsupportedVersion, decoded.Code);
        Assert.Equal(2, decoded.ReceivedProtocolVersion);
    }
}
