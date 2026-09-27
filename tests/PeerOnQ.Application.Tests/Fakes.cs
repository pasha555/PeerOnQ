using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Identity;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Tests;

public sealed class FakeSecureCollaborationTransport : ICollaborationTransport
{
    private readonly IMediaSession _media;

    public FakeSecureCollaborationTransport(IMediaSession media, SessionPermission permissions)
    {
        _media = media;
        Permissions = permissions;
        _media.DataMessageReceived += OnDataMessage;
    }

    public SessionPermission Permissions { get; }
    public bool IsReady => true;
    public TransferPriorityMode TransferPriorityMode { get; private set; } = TransferPriorityMode.Balanced;
    public SecureSessionInfo? Security { get; } = new()
    {
        ProtocolVersion = SecureSessionProtocol.ProtocolVersion,
        HandshakeSuite = SecureSessionProtocol.HandshakeSuite,
        IdentitySuite = SecureSessionProtocol.IdentitySuite,
        TrafficProtection = SecureSessionProtocol.TrafficProtection,
        KeyDerivation = SecureSessionProtocol.KeyDerivation,
        PeerIdentityFingerprint = new string('b', 64),
        EstablishedAt = DateTimeOffset.UtcNow,
        PeerAuthenticated = true,
        PostQuantumProtected = true,
        ConnectionPath = ConnectionPath.DirectLan,
    };
    public List<CollaborationMessage> Sent { get; } = [];
    public event EventHandler? Ready { add { } remove { } }
    public event EventHandler<CollaborationMessage>? MessageReceived;
    public event EventHandler<string>? ProtocolError { add { } remove { } }
    public void SetTransferPriorityMode(TransferPriorityMode mode) => TransferPriorityMode = mode;
    public async Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken = default)
    {
        var bound = message with
        {
            SessionId = _media.SessionId.Value,
            PermissionGeneration = 1,
        };
        Sent.Add(bound);
        var priority = bound is TransferChunk
            ? DataMessagePriority.Bulk
            : bound is RemoteInputMessage
                ? DataMessagePriority.Interactive
                : DataMessagePriority.Normal;
        await _media.SendDataAsync(CollaborationProtocolCodec.Encode(bound), cancellationToken, priority);
    }

    private void OnDataMessage(object? sender, ReadOnlyMemory<byte> payload)
    {
        if (CollaborationProtocolCodec.TryDecode(payload.Span, out var message, out _) && message is not null)
            MessageReceived?.Invoke(this, message);
    }

    public ValueTask DisposeAsync()
    {
        _media.DataMessageReceived -= OnDataMessage;
        return ValueTask.CompletedTask;
    }
}

public sealed class RejectingHybridIdentityProvider : IHybridDeviceIdentityProvider
{
    public Task<HybridIdentityPublic> GetPublicIdentityAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<HybridIdentityPublic>(new PlatformNotSupportedException("test provider rejected"));

    public Task<HybridIdentitySignature> SignTranscriptAsync(
        ReadOnlyMemory<byte> transcriptHash,
        ReadOnlyMemory<byte> context,
        CancellationToken cancellationToken = default) =>
        Task.FromException<HybridIdentitySignature>(new PlatformNotSupportedException("test provider rejected"));
}

public sealed class TestHybridIdentity : IDisposable
{
    private TestHybridIdentity(HybridDeviceIdentityService service, string legacyFingerprint)
    {
        Service = service;
        LegacyFingerprint = legacyFingerprint;
    }

    public HybridDeviceIdentityService Service { get; }
    public string LegacyFingerprint { get; }

    public static async Task<TestHybridIdentity> CreateAsync(string displayName)
    {
        using var legacyKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = legacyKey.ExportSubjectPublicKeyInfo();
        var store = new InMemoryDeviceSecretStore();
        await store.SetAsync(
            DeviceProvisioningService.SigningKeyName,
            legacyKey.ExportPkcs8PrivateKey());
        var identity = DeviceIdentity.Create(displayName) with
        {
            PublicKey = Convert.ToBase64String(publicKey),
        };
        var service = new HybridDeviceIdentityService(store, identity);
        await service.GetPublicIdentityAsync();
        return new TestHybridIdentity(
            service,
            Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant());
    }

    public void Dispose() => Service.Dispose();

    private sealed class InMemoryDeviceSecretStore : IDeviceSecretStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        public Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(name, out var value) ? value.ToArray() : null);
        public Task SetAsync(string name, byte[] secret, CancellationToken cancellationToken = default)
        {
            _values[name] = secret.ToArray();
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
        {
            _values.Remove(name);
            return Task.CompletedTask;
        }
    }
}

public sealed class RecordingClientSessionEventSink : IClientSessionEventSink
{
    public ConcurrentQueue<ClientSessionTelemetryEvent> Events { get; } = new();

    public ValueTask EnqueueAsync(
        ClientSessionTelemetryEvent telemetryEvent,
        CancellationToken cancellationToken = default)
    {
        Events.Enqueue(telemetryEvent);
        return ValueTask.CompletedTask;
    }
}

/// <summary>In-memory signaling that lets a test drive both ends of a session.</summary>
public sealed class FakeSignalingClient :
    ISignalingClient,
    ISessionQualitySignaling,
    IResumableSignalingClient,
    IUnattendedSignalingClient
{
    private int _sdpFailuresRemaining;
    private int _qualityFailuresRemaining;
    private int _qualitySendAttempts;

    public SignalingConnectionState State { get; private set; } = SignalingConnectionState.Registered;
    public PeerOnQId? RegisteredId { get; private set; } = PeerOnQId.Parse("LNK-100-200-300-400");

    public List<(SessionId Id, PeerOnQId Target, SessionMode Mode, QualityProfile Quality, CaptureResolution Resolution,
        SessionPermission Permissions, SessionAccessKind AccessKind, Guid? ChallengeId, string? Proof,
        bool RemoteScopeSelectionRequired)> Requests
    { get; } = [];
    public List<(SessionId Id, PermissionDecision Decision)> Decisions { get; } = [];
    public List<(SessionId Id, SessionMode Mode, SessionPermission Permissions)> GrantedDecisions { get; } = [];
    public List<(SessionId Id, string Type, string Sdp)> Sdps { get; } = [];
    public List<(SessionId Id, string Candidate)> IceCandidates { get; } = [];
    public List<(SessionId Id, SessionEndReason Reason)> Ends { get; } = [];
    public List<SessionQualityNotification> PublishedQuality { get; } = [];
    public int QualitySendAttempts => Volatile.Read(ref _qualitySendAttempts);
    public int QualityFailuresRemaining
    {
        get => Volatile.Read(ref _qualityFailuresRemaining);
        set => Volatile.Write(ref _qualityFailuresRemaining, Math.Max(0, value));
    }
    public List<SessionId> ResumeRequests { get; } = [];

    public bool FailNextRequest { get; set; }
    public Func<CancellationToken, Task<IceConfiguration>>? OnGetIceConfigurationAsync { get; set; }
    public string? LocalKeyFingerprint { get; set; } = new string('a', 64);
    public UnattendedChallenge? NextUnattendedChallenge { get; set; }

    public event EventHandler<SignalingConnectionState>? StateChanged;
    public event EventHandler<IncomingSessionNotification>? SessionRequested;
    public event EventHandler<PermissionResultNotification>? PermissionResolved;
    public event EventHandler<SdpNotification>? SdpReceived;
    public event EventHandler<IceNotification>? IceReceived;
    public event EventHandler<SessionEndedNotification>? SessionEnded;
    public event EventHandler<SignalingErrorNotification>? ErrorReceived;
    public event EventHandler<RemoteDisplaysNotification>? RemoteDisplaysReceived;
    public event EventHandler<SelectDisplayNotification>? DisplaySelectionRequested;
    public event EventHandler<SessionQualityNotification>? QualityReceived;
    public event EventHandler<SessionPeerResumedNotification>? PeerResumed;
    public event EventHandler<UnattendedChallengeRequestNotification>? UnattendedChallengeRequested
    {
        add { }
        remove { }
    }

    public Task ConnectAsync(DeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        State = SignalingConnectionState.Registered;
        RegisteredId = identity.PublicId;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        State = SignalingConnectionState.Disconnected;
        StateChanged?.Invoke(this, State);
        return Task.CompletedTask;
    }

    public Task RequestSessionAsync(SessionId sessionId, PeerOnQId target, SessionMode mode, CancellationToken cancellationToken = default)
    {
        if (FailNextRequest)
        {
            FailNextRequest = false;
            throw new InvalidOperationException("signaling unavailable");
        }

        Requests.Add((
            sessionId,
            target,
            mode,
            QualityProfile.Automatic,
            CaptureResolution.Automatic,
            SessionPermissionPolicy.ForMode(mode),
            SessionAccessKind.Attended,
            null,
            null,
            false));
        return Task.CompletedTask;
    }

    public Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        Guid? unattendedChallengeId,
        string? unattendedProof,
        QualityProfile quality,
        CancellationToken cancellationToken = default)
    {
        if (FailNextRequest)
        {
            FailNextRequest = false;
            throw new InvalidOperationException("signaling unavailable");
        }

        Requests.Add((
            sessionId,
            target,
            mode,
            quality,
            MediaProfile.For(quality).Resolution,
            permissions,
            accessKind,
            unattendedChallengeId,
            unattendedProof,
            false));
        return Task.CompletedTask;
    }

    public Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        Guid? unattendedChallengeId,
        string? unattendedProof,
        QualityProfile quality,
        CaptureResolution resolution,
        CancellationToken cancellationToken = default)
    {
        if (FailNextRequest)
        {
            FailNextRequest = false;
            throw new InvalidOperationException("signaling unavailable");
        }

        Requests.Add((
            sessionId,
            target,
            mode,
            quality,
            resolution,
            permissions,
            accessKind,
            unattendedChallengeId,
            unattendedProof,
            false));
        return Task.CompletedTask;
    }

    public Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        Guid? unattendedChallengeId,
        string? unattendedProof,
        QualityProfile quality,
        CaptureResolution resolution,
        bool remoteScopeSelectionRequired,
        CancellationToken cancellationToken = default)
    {
        if (FailNextRequest)
        {
            FailNextRequest = false;
            throw new InvalidOperationException("signaling unavailable");
        }

        Requests.Add((
            sessionId,
            target,
            mode,
            quality,
            resolution,
            permissions,
            accessKind,
            unattendedChallengeId,
            unattendedProof,
            remoteScopeSelectionRequired));
        return Task.CompletedTask;
    }

    public Task<UnattendedChallenge> RequestUnattendedChallengeAsync(
        PeerOnQId target,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextUnattendedChallenge
                        ?? throw new InvalidOperationException("No unattended challenge was configured."));

    public Task SendUnattendedChallengeResponseAsync(
        string requestId,
        PeerOnQId requester,
        UnattendedChallenge challenge,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SendPermissionDecisionAsync(SessionId sessionId, PermissionDecision decision, CancellationToken cancellationToken = default)
    {
        Decisions.Add((sessionId, decision));
        return Task.CompletedTask;
    }

    public Task SendPermissionDecisionAsync(
        SessionId sessionId,
        PermissionDecision decision,
        SessionMode grantedMode,
        SessionPermission grantedPermissions,
        CancellationToken cancellationToken = default)
    {
        Decisions.Add((sessionId, decision));
        GrantedDecisions.Add((sessionId, grantedMode, grantedPermissions));
        return Task.CompletedTask;
    }

    public Task SendSdpAsync(SessionId sessionId, string sdpType, string sdp, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Decrement(ref _sdpFailuresRemaining) >= 0)
            throw new InvalidOperationException("Synthetic transient signaling failure.");

        Sdps.Add((sessionId, sdpType, sdp));
        return Task.CompletedTask;
    }

    public void FailNextSdpSends(int count) =>
        Interlocked.Exchange(ref _sdpFailuresRemaining, Math.Max(0, count));

    public Task SendIceAsync(SessionId sessionId, string candidate, string? sdpMid, ushort sdpMLineIndex, CancellationToken cancellationToken = default)
    {
        IceCandidates.Add((sessionId, candidate));
        return Task.CompletedTask;
    }

    public Task EndSessionAsync(SessionId sessionId, SessionEndReason reason, CancellationToken cancellationToken = default)
    {
        Ends.Add((sessionId, reason));
        return Task.CompletedTask;
    }

    public Task<IceConfiguration> GetIceConfigurationAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        OnGetIceConfigurationAsync?.Invoke(cancellationToken)
        ?? Task.FromResult(IceConfiguration.DirectOnly);

    public List<(SessionId Id, IReadOnlyList<CaptureTargetInfo> Displays, string Active)> PublishedDisplays { get; } = [];
    public List<(SessionId Id, string DisplayId)> DisplayRequests { get; } = [];

    public Task SendDisplaysAsync(
        SessionId sessionId,
        IReadOnlyList<CaptureTargetInfo> displays,
        string activeDisplayId,
        CancellationToken cancellationToken = default)
    {
        PublishedDisplays.Add((sessionId, displays, activeDisplayId));
        return Task.CompletedTask;
    }

    public Task RequestDisplayAsync(SessionId sessionId, string displayId, CancellationToken cancellationToken = default)
    {
        DisplayRequests.Add((sessionId, displayId));
        return Task.CompletedTask;
    }

    public Task SendQualityAsync(
        SessionQualityNotification quality,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _qualitySendAttempts);
        if (TryConsumeQualityFailure())
            return Task.FromException(new IOException("Scripted quality send failure."));

        PublishedQuality.Add(quality);
        return Task.CompletedTask;
    }

    private bool TryConsumeQualityFailure()
    {
        while (true)
        {
            var remaining = Volatile.Read(ref _qualityFailuresRemaining);
            if (remaining <= 0) return false;
            if (Interlocked.CompareExchange(ref _qualityFailuresRemaining, remaining - 1, remaining) == remaining)
                return true;
        }
    }

    public Task<SessionResumeResult> ResumeSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ResumeRequests.Add(sessionId);
        return Task.FromResult(new SessionResumeResult(
            sessionId,
            Resumed: true,
            Reason: "resumed",
            ResumeExpiresAt: DateTimeOffset.UtcNow.AddSeconds(30)));
    }

    // Test drivers -------------------------------------------------------

    public void RaiseIncoming(
        SessionId id,
        PeerOnQId from,
        string name,
        TimeSpan timeout,
        QualityProfile quality = QualityProfile.Automatic,
        SessionMode mode = SessionMode.ViewOnly,
        SessionPermission permissions = SessionPermission.ViewScreen,
        SessionAccessKind accessKind = SessionAccessKind.Attended,
        string? fromKeyFingerprint = null,
        Guid? unattendedChallengeId = null,
        string? unattendedProof = null,
        CaptureResolution? resolution = null,
        bool remoteScopeSelectionRequired = false) =>
        SessionRequested?.Invoke(this, new IncomingSessionNotification(
            id,
            from,
            name,
            mode,
            DateTimeOffset.UtcNow + timeout,
            permissions,
            accessKind,
            fromKeyFingerprint,
            unattendedChallengeId,
            unattendedProof,
            Quality: quality,
            Resolution: resolution,
            RemoteScopeSelectionRequired: remoteScopeSelectionRequired));

    public void RaisePermission(
        SessionId id,
        PermissionDecision decision,
        string? peerName = "Peer",
        SessionMode? grantedMode = null,
        SessionPermission? grantedPermissions = null) =>
        PermissionResolved?.Invoke(this, new PermissionResultNotification(
            id,
            decision,
            peerName,
            GrantedMode: grantedMode,
            GrantedPermissions: grantedPermissions));

    public void RaiseSdp(SessionId id, string type, string sdp) =>
        SdpReceived?.Invoke(this, new SdpNotification(id, type, sdp));

    public void RaiseIce(SessionId id, string candidate) =>
        IceReceived?.Invoke(this, new IceNotification(id, candidate, "0", 0));

    public void RaiseEnded(SessionId id, SessionEndReason reason) =>
        SessionEnded?.Invoke(this, new SessionEndedNotification(id, reason));

    public void RaiseDisplays(SessionId id, IReadOnlyList<CaptureTargetInfo> displays, string active) =>
        RemoteDisplaysReceived?.Invoke(this, new RemoteDisplaysNotification(id, displays, active));

    public void RaiseDisplaySelection(SessionId id, string displayId) =>
        DisplaySelectionRequested?.Invoke(this, new SelectDisplayNotification(id, displayId));

    public void RaiseError(string code, string message) =>
        ErrorReceived?.Invoke(this, new SignalingErrorNotification(code, message, null));

    public void RaiseConnectionState(SignalingConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void RaiseQuality(SessionQualityNotification quality) =>
        QualityReceived?.Invoke(this, quality);

    public void RaisePeerResumed(SessionId sessionId) =>
        PeerResumed?.Invoke(this, new SessionPeerResumedNotification(sessionId));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeMediaSession(SessionId sessionId, SessionRole role) : IMediaSession
{
    private bool _dataChannelReady;

    public SessionId SessionId { get; } = sessionId;
    public SessionRole Role { get; } = role;
    public MediaConnectionState State { get; private set; } = MediaConnectionState.New;

    public bool Closed { get; private set; }
    public bool Disposed { get; private set; }
    public List<byte[]> SentData { get; } = [];
    public List<DataMessagePriority> SentDataPriorities { get; } = [];
    public FakeMediaSession? DataPeer { get; set; }
    public bool IsVideoFrameTelemetryNegotiated { get; set; }
    public bool IsInputAcknowledgementNegotiated { get; set; }
    public bool IsBulkDataLaneNegotiated { get; set; }
    public bool IsBulkDataLaneReady { get; set; }
    public bool IsNativeBulkTransportNegotiated { get; set; }
    public IPAddress? SelectedRemoteAddress { get; set; }
    public PeerClockEstimate? PeerClockEstimate { get; private set; }
    public List<InputLatencyMeasurement> InputLatencyMeasurements { get; } = [];
    public List<RemoteSessionQualityFeedback> RemoteQualityFeedback { get; } = [];

    public event EventHandler<MediaConnectionState>? StateChanged;
    public event EventHandler<(string Candidate, string? SdpMid, ushort SdpMLineIndex)>? LocalIceCandidate;
    public event EventHandler<RemoteVideoFrame>? RemoteFrameReceived;
    public event EventHandler? DataChannelReady;
    public event EventHandler<ReadOnlyMemory<byte>>? DataMessageReceived;

    public bool IsDataChannelReady => _dataChannelReady;

    public Task SendDataAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default,
        DataMessagePriority priority = DataMessagePriority.Normal)
    {
        if (!_dataChannelReady)
            return Task.FromException(new InvalidOperationException("Fake data channel is not ready."));

        SentData.Add(payload.ToArray());
        SentDataPriorities.Add(priority);
        DataPeer?.EmitData(payload);
        return Task.CompletedTask;
    }

    public Task<string> CreateOfferAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("v=0\r\no=- fake offer");

    public Task<string> CreateAnswerAsync(string remoteOffer, CancellationToken cancellationToken = default) =>
        Task.FromResult("v=0\r\no=- fake answer");

    public Task ApplyRemoteAnswerAsync(string answer, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AddRemoteIceCandidateAsync(string candidate, string? sdpMid, ushort sdpMLineIndex, CancellationToken cancellationToken = default)
    {
        AddedCandidates.Add(candidate);
        return Task.CompletedTask;
    }

    public List<string> AddedCandidates { get; } = [];
    public int IceRestarts { get; private set; }
    public MediaStatistics Statistics { get; set; } = new() { FramesEncoded = 42, CurrentFps = 30 };

    public Task<string> RestartIceAsync(CancellationToken cancellationToken = default)
    {
        IceRestarts++;
        return Task.FromResult("v=0\r\no=- fake restart offer");
    }

    public MediaStatistics GetStatistics() => Statistics;

    public IPAddress? GetSelectedRemoteAddress() => SelectedRemoteAddress;

    public void SetPeerClockEstimate(PeerClockEstimate? estimate) =>
        PeerClockEstimate = estimate;

    public PeerClockEstimate? GetPeerClockEstimate() => PeerClockEstimate;

    public void ReportInputLatency(InputLatencyMeasurement measurement) =>
        InputLatencyMeasurements.Add(measurement);

    public void ReportRemoteQualityFeedback(RemoteSessionQualityFeedback feedback) =>
        RemoteQualityFeedback.Add(feedback);

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        Closed = true;
        SetState(MediaConnectionState.Closed);
        return Task.CompletedTask;
    }

    public void SetState(MediaConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void SetDataChannelReady()
    {
        _dataChannelReady = true;
        DataChannelReady?.Invoke(this, EventArgs.Empty);
    }

    public void EmitData(ReadOnlyMemory<byte> payload) =>
        DataMessageReceived?.Invoke(this, payload);

    public void EmitIce(string candidate) => LocalIceCandidate?.Invoke(this, (candidate, "0", 0));

    public void EmitFrame() => RemoteFrameReceived?.Invoke(this,
        new RemoteVideoFrame(1280, 720, new byte[10], RemotePixelFormat.Bgr24, TimeSpan.Zero));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeInputSafetyController : IInputSafetyController
{
    public int DisableAndReleaseCalls { get; private set; }
    public int RestoreCalls { get; private set; }
    public SessionPermission? LastRestoredPermissions { get; private set; }

    public void DisableAndReleaseAll() => DisableAndReleaseCalls++;

    public void RestoreApprovedScope(SessionPermission approvedPermissions)
    {
        RestoreCalls++;
        LastRestoredPermissions = approvedPermissions;
    }

}

public sealed class FakeRemoteInputSink : IRemoteInputSink
{
    public CaptureTargetInfo? CaptureTarget { get; private set; }

    public void SetCaptureTarget(CaptureTargetInfo? target) => CaptureTarget = target;
    public void RestoreApprovedScope(SessionPermission approvedPermissions) { }
    public void DisableAndReleaseAll() { }
    public bool TryInject(RemoteInputEvent input) => true;
    public void ReleaseAll() { }
}

public sealed class FakeMediaEngine : IMediaEngine
{
    public FakeMediaSession? Sharer { get; private set; }
    public FakeMediaSession? Viewer { get; private set; }
    public FakeMediaSession? Last => Viewer ?? Sharer;
    public MediaProfile? SharerProfile { get; private set; }
    public SessionPermission? SharerPermissions { get; private set; }
    public SessionPermission? ViewerPermissions { get; private set; }
    public bool SharerHadCapture { get; private set; }
    public int SharerFramesObserved { get; private set; }

    public Task<IMediaSession> CreateSharerSessionAsync(
        SessionId sessionId, IScreenCaptureSource capture, MediaProfile profile, CancellationToken cancellationToken = default)
    {
        SharerProfile = profile;
        Sharer = new FakeMediaSession(sessionId, SessionRole.Sharer);
        return Task.FromResult<IMediaSession>(Sharer);
    }

    public Task<IMediaSession> CreateViewerSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        Viewer = new FakeMediaSession(sessionId, SessionRole.Viewer);
        return Task.FromResult<IMediaSession>(Viewer);
    }

    public Task<IMediaSession> CreateSharerSessionAsync(
        SessionId sessionId,
        IScreenCaptureSource? capture,
        MediaProfile profile,
        IceConfiguration iceConfiguration,
        SessionPermission permissions,
        CancellationToken cancellationToken = default)
    {
        SharerProfile = profile;
        SharerPermissions = permissions;
        SharerHadCapture = capture is not null;
        if (capture is not null)
        {
            capture.FrameArrived += (_, _) => SharerFramesObserved++;
        }
        Sharer = new FakeMediaSession(sessionId, SessionRole.Sharer);
        return Task.FromResult<IMediaSession>(Sharer);
    }

    public Task<IMediaSession> CreateViewerSessionAsync(
        SessionId sessionId,
        IceConfiguration iceConfiguration,
        SessionPermission permissions,
        CancellationToken cancellationToken = default)
    {
        ViewerPermissions = permissions;
        Viewer = new FakeMediaSession(sessionId, SessionRole.Viewer);
        return Task.FromResult<IMediaSession>(Viewer);
    }
}

public sealed class FakeCaptureSource : IScreenCaptureSource, IAdaptiveCaptureSource
{
    public IReadOnlyList<CaptureTargetInfo> AvailableTargets { get; set; } =
    [
        new(CaptureTargetKind.Display, @"\\.\DISPLAY1", "Display 1", 1920, 1080),
        new(CaptureTargetKind.Display, @"\\.\DISPLAY2", "Display 2", 2560, 1440),
    ];

    public int DownscaleFactor { get; private set; } = 1;

    public int TargetFps { get; private set; } = 30;

    public void SetDownscaleFactor(int factor) => DownscaleFactor = factor;

    public void SetTargetFps(int fps) => TargetFps = fps;

    public Task SwitchTargetAsync(CaptureTargetInfo target, CancellationToken cancellationToken = default)
    {
        Target = target;
        SwitchedTargets.Add(target);
        return Task.CompletedTask;
    }

    public List<CaptureTargetInfo> SwitchedTargets { get; } = [];

    public bool IsCapturing { get; private set; }
    public CaptureTargetInfo? Target { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }
    public CaptureRequest? LastRequest { get; private set; }
    public Exception? StartException { get; set; }
    public bool EmitFrameOnStart { get; set; }

    public event EventHandler<CapturedFrame>? FrameArrived;
    public event EventHandler<CaptureStoppedReason>? CaptureStopped;

    public Task StartAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        if (StartException is not null) throw StartException;
        LastRequest = request;
        Target = request.Target;
        IsCapturing = true;
        if (EmitFrameOnStart) EmitFrame();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;
        Stopped = true;
        CaptureStopped?.Invoke(this, CaptureStoppedReason.StoppedByUser);
        return Task.CompletedTask;
    }

    public void EmitFrame() => FrameArrived?.Invoke(this, new CapturedFrame
    {
        Width = 1280,
        Height = 720,
        I420 = new byte[1280 * 720 * 3 / 2],
        Timestamp = TimeSpan.Zero,
        SequenceNumber = 1,
    });

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class ScriptedPermissionPrompt(
    PermissionDecision decision,
    TimeSpan? delay = null,
    SessionMode? grantedMode = null,
    SessionPermission? grantedPermissions = null) : IPermissionPrompt
{
    public int Calls { get; private set; }
    public PermissionRequest? LastRequest { get; private set; }

    public async Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;

        if (delay is { } wait)
        {
            await Task.Delay(wait, cancellationToken);
        }

        return decision;
    }

    public async Task<PermissionPromptResult> AskForScopeAsync(
        PermissionRequest request,
        CancellationToken cancellationToken = default)
    {
        var resolvedDecision = await AskAsync(request, cancellationToken);
        return resolvedDecision == PermissionDecision.Accept
               && grantedMode is { } mode
               && grantedPermissions is { } permissions
            ? new PermissionPromptResult(resolvedDecision, mode, permissions)
            : PermissionPromptResult.ForRequestedScope(resolvedDecision, request);
    }
}

public sealed class FakeClipboardAdapter : IClipboardAdapter
{
    public string? Text { get; private set; }
    public event EventHandler? ContentChanged;

    public Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Text);

    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        Text = text;
        ContentChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Text = null;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryBlockedDeviceStore : IBlockedDeviceStore
{
    private readonly ConcurrentDictionary<string, byte> _blocked = new();

    public Task<bool> IsBlockedAsync(PeerOnQId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_blocked.ContainsKey(id.Value));

    public Task BlockAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        _blocked[id.Value] = 1;
        return Task.CompletedTask;
    }

    public Task UnblockAsync(PeerOnQId id, CancellationToken cancellationToken = default)
    {
        _blocked.TryRemove(id.Value, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PeerOnQId>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PeerOnQId>>(_blocked.Keys.Select(PeerOnQId.Parse).ToArray());
}

public sealed class InMemoryAuditLog : ISessionAuditLog
{
    public List<SessionAuditEntry> Entries { get; } = [];

    public Task RecordAsync(SessionAuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (Entries) { Entries.Add(entry); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionAuditEntry>> RecentAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        lock (Entries) { return Task.FromResult<IReadOnlyList<SessionAuditEntry>>(Entries.ToArray()); }
    }
}

public sealed class InMemoryNativeBulkHub
{
    private readonly ConcurrentDictionary<int, InMemoryNativeBulkListener> _listeners = new();
    private int _nextPort = 40_000;
    private int _connections;
    private long _messagesSent;

    public long MessagesSent => Interlocked.Read(ref _messagesSent);
    public int Connections => Volatile.Read(ref _connections);

    internal InMemoryNativeBulkListener Listen(string localPin, string expectedPeerPin)
    {
        var port = Interlocked.Increment(ref _nextPort);
        var listener = new InMemoryNativeBulkListener(this, port, localPin, expectedPeerPin);
        if (!_listeners.TryAdd(port, listener))
            throw new InvalidOperationException("The in-memory native listener port collided.");
        return listener;
    }

    internal IReliableRemoteSessionTransport Connect(
        int port,
        string localPin,
        string expectedPeerPin)
    {
        if (!_listeners.TryGetValue(port, out var listener))
            throw new IOException("The in-memory native listener is unavailable.");
        var transport = listener.Connect(localPin, expectedPeerPin);
        Interlocked.Increment(ref _connections);
        return transport;
    }

    internal void Remove(int port, InMemoryNativeBulkListener listener) =>
        _listeners.TryRemove(new KeyValuePair<int, InMemoryNativeBulkListener>(port, listener));

    internal void CountMessage() => Interlocked.Increment(ref _messagesSent);
}

public sealed class InMemoryNativeBulkTransportFactory(InMemoryNativeBulkHub hub)
    : INativeBulkTransportFactory
{
    public bool IsSupported => true;

    public ValueTask<INativeBulkTransportEndpoint> CreateEndpointAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<INativeBulkTransportEndpoint>(
            new InMemoryNativeBulkEndpoint(hub, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));
    }
}

public sealed class GatedNativeBulkTransportFactory(InMemoryNativeBulkHub hub)
    : INativeBulkTransportFactory
{
    private readonly InMemoryNativeBulkTransportFactory _inner = new(hub);
    private readonly TaskCompletionSource _creationRequested = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsSupported => true;
    public Task CreationRequested => _creationRequested.Task;

    public async ValueTask<INativeBulkTransportEndpoint> CreateEndpointAsync(
        CancellationToken cancellationToken = default)
    {
        _creationRequested.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        return await _inner.CreateEndpointAsync(cancellationToken);
    }

    public void Release() => _release.TrySetResult();
}

internal sealed class InMemoryNativeBulkEndpoint(InMemoryNativeBulkHub hub, string localPin)
    : INativeBulkTransportEndpoint
{
    public string CertificateSha256 => localPin;

    public ValueTask<INativeBulkTransportListener> ListenAsync(
        IPAddress localAddress,
        string expectedPeerCertificateSha256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<INativeBulkTransportListener>(
            hub.Listen(localPin, expectedPeerCertificateSha256));
    }

    public ValueTask<IReliableRemoteSessionTransport> ConnectAsync(
        IPEndPoint remoteEndPoint,
        string expectedPeerCertificateSha256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            hub.Connect(remoteEndPoint.Port, localPin, expectedPeerCertificateSha256));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class InMemoryNativeBulkListener(
    InMemoryNativeBulkHub hub,
    int port,
    string localPin,
    string expectedPeerPin) : INativeBulkTransportListener
{
    private readonly TaskCompletionSource<IReliableRemoteSessionTransport> _accepted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, port);

    public async ValueTask<IReliableRemoteSessionTransport> AcceptAsync(
        CancellationToken cancellationToken = default) =>
        await _accepted.Task.WaitAsync(cancellationToken);

    public IReliableRemoteSessionTransport Connect(string peerPin, string expectedServerPin)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!string.Equals(localPin, expectedServerPin, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expectedPeerPin, peerPin, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The in-memory native certificate pin was rejected.");
        }

        var client = new InMemoryNativeBulkTransport(hub, LocalEndPoint, new IPEndPoint(IPAddress.Loopback, 50_001));
        var server = new InMemoryNativeBulkTransport(hub, new IPEndPoint(IPAddress.Loopback, 50_001), LocalEndPoint);
        client.Peer = server;
        server.Peer = client;
        if (!_accepted.TrySetResult(server))
            throw new IOException("The in-memory native listener already accepted a connection.");
        return client;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            hub.Remove(port, this);
            _accepted.TrySetCanceled();
        }
        return ValueTask.CompletedTask;
    }
}

internal sealed class InMemoryNativeBulkTransport(
    InMemoryNativeBulkHub hub,
    IPEndPoint remoteEndPoint,
    IPEndPoint localEndPoint) : IReliableRemoteSessionTransport
{
    private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private int _disposed;

    public InMemoryNativeBulkTransport? Peer { get; set; }
    public EndPoint LocalEndPoint => localEndPoint;
    public EndPoint RemoteEndPoint => remoteEndPoint;
    public RemoteTransportCapabilities Capabilities { get; } = new()
    {
        Kind = RemoteSessionTransportKind.NativeQuic,
        ProtocolVersion = 1,
        SupportsReliableStreams = true,
    };

    public async ValueTask SendAsync(
        RemoteTransportChannel channel,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (channel != RemoteTransportChannel.FileTransfer)
            throw new ArgumentOutOfRangeException(nameof(channel));
        var peer = Peer ?? throw new IOException("The in-memory native peer is unavailable.");
        hub.CountMessage();
        await peer._incoming.Writer.WriteAsync(payload.ToArray(), cancellationToken);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
        RemoteTransportChannel channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (channel != RemoteTransportChannel.FileTransfer)
            throw new ArgumentOutOfRangeException(nameof(channel));
        await foreach (var payload in _incoming.Reader.ReadAllAsync(cancellationToken))
            yield return payload;
    }

    public ValueTask<IRemoteTransportLane> OpenLaneAsync(
        RemoteTransportChannel channel,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IRemoteTransportLane>(new NotSupportedException());

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => CloseAsync();
}
