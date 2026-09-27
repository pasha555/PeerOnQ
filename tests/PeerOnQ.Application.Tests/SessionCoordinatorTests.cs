using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public class SessionCoordinatorTests
{
    private static readonly PeerOnQId Peer = PeerOnQId.Parse("LNK-483-921-756-204");

    private sealed record Harness(
        SessionCoordinator Coordinator,
        FakeSignalingClient Signaling,
        FakeMediaEngine Media,
        FakeCaptureSource Capture,
        InMemoryBlockedDeviceStore Blocked,
        InMemoryAuditLog Audit,
        ScriptedPermissionPrompt Prompt,
        FakeInputSafetyController InputSafety,
        FakeRemoteInputSink RemoteInputSink,
        RecordingClientSessionEventSink Telemetry,
        RecordingSecurityAudit SecurityAudit);

    private sealed record LifecycleHarness(
        SessionCoordinator Coordinator,
        ControlledSignalingClient Signaling,
        FakeMediaEngine Media,
        FakeCaptureSource Capture,
        InMemoryAuditLog Audit);

    private sealed class ControlledSignalingClient : ISignalingClient
    {
        private int _applicationShutdownCalls;

        public Func<CancellationToken, Task>? OnApplicationShutdownAsync { get; init; }
        public Func<CancellationToken, Task<IceConfiguration>>? OnGetIceConfigurationAsync { get; init; }
        public TaskCompletionSource ApplicationShutdownEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SdpSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ApplicationShutdownCalls => Volatile.Read(ref _applicationShutdownCalls);

        public SignalingConnectionState State { get; private set; } = SignalingConnectionState.Registered;
        public PeerOnQId? RegisteredId { get; private set; } = PeerOnQId.Parse("LNK-100-200-300-400");

        public event EventHandler<SignalingConnectionState>? StateChanged;
        public event EventHandler<IncomingSessionNotification>? SessionRequested;
        public event EventHandler<PermissionResultNotification>? PermissionResolved
        {
            add { }
            remove { }
        }
        public event EventHandler<SdpNotification>? SdpReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<IceNotification>? IceReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<SessionEndedNotification>? SessionEnded
        {
            add { }
            remove { }
        }
        public event EventHandler<SignalingErrorNotification>? ErrorReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<RemoteDisplaysNotification>? RemoteDisplaysReceived
        {
            add { }
            remove { }
        }
        public event EventHandler<SelectDisplayNotification>? DisplaySelectionRequested
        {
            add { }
            remove { }
        }

        public Task ConnectAsync(DeviceIdentity identity, CancellationToken cancellationToken = default)
        {
            RegisteredId = identity.PublicId;
            State = SignalingConnectionState.Registered;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = SignalingConnectionState.Disconnected;
            StateChanged?.Invoke(this, State);
            return Task.CompletedTask;
        }

        public Task RequestSessionAsync(
            SessionId sessionId,
            PeerOnQId target,
            SessionMode mode,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendPermissionDecisionAsync(
            SessionId sessionId,
            PermissionDecision decision,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendSdpAsync(
            SessionId sessionId,
            string sdpType,
            string sdp,
            CancellationToken cancellationToken = default)
        {
            SdpSent.TrySetResult();
            return Task.CompletedTask;
        }

        public Task SendIceAsync(
            SessionId sessionId,
            string candidate,
            string? sdpMid,
            ushort sdpMLineIndex,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EndSessionAsync(
            SessionId sessionId,
            SessionEndReason reason,
            CancellationToken cancellationToken = default)
        {
            if (reason != SessionEndReason.ApplicationShutdown)
            {
                return Task.CompletedTask;
            }

            Interlocked.Increment(ref _applicationShutdownCalls);
            ApplicationShutdownEntered.TrySetResult();
            return OnApplicationShutdownAsync?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task<IceConfiguration> GetIceConfigurationAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default) =>
            OnGetIceConfigurationAsync?.Invoke(cancellationToken)
            ?? Task.FromResult(IceConfiguration.DirectOnly);

        public Task SendDisplaysAsync(
            SessionId sessionId,
            IReadOnlyList<CaptureTargetInfo> displays,
            string activeDisplayId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RequestDisplayAsync(
            SessionId sessionId,
            string displayId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void RaiseIncoming(SessionId sessionId) =>
            SessionRequested?.Invoke(this, new IncomingSessionNotification(
                sessionId,
                Peer,
                "Viewer PC",
                SessionMode.ViewOnly,
                DateTimeOffset.UtcNow.AddSeconds(30)));

        public ValueTask DisposeAsync()
        {
            StateChanged = null;
            SessionRequested = null;
            return ValueTask.CompletedTask;
        }
    }

    private static Harness Build(
        PermissionDecision decision = PermissionDecision.Accept,
        TimeSpan? promptDelay = null,
        SessionOptions? options = null,
        bool enableRemoteInput = false,
        bool enableClipboard = false,
        UnattendedAccessService? unattendedAccess = null,
        SessionMode? grantedMode = null,
        SessionPermission? grantedPermissions = null)
    {
        var signaling = new FakeSignalingClient();
        var media = new FakeMediaEngine();
        var capture = new FakeCaptureSource();
        var blocked = new InMemoryBlockedDeviceStore();
        var audit = new InMemoryAuditLog();
        var prompt = new ScriptedPermissionPrompt(
            decision,
            promptDelay,
            grantedMode,
            grantedPermissions);
        var inputSafety = new FakeInputSafetyController();
        var remoteInputSink = new FakeRemoteInputSink();
        var telemetry = new RecordingClientSessionEventSink();
        var securityAudit = new RecordingSecurityAudit();

        var coordinator = new SessionCoordinator(
            signaling, media, prompt, blocked, audit, () => capture,
            options ?? SessionOptions.Default,
            inputSafetyController: inputSafety,
            remoteInputSink: enableRemoteInput ? remoteInputSink : null,
            unattendedAccess: unattendedAccess,
            clipboardFactory: enableClipboard ? () => new FakeClipboardAdapter() : null,
            securityAudit: securityAudit,
            clientSessionEvents: telemetry,
            collaborationTransportFactory: (session, permissions, _) =>
                new FakeSecureCollaborationTransport(session, permissions))
        {
            PreferredCaptureTarget = new CaptureTargetInfo(CaptureTargetKind.Display, "\\\\.\\DISPLAY1", "Display 1", 1920, 1080),
        };

        return new Harness(
            coordinator,
            signaling,
            media,
            capture,
            blocked,
            audit,
            prompt,
            inputSafety,
            remoteInputSink,
            telemetry,
            securityAudit);
    }

    private static LifecycleHarness BuildLifecycle(ControlledSignalingClient signaling)
    {
        var media = new FakeMediaEngine();
        var capture = new FakeCaptureSource();
        var audit = new InMemoryAuditLog();
        var coordinator = new SessionCoordinator(
            signaling,
            media,
            new ScriptedPermissionPrompt(PermissionDecision.Accept),
            new InMemoryBlockedDeviceStore(),
            audit,
            () => capture,
            inputSafetyController: new FakeInputSafetyController(),
            collaborationTransportFactory: (session, permissions, _) =>
                new FakeSecureCollaborationTransport(session, permissions))
        {
            PreferredCaptureTarget = new CaptureTargetInfo(
                CaptureTargetKind.Display,
                "\\\\.\\DISPLAY1",
                "Display 1",
                1920,
                1080),
        };

        return new LifecycleHarness(coordinator, signaling, media, capture, audit);
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    private sealed class RecordingSecurityAudit : ISecurityAuditLog
    {
        public List<SecurityAuditEvent> Events { get; } = [];

        public Task AppendAsync(
            SecurityAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SecurityAuditEvent>> ReadRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SecurityAuditEvent>>(Events.TakeLast(limit).ToArray());

        public Task<AuditIntegrityResult> VerifyIntegrityAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditIntegrityResult(true, Events.Count, null));

        public Task ExportSanitizedJsonLinesAsync(
            string destinationFile,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApplyRetentionAsync(
            TimeSpan retention,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(bool confirmed, CancellationToken cancellationToken = default)
        {
            if (!confirmed) throw new InvalidOperationException("Confirmation is required.");
            Events.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class CoordinatorProfileStore : ICollaborationProfileStore
    {
        public CollaborationProfileSnapshot Snapshot { get; private set; } = new();

        public Task<CollaborationProfileSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Snapshot);

        public Task SaveAsync(
            CollaborationProfileSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            Snapshot = snapshot;
            return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------ viewer

    [Fact]
    public async Task Viewer_request_moves_to_AwaitingPermission_and_sends_one_request()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);

        Assert.Equal(SessionState.AwaitingPermission, h.Coordinator.StateOf(sessionId));
        var request = Assert.Single(h.Signaling.Requests);
        Assert.Equal(sessionId, request.Id);
        Assert.Equal(Peer, request.Target);
        Assert.Equal(SessionMode.ViewOnly, request.Mode);
        Assert.Equal(QualityProfile.Automatic, request.Quality);
    }

    [Fact]
    public async Task Viewer_request_sends_the_current_quality_profile()
    {
        var h = Build();
        await using var _ = h.Coordinator;
        h.Coordinator.Profile = MediaProfile.For(QualityProfile.Quality);

        await h.Coordinator.RequestViewOnlySessionAsync(Peer);

        Assert.Equal(QualityProfile.Quality, Assert.Single(h.Signaling.Requests).Quality);
        Assert.Equal(CaptureResolution.P2160, Assert.Single(h.Signaling.Requests).Resolution);
    }

    [Fact]
    public async Task Viewer_can_request_attended_full_control_scope()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestSessionAsync(
            Peer,
            SessionMode.FullControl,
            SessionPermission.ViewScreen | SessionPermission.ControlInput,
            SessionAccessKind.Attended);

        Assert.Equal(SessionState.AwaitingPermission, h.Coordinator.StateOf(sessionId));
        var request = Assert.Single(h.Signaling.Requests);
        Assert.Equal(SessionMode.FullControl, request.Mode);
        Assert.Equal(SessionMode.FullControl, Assert.Single(h.Coordinator.ActiveSessions).Mode);
    }

    [Fact]
    public async Task Sharer_prompts_and_accepts_attended_full_control_scope()
    {
        var h = Build(enableRemoteInput: true);
        await using var _ = h.Coordinator;

        h.Signaling.RaiseIncoming(
            SessionId.New(),
            Peer,
            "Viewer PC",
            TimeSpan.FromSeconds(30),
            mode: SessionMode.FullControl,
            permissions: SessionPermission.ViewScreen | SessionPermission.ControlInput);

        Assert.True(await UntilAsync(() => h.Capture.LastRequest is not null));
        Assert.Equal(PermissionDecision.Accept, Assert.Single(h.Signaling.Decisions).Decision);
        Assert.Equal(1, h.Prompt.Calls);
        Assert.NotNull(h.Media.Sharer);
        Assert.False(h.Capture.LastRequest!.IncludeCursor);
        var session = Assert.Single(h.Coordinator.ActiveSessions);
        Assert.Equal(SessionMode.FullControl, session.Mode);
        Assert.Equal(SessionPermission.ViewScreen | SessionPermission.ControlInput, session.Permissions);
    }

    [Fact]
    public async Task Full_control_arms_the_input_target_before_the_offer_can_reach_the_viewer()
    {
        var h = Build(enableRemoteInput: true);
        await using var _ = h.Coordinator;

        h.Signaling.RaiseIncoming(
            SessionId.New(),
            Peer,
            "Viewer PC",
            TimeSpan.FromSeconds(30),
            mode: SessionMode.FullControl,
            permissions: SessionPermission.ViewScreen | SessionPermission.ControlInput);

        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Any(s => s.Type == "offer")));
        Assert.Equal(h.Capture.Target, h.RemoteInputSink.CaptureTarget);
        Assert.NotNull(h.RemoteInputSink.CaptureTarget);
    }

    [Fact]
    public async Task Sharer_can_revoke_control_without_ending_view_and_reconnect_cannot_restore_it()
    {
        var h = Build(enableRemoteInput: true);
        await using var _ = h.Coordinator;
        var sessionId = SessionId.New();

        h.Signaling.RaiseIncoming(
            sessionId,
            Peer,
            "Viewer PC",
            TimeSpan.FromSeconds(30),
            mode: SessionMode.FullControl,
            permissions: SessionPermission.ViewScreen | SessionPermission.ControlInput);
        Assert.True(await UntilAsync(() => h.Coordinator.CollaborationFor(sessionId) is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0");
        h.Media.Sharer!.SetDataChannelReady();
        h.Media.Sharer.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() =>
            h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));

        Assert.True(await h.Coordinator.RevokeControlAsync(sessionId));
        Assert.True(h.InputSafety.DisableAndReleaseCalls >= 1);
        var permissionAudit = Assert.Single(
            h.SecurityAudit.Events,
            entry => entry.EventType == SecurityAuditEventType.PermissionChanged);
        Assert.Equal("control_revoked", permissionAudit.Outcome);
        Assert.Equal(SessionPermission.ViewScreen, permissionAudit.PermissionSet);
        Assert.True(CollaborationProtocolCodec.TryDecode(
            Assert.Single(h.Media.Sharer.SentData),
            out var revocationMessage,
            out var decodeError));
        Assert.Null(decodeError);
        Assert.IsType<RemoteInputPermissionRevoked>(revocationMessage);

        h.Media.Sharer.SetState(MediaConnectionState.Disconnected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Reconnecting));
        h.Media.Sharer.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() =>
            h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));

        Assert.Equal(SessionPermission.ViewScreen, h.InputSafety.LastRestoredPermissions);
        Assert.Equal(
            SessionPermission.ViewScreen | SessionPermission.ControlInput,
            Assert.Single(h.Coordinator.ActiveSessions).Permissions);
    }

    [Fact]
    public async Task Sharer_accepts_file_transfer_without_starting_screen_capture()
    {
        var h = Build();
        await using var _ = h.Coordinator;
        var collaboration = new TaskCompletionSource<SessionCollaborationContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        h.Coordinator.CollaborationAvailable += (_, context) => collaboration.TrySetResult(context);

        h.Signaling.RaiseIncoming(
            SessionId.New(),
            Peer,
            "Viewer PC",
            TimeSpan.FromSeconds(30),
            mode: SessionMode.FileTransferOnly,
            permissions: SessionPermission.FileTransfer);

        var context = await collaboration.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PermissionDecision.Accept, Assert.Single(h.Signaling.Decisions).Decision);
        Assert.Equal(SessionPermission.FileTransfer, h.Media.SharerPermissions);
        Assert.False(h.Media.SharerHadCapture);
        Assert.Null(h.Capture.LastRequest);
        Assert.NotNull(context.FileTransfers);
        Assert.Null(context.RemoteInput);
        Assert.Null(context.Clipboard);
        Assert.Equal(TransferPriorityMode.FileTransferPriority, context.TransferPriorityMode);
    }

    [Fact]
    public async Task Remote_selected_view_only_scope_blocks_input_and_file_transfer_before_media_starts()
    {
        var h = Build(
            enableRemoteInput: true,
            grantedMode: SessionMode.ViewOnly,
            grantedPermissions: SessionPermission.ViewScreen);
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(
            sessionId,
            Peer,
            "Viewer PC",
            TimeSpan.FromSeconds(30),
            mode: SessionMode.FullControl,
            permissions: Phase1SessionScope.FullControlPermissions,
            remoteScopeSelectionRequired: true);

        Assert.True(await UntilAsync(() => h.Coordinator.CollaborationFor(sessionId) is not null));
        var granted = Assert.Single(h.Signaling.GrantedDecisions);
        Assert.Equal(sessionId, granted.Id);
        Assert.Equal(SessionMode.ViewOnly, granted.Mode);
        Assert.Equal(SessionPermission.ViewScreen, granted.Permissions);
        Assert.Equal(SessionPermission.ViewScreen, h.Media.SharerPermissions);
        Assert.Equal(SessionPermission.ViewScreen, Assert.Single(h.Coordinator.ActiveSessions).Permissions);
        Assert.Null(h.Coordinator.CollaborationFor(sessionId)?.RemoteInput);
        Assert.Null(h.Coordinator.CollaborationFor(sessionId)?.FileTransfers);
    }

    [Fact]
    public async Task Requester_fails_closed_when_remote_selected_scope_is_not_confirmed()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestRemoteSelectedSessionAsync(Peer);
        Assert.True(Assert.Single(h.Signaling.Requests).RemoteScopeSelectionRequired);

        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept, "Sharer PC");

        Assert.True(await UntilAsync(() => h.Coordinator.ActiveSessions.Count == 0));
        Assert.Null(h.Media.Viewer);
    }

    [Fact]
    public async Task Phase4_custom_scope_creates_only_the_explicitly_approved_collaboration_services()
    {
        var h = Build(enableRemoteInput: true, enableClipboard: true);
        await using var _ = h.Coordinator;
        var permissions = SessionPermission.ViewScreen |
                          SessionPermission.ControlInput |
                          SessionPermission.FileTransfer |
                          SessionPermission.ClipboardText;
        var sessionId = await h.Coordinator.RequestSessionAsync(
            Peer,
            SessionMode.Custom,
            permissions,
            SessionAccessKind.Attended);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept, "Sharer PC");

        Assert.True(await UntilAsync(() => h.Coordinator.CollaborationFor(sessionId) is not null));
        var context = h.Coordinator.CollaborationFor(sessionId)!;
        Assert.Equal(permissions, context.Permissions);
        Assert.NotNull(context.RemoteInput);
        Assert.NotNull(context.FileTransfers);
        Assert.NotNull(context.Clipboard);
        Assert.Equal(permissions, h.Media.ViewerPermissions);
    }

    [Fact]
    public async Task Unattended_view_request_uses_a_challenge_proof_and_never_sends_the_password()
    {
        var h = Build();
        await using var _ = h.Coordinator;
        const string password = "Correct-Horse-9!Battery"; // secret-scan: allow-test-vector
        h.Signaling.NextUnattendedChallenge = new UnattendedChallenge(
            "request-1",
            true,
            Guid.NewGuid(),
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            600_000,
            DateTimeOffset.UtcNow.AddSeconds(30),
            "ok");

        await h.Coordinator.RequestSessionAsync(
            Peer,
            SessionMode.ViewOnly,
            SessionPermission.ViewScreen,
            SessionAccessKind.Unattended,
            password);

        var request = Assert.Single(h.Signaling.Requests);
        Assert.Equal(SessionAccessKind.Unattended, request.AccessKind);
        Assert.Equal(h.Signaling.NextUnattendedChallenge.ChallengeId, request.ChallengeId);
        Assert.False(string.IsNullOrWhiteSpace(request.Proof));
        Assert.DoesNotContain(password, request.Proof, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unattended_request_reports_the_remote_allowed_mode_before_sending_a_session()
    {
        var h = Build(enableRemoteInput: true);
        await using var _ = h.Coordinator;
        const string password = "Correct-Horse-9!Battery"; // secret-scan: allow-test-vector
        h.Signaling.NextUnattendedChallenge = new UnattendedChallenge(
            "request-mode",
            true,
            Guid.NewGuid(),
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            600_000,
            DateTimeOffset.UtcNow.AddSeconds(30),
            "ok",
            SessionPermission.ViewScreen);

        var exception = await Assert.ThrowsAsync<UnattendedPermissionMismatchException>(() =>
            h.Coordinator.RequestSessionAsync(
                Peer,
                SessionMode.FullControl,
                SessionPermissionPolicy.ForMode(SessionMode.FullControl),
                SessionAccessKind.Unattended,
                password));

        Assert.Equal(SessionPermission.ViewScreen, exception.AllowedPermissions);
        Assert.Empty(h.Signaling.Requests);
    }

    [Fact]
    public async Task Valid_unattended_proof_auto_accepts_without_showing_the_attended_prompt()
    {
        const string password = "Correct-Horse-9!Battery"; // secret-scan: allow-test-vector
        var store = new CoordinatorProfileStore();
        var trust = new TrustedDeviceService(store);
        var unattended = new UnattendedAccessService(store, trust);
        await unattended.EnableAsync(new UnattendedSetupRequest
        {
            LocalUserConfirmed = true,
            ExplanationAcknowledged = true,
            StrongPassword = password,
            AllowedPermissions = SessionPermission.ViewScreen,
        });
        var fingerprint = new string('b', 64);
        var challenge = await unattended.IssuePasswordChallengeAsync(new UnattendedChallengeRequestNotification(
            "request-2",
            Peer,
            fingerprint,
            DateTimeOffset.UtcNow.AddSeconds(30)));
        var proof = UnattendedAccessService.CreatePasswordProof(
            password,
            challenge,
            Peer,
            fingerprint,
            SessionPermission.ViewScreen);
        var h = Build(unattendedAccess: unattended);
        await using var _ = h.Coordinator;

        h.Signaling.RaiseIncoming(
            SessionId.New(),
            Peer,
            "Viewer PC",
            TimeSpan.FromSeconds(30),
            mode: SessionMode.ViewOnly,
            permissions: SessionPermission.ViewScreen,
            accessKind: SessionAccessKind.Unattended,
            fromKeyFingerprint: fingerprint,
            unattendedChallengeId: challenge.ChallengeId,
            unattendedProof: proof);

        Assert.True(await UntilAsync(() => h.Signaling.Decisions.Count == 1));
        Assert.Equal(PermissionDecision.Accept, h.Signaling.Decisions[0].Decision);
        Assert.Equal(0, h.Prompt.Calls);
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
    }

    [Fact]
    public async Task Viewer_completes_the_flow_to_ConnectedViewOnly()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept, "Sharer PC");

        Assert.True(await UntilAsync(() => h.Media.Viewer is not null));
        Assert.Equal(SessionState.Negotiating, h.Coordinator.StateOf(sessionId));

        h.Signaling.RaiseSdp(sessionId, "offer", "v=0 sharer offer");
        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Any(s => s.Type == "answer")));
        Assert.Equal(SessionState.Connecting, h.Coordinator.StateOf(sessionId));

        h.Media.Viewer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));

        var info = Assert.Single(h.Coordinator.ActiveSessions);
        Assert.Equal(SessionRole.Viewer, info.Role);
        Assert.Equal("Sharer PC", info.PeerDisplayName);
        Assert.Equal("483-***-***-204", info.PeerMaskedId);
        Assert.Equal(SessionMode.ViewOnly, info.Mode);
    }

    [Fact]
    public async Task Viewer_processes_offer_and_ice_that_arrive_before_media_is_ready()
    {
        var h = Build();
        await using var _ = h.Coordinator;
        var iceRequestEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIceRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        h.Signaling.OnGetIceConfigurationAsync = async cancellationToken =>
        {
            iceRequestEntered.TrySetResult();
            await releaseIceRequest.Task.WaitAsync(cancellationToken);
            return IceConfiguration.DirectOnly;
        };

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept, "Sharer PC");
        await iceRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        h.Signaling.RaiseSdp(sessionId, "offer", "v=0 early sharer offer");
        h.Signaling.RaiseIce(sessionId, "candidate:early 1 udp");
        Assert.Null(h.Media.Viewer);

        releaseIceRequest.TrySetResult();

        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Any(s => s.Type == "answer")));
        Assert.True(await UntilAsync(() => h.Media.Viewer?.AddedCandidates.Count == 1));
        Assert.Equal("candidate:early 1 udp", Assert.Single(h.Media.Viewer!.AddedCandidates));
        Assert.Equal(SessionState.Connecting, h.Coordinator.StateOf(sessionId));
    }

    [Fact]
    public async Task Initial_media_connected_does_not_disable_an_already_ready_full_control_channel()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestSessionAsync(
            Peer,
            SessionMode.FullControl,
            SessionPermission.ViewScreen | SessionPermission.ControlInput);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept, "Sharer PC");

        Assert.True(await UntilAsync(() =>
            h.Media.Viewer is not null && h.Coordinator.CollaborationFor(sessionId) is not null));
        h.Signaling.RaiseSdp(sessionId, "offer", "v=0 sharer offer");
        Assert.True(await UntilAsync(() =>
            h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        var collaboration = h.Coordinator.CollaborationFor(sessionId);
        var remoteInput = Assert.IsType<RemoteInputSession>(collaboration?.RemoteInput);
        h.Media.Viewer!.SetDataChannelReady();
        var enableInput = remoteInput.SetLocalCaptureEnabledAsync(true);
        Assert.True(await UntilAsync(() => h.Media.Viewer.SentData.Count == 1));
        Assert.True(CollaborationProtocolCodec.TryDecode(
            h.Media.Viewer.SentData[0],
            out var focusMessage,
            out var decodeError));
        Assert.Null(decodeError);
        var focus = Assert.IsType<RemoteInputFocusRequest>(focusMessage);
        h.Media.Viewer.EmitData(CollaborationProtocolCodec.Encode(new RemoteInputFocusResult
        {
            InputVersion = RemoteInputSession.CurrentInputProtocolVersion,
            SessionId = sessionId.Value,
            PermissionGeneration = focus.PermissionGeneration,
            SessionGeneration = focus.SessionGeneration,
            FocusGeneration = focus.FocusGeneration,
            Sequence = 1,
            Accepted = true,
            ReasonCode = "accepted",
        }));
        await enableInput;
        Assert.True(remoteInput.IsLocallyEnabled);

        h.Media.Viewer.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() =>
            h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));

        Assert.True(remoteInput.IsLocallyEnabled);
    }

    [Fact]
    public async Task Client_telemetry_uses_started_actual_media_connected_and_one_terminal_event()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);
        Assert.Equal(ClientSessionEventKind.Started, Assert.Single(h.Telemetry.Events).Kind);

        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept, "Sharer PC");
        Assert.True(await UntilAsync(() => h.Media.Viewer is not null));
        h.Signaling.RaiseSdp(sessionId, "offer", "v=0 sharer offer");
        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Any(s => s.Type == "answer")));
        Assert.DoesNotContain(h.Telemetry.Events, item => item.Kind == ClientSessionEventKind.Connected);

        h.Media.Viewer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Telemetry.Events.Any(item => item.Kind == ClientSessionEventKind.Connected)));
        await h.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedByViewer);
        Assert.True(await UntilAsync(() => h.Telemetry.Events.Any(item => item.Kind == ClientSessionEventKind.Ended)));

        Assert.Equal(3, h.Telemetry.Events.Count);
        Assert.Equal(1, h.Telemetry.Events.Count(item => item.Kind == ClientSessionEventKind.Ended));
    }

    [Fact]
    public async Task Viewer_diagnostics_use_address_free_sharer_capture_metrics()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept);
        Assert.True(await UntilAsync(() => h.Media.Viewer is not null));

        h.Signaling.RaiseQuality(new SessionQualityNotification(
            sessionId,
            CaptureFps: 27.5,
            EncodeFps: 26.75,
            PacketLossPercent: 1.25,
            JitterMs: 8.5,
            AvailableOutgoingBitrateKbps: 4_200,
            FramesDropped: 3,
            SourceWidth: 2560,
            SourceHeight: 1440,
            RequestedWidth: 1920,
            RequestedHeight: 1080,
            EncodedWidth: 1280,
            EncodedHeight: 720,
            EncoderName: "SoftwareVp8",
            EncoderHardwareAccelerated: false));

        var statistics = h.Coordinator.StatisticsOf(sessionId);
        Assert.Equal(27.5, statistics.CaptureFps);
        Assert.Equal(26.75, statistics.EncodeFps);
        Assert.Equal(1.25, statistics.PacketLossPercent);
        Assert.Equal(8.5, statistics.JitterMs);
        Assert.Equal(4_200, statistics.AvailableOutgoingBitrateKbps);
        Assert.Equal(3, statistics.FramesDropped);
        Assert.Equal(2560, statistics.SourceWidth);
        Assert.Equal(1440, statistics.SourceHeight);
        Assert.Equal(1920, statistics.RequestedWidth);
        Assert.Equal(1080, statistics.RequestedHeight);
        Assert.Equal(1280, statistics.EncodedWidth);
        Assert.Equal(720, statistics.EncodedHeight);
        Assert.Equal("SoftwareVp8", statistics.EncoderName);
        Assert.False(statistics.EncoderHardwareAccelerated);
    }

    [Fact]
    public async Task Viewer_quality_feedback_is_forwarded_only_to_the_active_sharer_media()
    {
        var h = Build();
        await using var _ = h.Coordinator;
        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));

        h.Signaling.RaiseQuality(new SessionQualityNotification(
            sessionId,
            CaptureFps: 0,
            EncodeFps: 0,
            PacketLossPercent: 0,
            JitterMs: 0,
            AvailableOutgoingBitrateKbps: 0,
            FramesDropped: 0,
            DecodeFps: 30,
            RenderFps: 12,
            DecodeToRenderLatencyP95Ms: 80,
            CaptureToPresentLatencyP95Ms: 140,
            FrameAgeClockUncertaintyMs: 2,
            InputToInjectionLatencyP95Ms: 60,
            InputClockUncertaintyMs: 3));

        var feedback = Assert.Single(h.Media.Sharer!.RemoteQualityFeedback);
        Assert.Equal(30, feedback.DecodeFps);
        Assert.Equal(12, feedback.RenderFps);
        Assert.Equal(80, feedback.DecodeToRenderLatencyP95Ms);
        Assert.Equal(140, feedback.CaptureToPresentLatencyP95Ms);
        Assert.Equal(2, feedback.FrameAgeClockUncertaintyMs);
        Assert.Equal(60, feedback.InputToInjectionLatencyP95Ms);
        Assert.Equal(3, feedback.InputClockUncertaintyMs);
    }

    [Fact]
    public async Task Viewer_publishes_local_input_pressure_on_the_existing_quality_channel()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), SessionCoordinator.QualityPublishInterval);
        Assert.Equal(
            TimeSpan.FromMilliseconds(250),
            SessionCoordinator.GetQualityPublishInterval(activeSessionCount: 5));
        Assert.Equal(
            TimeSpan.FromMilliseconds(300),
            SessionCoordinator.GetQualityPublishInterval(activeSessionCount: 6));
        Assert.Equal(
            TimeSpan.FromMilliseconds(3200),
            SessionCoordinator.GetQualityPublishInterval(activeSessionCount: 64));
        var h = Build();
        await using var _ = h.Coordinator;
        h.Signaling.QualityFailuresRemaining = 1;
        var sessionId = await h.Coordinator.RequestSessionAsync(
            Peer,
            SessionMode.FullControl,
            SessionPermission.ViewScreen | SessionPermission.ControlInput,
            SessionAccessKind.Attended);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept);
        Assert.True(await UntilAsync(() => h.Media.Viewer is not null));
        h.Signaling.RaiseSdp(sessionId, "offer", "v=0 sharer offer");
        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Any(sdp => sdp.Type == "answer")));
        h.Media.Viewer!.SetState(MediaConnectionState.Connected);
        h.Media.Viewer!.Statistics = new MediaStatistics
        {
            InputToInjectionLatencyP95Ms = 58,
            InputClockUncertaintyMs = 2,
        };

        Assert.True(await UntilAsync(() => h.Signaling.QualitySendAttempts >= 2));
        Assert.Single(h.Signaling.PublishedQuality);
        var quality = h.Signaling.PublishedQuality[^1];
        Assert.Equal(58, quality.InputToInjectionLatencyP95Ms);
        Assert.Equal(2, quality.InputClockUncertaintyMs);
    }

    [Fact]
    public async Task Session_timeline_contains_only_real_events_and_survives_local_release()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);
        var requested = h.Coordinator.TimelineOf(sessionId);
        Assert.Contains(requested, item => item.Code == "session.request.created");
        Assert.Contains(requested, item => item.Code == "session.device.online");

        h.Coordinator.ReportFrameRendered(sessionId);
        h.Coordinator.ReportFrameRendered(sessionId);
        Assert.Single(h.Coordinator.TimelineOf(sessionId), item => item.Code == "media.first-frame.rendered");

        await h.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedByViewer);
        var completed = h.Coordinator.TimelineOf(sessionId);
        Assert.Contains(completed, item => item.Code == "session.ended");
        Assert.DoesNotContain(completed, item => item.Description.Contains(Peer.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Viewer_declined_ends_the_session_and_releases_everything()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Decline);

        Assert.True(await UntilAsync(() => h.Coordinator.ActiveSessions.Count == 0));
        var entry = Assert.Single(h.Audit.Entries);
        Assert.Equal(SessionEndReason.PermissionDeclined, entry.EndReason);
    }

    [Fact]
    public async Task Viewer_permission_timeout_ends_the_session()
    {
        var h = Build(options: SessionOptions.Default with { PermissionTimeout = TimeSpan.FromMilliseconds(150) });
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);

        Assert.True(await UntilAsync(() => h.Signaling.Ends.Any(e => e.Reason == SessionEndReason.PermissionTimeout)));
        Assert.Empty(h.Coordinator.ActiveSessions);
    }

    [Fact]
    public async Task Viewer_negotiation_timeout_ends_the_session()
    {
        var h = Build(options: SessionOptions.Default with { NegotiationTimeout = TimeSpan.FromMilliseconds(150) });
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept);

        // The sharer never sends an offer.
        Assert.True(await UntilAsync(() => h.Signaling.Ends.Any(e => e.Reason == SessionEndReason.NegotiationTimeout)));
        Assert.Empty(h.Coordinator.ActiveSessions);
    }

    [Fact]
    public async Task Viewer_connect_timeout_ends_the_session()
    {
        var h = Build(options: SessionOptions.Default with { ConnectTimeout = TimeSpan.FromMilliseconds(150) });
        await using var _ = h.Coordinator;

        var sessionId = await h.Coordinator.RequestViewOnlySessionAsync(Peer);
        h.Signaling.RaisePermission(sessionId, PermissionDecision.Accept);
        Assert.True(await UntilAsync(() => h.Media.Viewer is not null));
        h.Signaling.RaiseSdp(sessionId, "offer", "v=0");

        // Media never reaches Connected.
        Assert.True(await UntilAsync(() => h.Signaling.Ends.Any(e => e.Reason == SessionEndReason.ConnectTimeout)));
    }

    [Fact]
    public async Task Signaling_failure_during_request_fails_the_session()
    {
        var h = Build();
        await using var _ = h.Coordinator;
        h.Signaling.FailNextRequest = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.RequestViewOnlySessionAsync(Peer));

        Assert.Empty(h.Coordinator.ActiveSessions);
    }

    // ------------------------------------------------------------------ sharer

    [Fact]
    public async Task Sharer_capture_start_failure_notifies_peer_without_waiting_for_negotiation_timeout()
    {
        var h = Build();
        await using var _ = h.Coordinator;
        h.Capture.StartException = new InvalidOperationException("capture unavailable");
        var sessionId = SessionId.New();

        h.Signaling.RaiseIncoming(
            sessionId,
            Peer,
            "Peer",
            TimeSpan.FromSeconds(30),
            mode: SessionMode.ViewOnly,
            permissions: SessionPermission.ViewScreen,
            accessKind: SessionAccessKind.Attended);

        Assert.True(await UntilAsync(() =>
            h.Signaling.Ends.Any(end => end.Id == sessionId && end.Reason == SessionEndReason.ProtocolError)));
        Assert.Empty(h.Coordinator.ActiveSessions);
    }

    [Fact]
    public async Task Incoming_request_shows_the_prompt_with_a_masked_id()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));

        Assert.True(await UntilAsync(() => h.Prompt.Calls == 1));
        Assert.Equal("483-***-***-204", h.Prompt.LastRequest!.MaskedRequesterId);
        Assert.Equal("Viewer PC", h.Prompt.LastRequest.RequesterDisplayName);
        Assert.Equal(SessionMode.ViewOnly, h.Prompt.LastRequest.RequestedMode);
    }

    [Fact]
    public async Task Accepting_starts_capture_and_sends_an_offer()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));

        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Any(s => s.Type == "offer")));
        Assert.True(h.Capture.IsCapturing);
        Assert.True(h.Capture.LastRequest!.IncludeCursor);
        Assert.Equal("Display 1", h.Capture.Target!.DisplayName);
        Assert.Equal((sessionId, PermissionDecision.Accept), h.Signaling.Decisions.Single());

        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 viewer answer");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));

        h.Media.Sharer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
    }

    [Fact]
    public async Task Sharer_subscribes_to_capture_before_the_initial_frame_is_emitted()
    {
        var h = Build();
        await using var _ = h.Coordinator;
        h.Capture.EmitFrameOnStart = true;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));

        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Any(s => s.Type == "offer")));
        Assert.Equal(1, h.Media.SharerFramesObserved);
    }

    [Fact]
    public async Task Sharer_snapshots_the_requested_built_in_profile()
    {
        var h = Build(promptDelay: TimeSpan.FromMilliseconds(100));
        await using var _ = h.Coordinator;
        h.Coordinator.Profile = MediaProfile.For(QualityProfile.Performance);

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(
            sessionId,
            Peer,
            "Viewer PC",
            TimeSpan.FromSeconds(30),
            QualityProfile.Quality,
            resolution: CaptureResolution.P1440);
        h.Coordinator.Profile = MediaProfile.For(QualityProfile.Balanced);

        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Any(s => s.Type == "offer")));
        Assert.Equal(QualityProfile.Quality, h.Media.SharerProfile!.Quality);
        Assert.Equal(CaptureResolution.P1440, h.Capture.LastRequest!.Resolution);
        Assert.Equal(30, h.Capture.LastRequest.MaxFramesPerSecond);
        Assert.Equal(36_000, h.Media.SharerProfile.MaxBitrateKbps);
    }

    [Fact]
    public async Task Declining_never_starts_capture()
    {
        var h = Build(PermissionDecision.Decline);
        await using var _ = h.Coordinator;

        h.Signaling.RaiseIncoming(SessionId.New(), Peer, "Viewer PC", TimeSpan.FromSeconds(30));

        Assert.True(await UntilAsync(() => h.Signaling.Decisions.Count == 1));
        Assert.Equal(PermissionDecision.Decline, h.Signaling.Decisions[0].Decision);
        Assert.False(h.Capture.IsCapturing);
        Assert.Null(h.Media.Sharer);
    }

    [Fact]
    public async Task No_answer_within_the_window_is_treated_as_a_decline()
    {
        var h = Build(
            PermissionDecision.Accept,
            promptDelay: TimeSpan.FromSeconds(30),
            options: SessionOptions.Default with { PermissionTimeout = TimeSpan.FromMilliseconds(200) });
        await using var _ = h.Coordinator;

        h.Signaling.RaiseIncoming(SessionId.New(), Peer, "Viewer PC", TimeSpan.FromSeconds(30));

        Assert.True(await UntilAsync(() => h.Signaling.Decisions.Count == 1));
        Assert.Equal(PermissionDecision.Timeout, h.Signaling.Decisions[0].Decision);
        Assert.False(h.Capture.IsCapturing);
    }

    [Fact]
    public async Task Block_records_the_peer_and_auto_declines_next_time()
    {
        var h = Build(PermissionDecision.Block);
        await using var _ = h.Coordinator;

        h.Signaling.RaiseIncoming(SessionId.New(), Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Signaling.Decisions.Count == 1));
        Assert.True(await h.Blocked.IsBlockedAsync(Peer));

        h.Signaling.RaiseIncoming(SessionId.New(), Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Signaling.Decisions.Count == 2));
        Assert.Equal(PermissionDecision.Decline, h.Signaling.Decisions[1].Decision);

        // The prompt was only shown for the first request.
        Assert.Equal(1, h.Prompt.Calls);
    }

    // ------------------------------------------------------------------ lifecycle

    [Fact]
    public async Task Ending_a_session_stops_capture_and_media_and_writes_an_audit_entry()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));

        await h.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedBySharer);

        Assert.True(h.Capture.Stopped);
        Assert.True(h.Capture.Disposed);
        Assert.True(h.Media.Sharer!.Closed);
        Assert.True(h.Media.Sharer.Disposed);
        Assert.Empty(h.Coordinator.ActiveSessions);
        Assert.Contains(h.Audit.Entries, e => e.EndReason == SessionEndReason.EndedBySharer);
        Assert.Contains(h.Signaling.Ends, e => e.Reason == SessionEndReason.EndedBySharer);
    }

    [Fact]
    public async Task Peer_ending_the_session_releases_local_resources()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));

        h.Signaling.RaiseEnded(sessionId, SessionEndReason.EndedByViewer);

        Assert.True(await UntilAsync(() => h.Coordinator.ActiveSessions.Count == 0));
        Assert.True(h.Capture.Stopped);
        Assert.True(h.Media.Sharer!.Closed);
    }

    [Fact]
    public async Task Late_connected_callback_after_full_control_end_cannot_restore_input()
    {
        var h = Build(enableRemoteInput: true);
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(
            sessionId,
            Peer,
            "Viewer PC",
            TimeSpan.FromSeconds(30),
            mode: SessionMode.FullControl,
            permissions: SessionPermission.ViewScreen | SessionPermission.ControlInput);
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        h.Media.Sharer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.InputSafety.RestoreCalls == 1));

        await h.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedBySharer);
        var restoreCallsAfterEnd = h.InputSafety.RestoreCalls;
        Assert.True(h.InputSafety.DisableAndReleaseCalls > 0);

        h.Media.Sharer.SetState(MediaConnectionState.Connected);
        await Task.Delay(50);

        Assert.Equal(restoreCallsAfterEnd, h.InputSafety.RestoreCalls);
        Assert.Empty(h.Coordinator.ActiveSessions);
    }

    [Fact]
    public async Task Media_drop_moves_to_Reconnecting_and_recovers()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        h.Media.Sharer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));

        h.Media.Sharer.SetState(MediaConnectionState.Disconnected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Reconnecting));
        Assert.Equal(1, h.InputSafety.DisableAndReleaseCalls);

        h.Media.Sharer.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
        Assert.Equal(1, h.InputSafety.RestoreCalls);
        Assert.Equal(SessionPermission.ViewScreen, h.InputSafety.LastRestoredPermissions);
    }

    [Fact]
    public async Task Signaling_reconnect_restores_session_state_when_media_never_dropped()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        h.Media.Sharer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));

        h.Signaling.RaiseConnectionState(SignalingConnectionState.Reconnecting);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Reconnecting));
        h.Signaling.RaiseConnectionState(SignalingConnectionState.Registered);
        Assert.True(await UntilAsync(() => h.Signaling.ResumeRequests.Contains(sessionId)));
        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Count(s => s.Id == sessionId && s.Type == "offer") >= 2));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 resumed");

        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
        Assert.Equal(ReconnectState.Connected, h.Coordinator.StatisticsOf(sessionId).ReconnectState);
        Assert.Equal(1, h.InputSafety.DisableAndReleaseCalls);
        Assert.Equal(1, h.InputSafety.RestoreCalls);
    }

    [Fact]
    public async Task Media_then_signaling_drop_still_resumes_and_completes_fresh_ice_negotiation()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 initial");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        h.Media.Sharer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));

        h.Media.Sharer.SetState(MediaConnectionState.Disconnected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Reconnecting));
        h.Signaling.RaiseConnectionState(SignalingConnectionState.Reconnecting);
        h.Signaling.RaiseConnectionState(SignalingConnectionState.Registered);

        Assert.True(await UntilAsync(() => h.Signaling.ResumeRequests.Contains(sessionId)));
        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Count(s => s.Id == sessionId && s.Type == "offer") >= 2));

        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 resumed");

        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
        Assert.Equal(ReconnectState.Connected, h.Coordinator.StatisticsOf(sessionId).ReconnectState);
    }

    [Fact]
    public async Task Transient_first_ice_offer_send_is_retried_during_wifi_recovery()
    {
        var h = Build(options: SessionOptions.Default with
        {
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(25),
            ReconnectJitterFraction = 0,
        });
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 initial");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        h.Media.Sharer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
        var initialOffers = h.Signaling.Sdps.Count(s => s.Id == sessionId && s.Type == "offer");

        h.Signaling.FailNextSdpSends(1);
        h.Media.Sharer.SetState(MediaConnectionState.Disconnected);

        Assert.True(await UntilAsync(() =>
            h.Signaling.Sdps.Count(s => s.Id == sessionId && s.Type == "offer") > initialOffers));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 retried");

        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
        Assert.Equal(ReconnectState.Connected, h.Coordinator.StatisticsOf(sessionId).ReconnectState);
        Assert.Empty(h.Signaling.Ends);
    }

    [Fact]
    public async Task Fresh_ice_recovery_reports_direct_to_relay_and_relay_to_direct_path_changes()
    {
        var h = Build(options: SessionOptions.Default with
        {
            ReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(25),
            ReconnectJitterFraction = 0,
        });
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 initial");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        h.Media.Sharer!.Statistics = h.Media.Sharer.Statistics with
        {
            ConnectionPath = ConnectionPath.DirectInternet,
        };
        h.Media.Sharer.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
        Assert.Equal(ConnectionPath.DirectInternet, h.Coordinator.StatisticsOf(sessionId).ConnectionPath);

        h.Media.Sharer.SetState(MediaConnectionState.Disconnected);
        Assert.True(await UntilAsync(() => h.Media.Sharer.IceRestarts >= 1));
        h.Media.Sharer.Statistics = h.Media.Sharer.Statistics with
        {
            ConnectionPath = ConnectionPath.Relayed,
        };
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 relayed");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
        Assert.Equal(ConnectionPath.Relayed, h.Coordinator.StatisticsOf(sessionId).ConnectionPath);

        h.Media.Sharer.SetState(MediaConnectionState.Disconnected);
        Assert.True(await UntilAsync(() => h.Media.Sharer.IceRestarts >= 2));
        h.Media.Sharer.Statistics = h.Media.Sharer.Statistics with
        {
            ConnectionPath = ConnectionPath.DirectInternet,
        };
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 direct-again");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
        Assert.Equal(ConnectionPath.DirectInternet, h.Coordinator.StatisticsOf(sessionId).ConnectionPath);
    }

    [Fact]
    public async Task Viewer_resume_notification_makes_the_online_sharer_restart_ice()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 initial");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        h.Media.Sharer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
        var initialOffers = h.Signaling.Sdps.Count(s => s.Id == sessionId && s.Type == "offer");

        h.Signaling.RaisePeerResumed(sessionId);

        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Reconnecting));
        Assert.True(await UntilAsync(() =>
            h.Signaling.Sdps.Count(s => s.Id == sessionId && s.Type == "offer") > initialOffers));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0 peer-resumed");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));
    }

    [Fact]
    public async Task Permanent_signaling_identity_mismatch_ends_reconnect_immediately()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        h.Media.Sharer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));

        h.Signaling.RaiseConnectionState(SignalingConnectionState.Reconnecting);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Reconnecting));
        h.Signaling.RaiseError("invalid_proof", "identity pin mismatch");

        Assert.True(await UntilAsync(() => h.Coordinator.ActiveSessions.Count == 0));
        Assert.Contains(h.Audit.Entries, entry => entry.EndReason == SessionEndReason.AuthenticationMismatch);
        Assert.Equal(0, h.InputSafety.RestoreCalls);
    }

    [Fact]
    public async Task Reconnect_timeout_ends_the_session()
    {
        var h = Build(options: SessionOptions.Default with { ReconnectTimeout = TimeSpan.FromMilliseconds(150) });
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));
        h.Signaling.RaiseSdp(sessionId, "answer", "v=0");
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.Connecting));
        h.Media.Sharer!.SetState(MediaConnectionState.Connected);
        Assert.True(await UntilAsync(() => h.Coordinator.StateOf(sessionId) == SessionState.ConnectedViewOnly));

        h.Media.Sharer.SetState(MediaConnectionState.Disconnected);

        Assert.True(await UntilAsync(() => h.Coordinator.ActiveSessions.Count == 0));
        Assert.Contains(h.Audit.Entries, e => e.EndReason == SessionEndReason.ReconnectFailed);
    }

    [Fact]
    public async Task Local_ice_candidates_are_forwarded_and_remote_ones_are_applied()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));

        h.Media.Sharer!.EmitIce("candidate:local 1 udp");
        Assert.True(await UntilAsync(() => h.Signaling.IceCandidates.Count == 1));

        h.Signaling.RaiseIce(sessionId, "candidate:remote 1 udp");
        Assert.True(await UntilAsync(() => h.Media.Sharer.AddedCandidates.Count == 1));
    }

    [Fact]
    public async Task Application_shutdown_releases_every_active_session()
    {
        var h = Build();

        h.Signaling.RaiseIncoming(SessionId.New(), Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Media.Sharer is not null));

        await h.Coordinator.DisposeAsync();

        Assert.True(h.Capture.Stopped);
        Assert.True(h.Media.Sharer!.Closed);
        Assert.Contains(h.Audit.Entries, e => e.EndReason == SessionEndReason.ApplicationShutdown);
        Assert.Contains(h.Signaling.Ends, e => e.Reason == SessionEndReason.ApplicationShutdown);
    }

    [Fact]
    public async Task Concurrent_application_shutdown_is_idempotent()
    {
        var releaseNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var signaling = new ControlledSignalingClient
        {
            OnApplicationShutdownAsync = _ => releaseNotification.Task,
        };
        var h = BuildLifecycle(signaling);
        var closedEvents = 0;
        h.Coordinator.SessionClosed += (_, _) => Interlocked.Increment(ref closedEvents);
        Task? firstDispose = null;
        Task? secondDispose = null;

        try
        {
            signaling.RaiseIncoming(SessionId.New());
            Assert.True(await UntilAsync(() => h.Media.Sharer is not null));

            firstDispose = h.Coordinator.DisposeAsync().AsTask();
            await signaling.ApplicationShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            secondDispose = h.Coordinator.DisposeAsync().AsTask();
            var callsBeforeRelease = signaling.ApplicationShutdownCalls;

            releaseNotification.TrySetResult();
            await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, callsBeforeRelease);
            Assert.Equal(1, signaling.ApplicationShutdownCalls);
            Assert.Equal(1, Volatile.Read(ref closedEvents));
            Assert.Single(h.Audit.Entries, entry =>
                entry.EndReason == SessionEndReason.ApplicationShutdown);
            Assert.Empty(h.Coordinator.ActiveSessions);
            Assert.True(h.Capture.Stopped);
            Assert.True(h.Capture.Disposed);
            Assert.True(h.Media.Sharer!.Closed);
            Assert.True(h.Media.Sharer.Disposed);
        }
        finally
        {
            releaseNotification.TrySetResult();
            if (firstDispose is not null) await firstDispose.WaitAsync(TimeSpan.FromSeconds(2));
            if (secondDispose is not null) await secondDispose.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Application_shutdown_does_not_wait_forever_for_peer_notification()
    {
        var releaseNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var signaling = new ControlledSignalingClient
        {
            // Models a transport call that ignores cancellation and never returns on its own.
            OnApplicationShutdownAsync = _ => releaseNotification.Task,
        };
        var h = BuildLifecycle(signaling);
        Task? dispose = null;
        var completedWithoutFallbackRelease = false;

        try
        {
            signaling.RaiseIncoming(SessionId.New());
            Assert.True(await UntilAsync(() => h.Media.Sharer is not null));

            dispose = h.Coordinator.DisposeAsync().AsTask();
            await signaling.ApplicationShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                await dispose.WaitAsync(TimeSpan.FromSeconds(5));
                completedWithoutFallbackRelease = true;
            }
            catch (TimeoutException)
            {
                // Release the deliberately stuck test double in finally, then report the
                // bounded-cleanup contract failure through the assertion below.
            }
        }
        finally
        {
            releaseNotification.TrySetResult();
            if (dispose is not null) await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(
            completedWithoutFallbackRelease,
            "A stuck best-effort peer notification must not block local shutdown cleanup.");
        Assert.Empty(h.Coordinator.ActiveSessions);
        Assert.True(h.Capture.Stopped);
        Assert.True(h.Media.Sharer!.Closed);
        Assert.Single(h.Audit.Entries, entry =>
            entry.EndReason == SessionEndReason.ApplicationShutdown);
    }

    [Fact]
    public async Task Application_shutdown_cancels_inflight_startup_before_capture_or_media_creation()
    {
        var iceEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var iceCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIce = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var signaling = new ControlledSignalingClient
        {
            OnGetIceConfigurationAsync = async cancellationToken =>
            {
                iceEntered.TrySetResult();
                try
                {
                    await releaseIce.Task.WaitAsync(cancellationToken);
                    return IceConfiguration.DirectOnly;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    iceCancelled.TrySetResult();
                    throw;
                }
            },
        };
        var h = BuildLifecycle(signaling);
        Task? dispose = null;

        try
        {
            signaling.RaiseIncoming(SessionId.New());
            await iceEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            dispose = h.Coordinator.DisposeAsync().AsTask();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseIce.TrySetResult();
            if (dispose is not null) await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAny(iceCancelled.Task, signaling.SdpSent.Task)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(
            iceCancelled.Task.IsCompletedSuccessfully,
            "Disposal must cancel and settle startup work before it can create resources.");
        Assert.False(signaling.SdpSent.Task.IsCompleted);
        Assert.Null(h.Capture.LastRequest);
        Assert.False(h.Capture.IsCapturing);
        Assert.Null(h.Media.Sharer);
        Assert.Empty(h.Coordinator.ActiveSessions);
    }

    [Fact]
    public async Task Repeated_sessions_do_not_leak_runtimes()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        for (var i = 0; i < 25; i++)
        {
            var sessionId = SessionId.New();
            h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
            var expectedStartAuditCount = (i * 2) + 1;
            Assert.True(await UntilAsync(() =>
                h.Coordinator.StateOf(sessionId) != SessionState.Idle
                && h.Audit.Entries.Count >= expectedStartAuditCount));
            await h.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedBySharer);
        }

        Assert.Empty(h.Coordinator.ActiveSessions);
        Assert.Equal(50, h.Audit.Entries.Count); // one start and one end per session
    }

    [Fact]
    public async Task Unknown_sessions_are_ignored_rather_than_crashing()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var stranger = SessionId.New();
        h.Signaling.RaisePermission(stranger, PermissionDecision.Accept);
        h.Signaling.RaiseSdp(stranger, "offer", "v=0");
        h.Signaling.RaiseIce(stranger, "candidate:x");
        h.Signaling.RaiseEnded(stranger, SessionEndReason.EndedByViewer);

        await Task.Delay(100);
        Assert.Empty(h.Coordinator.ActiveSessions);
    }

    [Fact]
    public async Task A_sharer_never_answers_its_own_offer()
    {
        var h = Build();
        await using var _ = h.Coordinator;

        var sessionId = SessionId.New();
        h.Signaling.RaiseIncoming(sessionId, Peer, "Viewer PC", TimeSpan.FromSeconds(30));
        Assert.True(await UntilAsync(() => h.Signaling.Sdps.Any(s => s.Type == "offer")));

        // An offer arriving at the sharer is ignored: only the viewer answers.
        h.Signaling.RaiseSdp(sessionId, "offer", "v=0 rogue offer");
        await Task.Delay(100);

        Assert.DoesNotContain(h.Signaling.Sdps, s => s.Type == "answer");
    }
}
