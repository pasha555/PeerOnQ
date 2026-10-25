using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Errors;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerOnQ.Application.Sessions;

public sealed record ActiveSessionInfo(
    SessionId SessionId,
    SessionRole Role,
    string PeerDisplayName,
    string PeerMaskedId,
    SessionMode Mode,
    SessionState State,
    DateTimeOffset StartedAt,
    SessionPermission Permissions = SessionPermission.ViewScreen,
    SessionAccessKind AccessKind = SessionAccessKind.Attended,
    string? PeerKeyFingerprint = null,
    PeerOnQId? PeerDeviceId = null,
    SecureSessionInfo? Security = null,
    bool SignalingUnavailable = false);

/// <summary>
/// Owns every session on this device: the viewer flow, the sharer flow, the timeouts and the
/// resource cleanup. The state machine is the single source of truth; anything that would be
/// an illegal transition is refused and logged rather than silently applied.
/// </summary>
public sealed class SessionCoordinator : IAsyncDisposable
{
    internal static readonly TimeSpan QualityPublishInterval = TimeSpan.FromMilliseconds(250);
    private const int QualityPublishMessageBudgetPerSecond = 20;

    internal static TimeSpan GetQualityPublishInterval(int activeSessionCount)
    {
        var sessions = Math.Max(1, activeSessionCount);
        var budgeted = TimeSpan.FromSeconds(sessions / (double)QualityPublishMessageBudgetPerSecond);
        return budgeted > QualityPublishInterval ? budgeted : QualityPublishInterval;
    }

    private static readonly TimeSpan EndNotificationTimeout = TimeSpan.FromSeconds(3);

    private readonly ISignalingClient _signaling;
    private readonly IMediaEngine _mediaEngine;
    private readonly IPermissionPrompt _permissionPrompt;
    private readonly IBlockedDeviceStore _blockedDevices;
    private readonly ISessionAuditLog _auditLog;
    private readonly Func<IScreenCaptureSource> _captureFactory;
    private readonly SessionOptions _options;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly IInputSafetyController _inputSafety;
    private readonly IRemoteInputSink? _remoteInputSink;
    private readonly ISessionQualitySignaling? _qualitySignaling;
    private readonly IResumableSignalingClient? _resumableSignaling;
    private readonly UnattendedAccessService? _unattendedAccess;
    private readonly Func<IClipboardAdapter>? _clipboardFactory;
    private readonly ISecurityAuditLog? _securityAudit;
    private readonly IUnattendedSignalingClient? _unattendedSignaling;
    private readonly IFileRelaySignaling? _fileRelaySignaling;
    private readonly IMalwareScanner _malwareScanner;
    private readonly IClientSessionEventSink _clientSessionEvents;
    private readonly IHybridDeviceIdentityProvider? _hybridIdentity;
    private readonly SupportInvitationService? _supportInvitations;
    private readonly INativeBulkTransportFactory? _nativeBulkTransportFactory;
    private readonly Func<ISystemAudioCaptureSource>? _systemAudioCaptureFactory;
    private readonly Func<ISystemAudioPlaybackSink>? _systemAudioPlaybackFactory;
    private readonly Func<IMediaSession, SessionPermission, string, ICollaborationTransport>?
        _collaborationTransportFactory;
    private readonly SessionTimelineStore _timeline;
    private readonly RemoteInputFocusCoordinator _remoteInputFocus = new();

    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, SessionRuntime> _sessions = [];
    private readonly HashSet<Task> _startupOperations = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private TaskCompletionSource<bool>? _disposeCompletion;
    private bool _disposing;

    public SessionCoordinator(
        ISignalingClient signaling,
        IMediaEngine mediaEngine,
        IPermissionPrompt permissionPrompt,
        IBlockedDeviceStore blockedDevices,
        ISessionAuditLog auditLog,
        Func<IScreenCaptureSource> captureFactory,
        SessionOptions? options = null,
        ILogger<SessionCoordinator>? logger = null,
        TimeProvider? timeProvider = null,
        IInputSafetyController? inputSafetyController = null,
        IRemoteInputSink? remoteInputSink = null,
        UnattendedAccessService? unattendedAccess = null,
        Func<IClipboardAdapter>? clipboardFactory = null,
        ISecurityAuditLog? securityAudit = null,
        IMalwareScanner? malwareScanner = null,
        IClientSessionEventSink? clientSessionEvents = null,
        IHybridDeviceIdentityProvider? hybridIdentity = null,
        Func<IMediaSession, SessionPermission, string, ICollaborationTransport>?
            collaborationTransportFactory = null,
        SupportInvitationService? supportInvitations = null,
        INativeBulkTransportFactory? nativeBulkTransportFactory = null,
        Func<ISystemAudioCaptureSource>? systemAudioCaptureFactory = null,
        Func<ISystemAudioPlaybackSink>? systemAudioPlaybackFactory = null)
    {
        _signaling = signaling;
        _mediaEngine = mediaEngine;
        _permissionPrompt = permissionPrompt;
        _blockedDevices = blockedDevices;
        _auditLog = auditLog;
        _captureFactory = captureFactory;
        _systemAudioCaptureFactory = systemAudioCaptureFactory;
        _systemAudioPlaybackFactory = systemAudioPlaybackFactory;
        _options = options ?? SessionOptions.Default;
        _log = logger ?? NullLogger<SessionCoordinator>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _timeline = new SessionTimelineStore(_time);
        _remoteInputSink = remoteInputSink;
        _inputSafety = inputSafetyController
                       ?? (IInputSafetyController?)remoteInputSink
                       ?? NoOpInputSafetyController.Instance;
        _qualitySignaling = signaling as ISessionQualitySignaling;
        _resumableSignaling = signaling as IResumableSignalingClient;
        _unattendedAccess = unattendedAccess;
        _clipboardFactory = clipboardFactory;
        _securityAudit = securityAudit;
        _unattendedSignaling = signaling as IUnattendedSignalingClient;
        _fileRelaySignaling = signaling as IFileRelaySignaling;
        _malwareScanner = malwareScanner ?? NoOpMalwareScanner.Instance;
        _clientSessionEvents = clientSessionEvents ?? NoOpClientSessionEventSink.Instance;
        _hybridIdentity = hybridIdentity;
        _collaborationTransportFactory = collaborationTransportFactory;
        _supportInvitations = supportInvitations;
        _nativeBulkTransportFactory = nativeBulkTransportFactory;

        _signaling.SessionRequested += OnSessionRequested;
        _signaling.PermissionResolved += OnPermissionResolved;
        _signaling.SdpReceived += OnSdpReceived;
        _signaling.IceReceived += OnIceReceived;
        _signaling.SessionEnded += OnSessionEnded;
        _signaling.RemoteDisplaysReceived += OnRemoteDisplays;
        _signaling.DisplaySelectionRequested += OnDisplaySelectionRequested;
        _signaling.StateChanged += OnSignalingStateChanged;
        _signaling.ErrorReceived += OnSignalingError;
        if (_qualitySignaling is not null) _qualitySignaling.QualityReceived += OnQualityReceived;
        if (_resumableSignaling is not null) _resumableSignaling.PeerResumed += OnPeerResumed;
        if (_unattendedSignaling is not null)
            _unattendedSignaling.UnattendedChallengeRequested += OnUnattendedChallengeRequested;
    }

    public MediaProfile Profile { get; set; } = MediaProfile.Conservative;

    /// <summary>Sharer side: set by the UI before accepting, so we know what to capture.</summary>
    public CaptureTargetInfo? PreferredCaptureTarget { get; set; }

    public event EventHandler<ActiveSessionInfo>? SessionChanged;
    public event EventHandler<(SessionId Id, SessionEndReason Reason)>? SessionClosed;
    public event EventHandler<RemoteVideoFrame>? RemoteFrameReceived;
    public event EventHandler<SessionRemoteFrame>? SessionRemoteFrameReceived;
    public event EventHandler<SessionCollaborationContext>? CollaborationAvailable;

    /// <summary>Viewer: the sharer published which of its displays can be shown.</summary>
    public event EventHandler<RemoteDisplaysNotification>? RemoteDisplaysChanged;

    public IReadOnlyList<ActiveSessionInfo> ActiveSessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values.Select(s => s.ToInfo()).ToArray();
            }
        }
    }

    public SessionState StateOf(SessionId id)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(id, out var runtime) ? runtime.Machine.State : SessionState.Idle;
        }
    }

    /// <summary>True when this session's peer opened the encrypted two-way voice lane.</summary>
    public bool IsVoiceAvailable(SessionId id)
    {
        lock (_gate)
            return _sessions.TryGetValue(id, out var runtime) && runtime.Media is { IsVoiceLaneReady: true };
    }

    public bool IsVoiceEnabled(SessionId id)
    {
        lock (_gate)
            return _sessions.TryGetValue(id, out var runtime) && runtime.Media is { IsVoiceEnabled: true };
    }

    /// <summary>The longest current screen-sharing video stall of any session (stall watchdog).</summary>
    public TimeSpan? LongestVideoPipelineStall
    {
        get
        {
            IMediaSession?[] media;
            lock (_gate) media = [.. _sessions.Values.Select(runtime => runtime.Media)];
            TimeSpan? longest = null;
            foreach (var session in media)
            {
                if (session?.VideoPipelineStalledFor is { } stalled && (longest is null || stalled > longest))
                    longest = stalled;
            }
            return longest;
        }
    }

    /// <summary>The peer is talking (for the in-session indicator).</summary>
    public bool IsPeerVoiceActive(SessionId id)
    {
        lock (_gate)
            return _sessions.TryGetValue(id, out var runtime) && runtime.Media is { IsPeerVoiceActive: true };
    }

    /// <summary>
    /// Why sound and voice are missing from a connected session, or null when they are available:
    /// the request carried no sound because an organization policy forbids it or a device cannot
    /// capture or play it (the server removes it rather than refusing the session).
    /// </summary>
    public string? AudioUnavailableReason(SessionId id)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var runtime)
                || !runtime.EffectivePermissions.HasFlag(SessionPermission.ViewScreen)) return null;
            return runtime.EffectivePermissions.HasFlag(SessionPermission.SystemAudio)
                ? null
                : "Sound and voice are off for this session (organization policy or a device without audio support).";
        }
    }

    /// <summary>
    /// Local opt-in for one session: enabling opens this device's microphone (the peer's voice
    /// plays automatically). It is only ever called from this device's own UI; the peer cannot
    /// invoke it.
    /// </summary>
    public bool SetVoiceEnabled(SessionId id, bool enabled)
    {
        IMediaSession? media;
        lock (_gate) media = _sessions.TryGetValue(id, out var runtime) ? runtime.Media : null;
        if (media is null) return !enabled;
        var applied = media.SetVoiceEnabled(enabled);
        if (applied)
        {
            _timeline.Record(id, enabled ? "voice.enabled" : "voice.disabled",
                enabled ? "Microphone turned on locally" : "Microphone turned off locally");
        }
        return applied;
    }

    public MediaStatistics StatisticsOf(SessionId id)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var runtime) || runtime.Media is null)
                return MediaStatistics.Empty;

            var statistics = runtime.Media.GetStatistics() with
            {
                ReconnectState = runtime.Reconnect.State,
                ReconnectAttempts = runtime.ReconnectAttempts,
                LastInterruptionReason = runtime.LastInterruptionReason,
                NativeBulkTransportReady = runtime.Collaboration?.IsNativeBulkTransportReady == true,
                NativeBulkBudgetKbps = runtime.Collaboration?.NativeBulkBudgetKbps ?? 0,
                NativeBulkGoodputKbps = runtime.Collaboration?.NativeBulkGoodputKbps ?? 0,
                NativeBulkFeedbackSamples = runtime.Collaboration?.NativeBulkFeedbackSamples ?? 0,
            };

            // Capture and encode happen on the sharer. Relay only these address-free values so
            // the viewer does not present zeros as if they were local measurements.
            if (runtime.Role == SessionRole.Viewer && runtime.RemoteQuality is { } remote)
            {
                statistics = statistics with
                {
                    CaptureFps = remote.CaptureFps,
                    EncodeFps = remote.EncodeFps,
                    PacketLossPercent = remote.PacketLossPercent,
                    JitterMs = remote.JitterMs,
                    AvailableOutgoingBitrateKbps = remote.AvailableOutgoingBitrateKbps,
                    FramesDropped = remote.FramesDropped,
                    SourceWidth = remote.SourceWidth,
                    SourceHeight = remote.SourceHeight,
                    RequestedWidth = remote.RequestedWidth,
                    RequestedHeight = remote.RequestedHeight,
                    EncodedWidth = remote.EncodedWidth,
                    EncodedHeight = remote.EncodedHeight,
                    EncoderName = remote.EncoderName,
                    EncoderHardwareAccelerated = remote.EncoderHardwareAccelerated,
                    ConnectionHealth = remote.ConnectionHealth,
                    ActiveQualityProfile = remote.ActiveQualityProfile,
                    AdaptiveQualityLevel = remote.AdaptiveQualityLevel,
                    QualityChangeReason = remote.QualityChangeReason,
                    TargetFps = remote.TargetFps,
                    TargetBitrateKbps = remote.TargetBitrateKbps,
                    EncoderQueueDepth = remote.EncoderQueueDepth,
                };
            }

            return statistics;
        }
    }

    public IReadOnlyList<SessionTimelineEntry> TimelineOf(SessionId id) => _timeline.Get(id);

    public SessionCollaborationContext? CollaborationFor(SessionId id)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id)?.Collaboration;
    }

    /// <summary>
    /// Sharer-side, monotonic reduction of the live ControlInput capability. The accepted mask
    /// remains immutable for audit/history, while the effective mask can only lose permission.
    /// </summary>
    public async Task<bool> RevokeControlAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var runtime = Find(sessionId);
        if (runtime is null
            || runtime.Role != SessionRole.Sharer
            || !runtime.Permissions.HasFlag(SessionPermission.ControlInput))
            return false;

        RemoteInputSession? remoteInput;
        var changed = false;
        await runtime.LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsRuntimeActive(runtime)) return false;
            changed = !runtime.RevokedPermissions.HasFlag(SessionPermission.ControlInput);
            runtime.RevokedPermissions |= SessionPermission.ControlInput;
            remoteInput = runtime.Collaboration?.RemoteInput;
            DisablePlatformInput(runtime);
        }
        finally
        {
            runtime.LifecycleGate.Release();
        }

        if (remoteInput is not null)
            await remoteInput.RevokeControlAsync("owner_revoked", cancellationToken);

        if (changed)
        {
            await AppendSecurityAuditAsync(new SecurityAuditEvent
            {
                EventId = Guid.NewGuid(),
                EventType = SecurityAuditEventType.PermissionChanged,
                OccurredAt = _time.GetUtcNow(),
                SessionId = runtime.SessionId.ToString(),
                PeerMaskedId = runtime.PeerId.Masked,
                PermissionSet = runtime.EffectivePermissions,
                Outcome = "control_revoked",
                FailureCategory = null,
            });
        }

        return true;
    }

    public void ReportFrameRendered(
        SessionId id,
        long decodedTimestamp = 0,
        int width = 0,
        int height = 0)
    {
        ReportFrameRendered(id, new PresentedVideoFrame(
            decodedTimestamp,
            width,
            height,
            0,
            0,
            null));
    }

    public void ReportFrameRendered(SessionId id, PresentedVideoFrame frame)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var runtime)) return;
            runtime.Media?.ReportFrameRendered(frame);
            if (!runtime.FirstFrameRendered)
            {
                runtime.FirstFrameRendered = true;
                _timeline.Record(id, "media.first-frame.rendered", "First remote frame rendered");
            }
        }
    }

    // ------------------------------------------------------------------ viewer

    /// <summary>Viewer: ask <paramref name="target"/> for a view-only session.</summary>
    public async Task<SessionId> RequestViewOnlySessionAsync(
        PeerOnQId target,
        CancellationToken cancellationToken = default) =>
        await RequestSessionAsync(
            target,
            SessionMode.ViewOnly,
            SessionPermission.ViewScreen,
            SessionAccessKind.Attended,
            cancellationToken);

    /// <summary>
    /// Requests the attended full-control envelope while requiring the remote owner to choose
    /// whether this session remains view-only or becomes full control. The granted scope is
    /// bound by signaling before either media session starts.
    /// </summary>
    public async Task<SessionId> RequestRemoteSelectedSessionAsync(
        PeerOnQId target,
        CancellationToken cancellationToken = default) =>
        await RequestRemoteSelectedSessionAsync(target, requestSystemAudio: false, cancellationToken);

    public async Task<SessionId> RequestRemoteSelectedSessionAsync(
        PeerOnQId target,
        bool requestSystemAudio,
        CancellationToken cancellationToken = default) =>
        await RequestSessionCoreAsync(
            target,
            SessionMode.FullControl,
            Phase1SessionScope.FullControlPermissions | (requestSystemAudio ? SessionPermission.SystemAudio : SessionPermission.None),
            SessionAccessKind.Attended,
            unattendedPassword: null,
            supportInvitationToken: null,
            supportInvitationPassword: null,
            remoteScopeSelectionRequired: true,
            cancellationToken);

    public async Task<SessionId> RequestSessionAsync(
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind = SessionAccessKind.Attended,
        CancellationToken cancellationToken = default) =>
        await RequestSessionAsync(target, mode, permissions, accessKind, unattendedPassword: null, cancellationToken);

    public async Task<SessionId> RequestSessionAsync(
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        string? unattendedPassword,
        CancellationToken cancellationToken = default) =>
        await RequestSessionCoreAsync(
            target,
            mode,
            permissions,
            accessKind,
            unattendedPassword,
            supportInvitationToken: null,
            supportInvitationPassword: null,
            remoteScopeSelectionRequired: false,
            cancellationToken);

    public async Task<SessionId> RequestSupportSessionAsync(
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        string invitationToken,
        string? invitationPassword = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invitationToken))
            throw new ArgumentException("A support invitation token is required.", nameof(invitationToken));

        return await RequestSessionCoreAsync(
            target,
            mode,
            permissions,
            SessionAccessKind.SupportInvitation,
            unattendedPassword: null,
            invitationToken,
            invitationPassword,
            remoteScopeSelectionRequired: false,
            cancellationToken);
    }

    private async Task<SessionId> RequestSessionCoreAsync(
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        string? unattendedPassword,
        string? supportInvitationToken,
        string? supportInvitationPassword,
        bool remoteScopeSelectionRequired,
        CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var operationToken = operationCancellation.Token;
        operationToken.ThrowIfCancellationRequested();

        if (!SessionPermissionPolicy.IsValid(mode, permissions))
            throw new ArgumentException("The requested permissions do not match the selected session profile.", nameof(permissions));
        Phase1SessionScope.EnsureAllowed(mode, permissions, accessKind);
        if (remoteScopeSelectionRequired
            && (accessKind != SessionAccessKind.Attended
                || mode != SessionMode.FullControl
                || !Phase1SessionScope.IsRemoteScopeSelectionEnvelope(permissions)))
        {
            throw new ArgumentException(
                "Remote scope selection is available only for attended full-control requests.",
                nameof(remoteScopeSelectionRequired));
        }

        // Signaling refuses it anyway (target_is_self); 0.9.86 still waited 30 s for that answer
        // and reported the device to the cloud as its own peer.
        if (_signaling.RegisteredId is { } registeredId && registeredId == target)
            throw new ArgumentException(
                "This is this computer's own PeerOnQ ID. Enter the ID shown on the other computer.",
                nameof(target));

        // A second request while the first still waits for the other user opens a second
        // dialog there (WinUI shows one at a time) and confuses both people; wait for the answer.
        lock (_gate)
        {
            if (_sessions.Values.Any(existing => existing.Role == SessionRole.Viewer
                    && existing.PeerId == target
                    && existing.Machine.State is SessionState.ResolvingDevice
                        or SessionState.RequestingPermission
                        or SessionState.AwaitingPermission))
                throw new InvalidOperationException(
                    "A connection request to this device is already waiting for an answer.");
        }

        var sessionId = SessionId.New();
        var requestedProfile = Profile with { Resolution = CaptureResolutionPolicy.Normalize(Profile.Resolution) };
        var runtime = new SessionRuntime(
            sessionId,
            SessionRole.Viewer,
            target,
            target.Masked,
            _time.GetUtcNow(),
            mode,
            permissions,
            accessKind,
            null,
            requestedProfile,
            remoteScopeSelectionRequired);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _sessions[sessionId] = runtime;
        }

        _timeline.Record(sessionId, "session.request.created", "Session request created");

        runtime.Machine.TransitionRejected += (_, e) =>
            _log.LogWarning("Rejected transition {Trigger} in {State} for session {SessionId}",
                e.Trigger, e.State, sessionId);

        Fire(runtime, SessionTrigger.StartRequest);
        Fire(runtime, SessionTrigger.DeviceResolved);
        _timeline.Record(sessionId, "session.device.online", "Remote device found online");
        // The viewer's cloud Started waits until the request reached the peer (an answer, or the
        // end of the permission window): a request that signaling refused is never reported.

        try
        {
            Guid? challengeId = null;
            string? proof = null;
            if (accessKind == SessionAccessKind.Unattended && !string.IsNullOrEmpty(unattendedPassword))
            {
                if (_unattendedSignaling?.LocalKeyFingerprint is not { } localFingerprint
                    || _signaling.RegisteredId is not { } localId)
                    throw new InvalidOperationException("The signaling client cannot perform unattended password authentication.");
                var challenge = await _unattendedSignaling
                    .RequestUnattendedChallengeAsync(target, operationToken)
                    .WaitAsync(operationToken);
                if (challenge.AllowedPermissions is { } allowedPermissions
                    && (permissions & ~allowedPermissions) != 0)
                {
                    throw new UnattendedPermissionMismatchException(permissions, allowedPermissions);
                }

                proof = await Task.Run(
                    () => UnattendedAccessService.CreatePasswordProof(
                        unattendedPassword,
                        challenge,
                        localId,
                        localFingerprint,
                        permissions),
                    operationToken);
                challengeId = challenge.ChallengeId;
            }

            if (accessKind == SessionAccessKind.SupportInvitation)
            {
                await _signaling.RequestSupportSessionAsync(
                    sessionId,
                    target,
                    mode,
                    permissions,
                    supportInvitationToken!,
                    supportInvitationPassword,
                    requestedProfile.Quality,
                    requestedProfile.Resolution,
                    operationToken).WaitAsync(operationToken);
            }
            else
            {
                await _signaling.RequestSessionAsync(
                    sessionId,
                    target,
                    mode,
                    permissions,
                    accessKind,
                    challengeId,
                    proof,
                    requestedProfile.Quality,
                    requestedProfile.Resolution,
                    remoteScopeSelectionRequired,
                    operationToken).WaitAsync(operationToken);
            }
        }
        catch (UnattendedPermissionMismatchException)
        {
            _log.LogInformation(
                "Remote unattended policy did not allow the requested mode for {Target}",
                target.Masked);
            // No session request was sent: nothing reached a peer, so nothing is reported.
            runtime.CloudTelemetrySuppressed = true;
            await FailAsync(runtime, SessionEndReason.PermissionDeclined);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Session request failed for {Target}", target.Masked);
            runtime.CloudTelemetrySuppressed = true;
            await FailAsync(runtime, SessionEndReason.SignalingLost);
            throw;
        }

        operationToken.ThrowIfCancellationRequested();
        // Signaling can refuse the request before this send even returned.
        if (runtime.RequestRefusal is { } refusal)
            throw new InvalidOperationException(refusal);
        Fire(runtime, SessionTrigger.PermissionRequestSent);
        Notify(runtime);

        // No answer inside the permission window ends the attempt.
        runtime.ArmTimeout(_options.PermissionTimeout, () => TimeoutAsync(runtime, SessionEndReason.PermissionTimeout));

        return sessionId;
    }

    // ------------------------------------------------------------------ sharer

    private void OnSessionRequested(object? sender, IncomingSessionNotification notification) =>
        RunStartupOperation(
            cancellationToken => HandleIncomingAsync(notification, cancellationToken),
            "Failed to handle an incoming session request");

    private async Task HandleIncomingAsync(
        IncomingSessionNotification notification,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedProfile = MediaProfile.For(notification.Quality).ForViewerDisplay(notification.ViewerMaxFps);
        if (notification.Resolution is { } requestedResolution)
            requestedProfile = requestedProfile with { Resolution = CaptureResolutionPolicy.Normalize(requestedResolution) };

        if (!SessionPermissionPolicy.IsValid(notification.Mode, notification.Permissions)
            || !Phase1SessionScope.IsAllowed(
                notification.Mode,
                notification.Permissions,
                notification.AccessKind)
            || (notification.Permissions.HasFlag(SessionPermission.ControlInput) && _remoteInputSink is null))
        {
            await _signaling.SendPermissionDecisionAsync(
                notification.SessionId,
                PermissionDecision.Decline,
                cancellationToken).WaitAsync(cancellationToken);
            return;
        }

        if (await _blockedDevices.IsBlockedAsync(notification.FromDeviceId, cancellationToken)
                .WaitAsync(cancellationToken))
        {
            _log.LogInformation("Auto-declining a request from blocked device {Device}",
                notification.FromDeviceId.Masked);
            await _signaling.SendPermissionDecisionAsync(
                notification.SessionId,
                PermissionDecision.Decline,
                cancellationToken).WaitAsync(cancellationToken);
            return;
        }

        if (notification.Permissions.HasFlag(SessionPermission.ViewScreen)
            && await WaitForScreenSharingSlotAsync(notification.SessionId, cancellationToken) is { } busyWith)
        {
            await DeclineBusyAsync(notification.SessionId, notification.FromDeviceId, busyWith, cancellationToken);
            return;
        }

        SupportInvitationAuthorization? supportAuthorization = null;
        if (notification.AccessKind == SessionAccessKind.SupportInvitation)
        {
            if (_supportInvitations is null
                || string.IsNullOrEmpty(notification.SupportInvitationToken)
                || string.IsNullOrEmpty(notification.FromKeyFingerprint))
            {
                await _signaling.SendPermissionDecisionAsync(
                    notification.SessionId,
                    PermissionDecision.Decline,
                    cancellationToken).WaitAsync(cancellationToken);
                return;
            }

            var validation = await _supportInvitations.ValidateAsync(
                notification.SupportInvitationToken,
                notification.SupportInvitationPassword,
                notification.FromDeviceId,
                notification.FromKeyFingerprint,
                notification.Mode,
                notification.Permissions,
                cancellationToken).WaitAsync(cancellationToken);
            if (!validation.IsValid)
            {
                await _signaling.SendPermissionDecisionAsync(
                    notification.SessionId,
                    PermissionDecision.Decline,
                    cancellationToken).WaitAsync(cancellationToken);
                return;
            }
            supportAuthorization = validation.Authorization;
        }

        var runtime = new SessionRuntime(
            notification.SessionId,
            SessionRole.Sharer,
            notification.FromDeviceId,
            notification.FromDisplayName,
            _time.GetUtcNow(),
            notification.Mode,
            notification.Permissions,
            notification.AccessKind,
            notification.FromKeyFingerprint,
            requestedProfile,
            notification.RemoteScopeSelectionRequired);
        runtime.FileRelayEnabled = notification.FileRelay;
        runtime.UnicodeInputEnabled = notification.UnicodeInput;

        lock (_gate)
        {
            if (_disposing) return;
            _sessions[notification.SessionId] = runtime;
        }

        _timeline.Record(notification.SessionId, "session.request.received", "Incoming session request received");

        Fire(runtime, SessionTrigger.PermissionRequestSent);
        await EnsureCloudStartQueuedAsync(runtime);
        Notify(runtime);

        var request = new PermissionRequest
        {
            SessionId = notification.SessionId,
            RequesterId = notification.FromDeviceId,
            RequesterDisplayName = notification.FromDisplayName,
            RequestedMode = notification.Mode,
            RequestedPermissions = notification.Permissions,
            AccessKind = notification.AccessKind,
            RequesterKeyFingerprint = notification.FromKeyFingerprint,
            SupportNote = supportAuthorization?.SupportNote,
            RemoteScopeSelectionRequired = notification.RemoteScopeSelectionRequired,
            RequestedAt = _time.GetUtcNow(),
            Timeout = _options.PermissionTimeout,
        };

        PermissionPromptResult response;
        if (notification.AccessKind == SessionAccessKind.Unattended)
        {
            PermissionDecision unattendedDecision;
            if (_unattendedAccess is null || string.IsNullOrEmpty(notification.FromKeyFingerprint))
            {
                unattendedDecision = PermissionDecision.Decline;
            }
            else
            {
                try
                {
                    var authentication = await _unattendedAccess.AuthenticateTrustedDeviceAsync(
                        notification.FromDeviceId,
                        notification.FromKeyFingerprint,
                        notification.Permissions).WaitAsync(cancellationToken);
                    if (authentication != UnattendedAuthenticationResult.Succeeded
                        && notification.UnattendedChallengeId is { } passwordChallengeId
                        && !string.IsNullOrEmpty(notification.UnattendedProof))
                    {
                        authentication = await _unattendedAccess.VerifyPasswordProofAsync(
                            notification.FromDeviceId,
                            notification.FromKeyFingerprint,
                            notification.Permissions,
                            passwordChallengeId,
                            notification.UnattendedProof).WaitAsync(cancellationToken);
                    }
                    unattendedDecision = authentication == UnattendedAuthenticationResult.Succeeded
                        ? PermissionDecision.Accept
                        : PermissionDecision.Decline;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Unattended authentication failed closed ({ErrorType})", ex.GetType().Name);
                    unattendedDecision = PermissionDecision.Decline;
                }
            }

            response = PermissionPromptResult.ForRequestedScope(unattendedDecision, request);
        }
        else
        {
            // Closing the runtime (the requester ended the request) withdraws the open dialog;
            // a late answer would only reach signaling as unknown_session.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runtime.PermissionPrompt = timeout;
            timeout.CancelAfter(_options.PermissionTimeout);
            try
            {
                response = await _permissionPrompt.AskForScopeAsync(request, timeout.Token)
                    .WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Silence is a decline, never an accept.
                response = new PermissionPromptResult(PermissionDecision.Timeout);
            }
            finally
            {
                runtime.PermissionPrompt = null;
            }

            if (runtime.PermissionWithdrawn || !IsRuntimeActive(runtime))
            {
                // Both sides use the same permission window, so the requester's own timeout and the
                // dialog deadline usually coincide; 0.9.87 then logged "ended by the requester",
                // PermissionTimeout and TimedOut=True for one request. Report the end reason.
                var endReason = runtime.CloseReason ?? SessionEndReason.EndedByViewer;
                if (endReason == SessionEndReason.PermissionTimeout || response.Decision == PermissionDecision.Timeout)
                    _log.LogInformation(
                        "Session {SessionId} request timed out without an answer ({Reason})",
                        notification.SessionId, SessionEndReason.PermissionTimeout);
                else
                    _log.LogInformation(
                        "Session {SessionId} request was withdrawn by the requester before an answer ({Reason})",
                        notification.SessionId, endReason);
                return;
            }
        }

        var decision = response.Decision;
        var grantedMode = notification.Mode;
        var grantedPermissions = notification.Permissions;
        if (decision == PermissionDecision.Accept
            && !TryResolveGrantedScope(
                notification.Mode,
                notification.Permissions,
                notification.AccessKind,
                notification.RemoteScopeSelectionRequired,
                response.GrantedMode,
                response.GrantedPermissions,
                out grantedMode,
                out grantedPermissions))
        {
            _log.LogWarning("Permission prompt returned an invalid granted scope for session {SessionId}",
                notification.SessionId);
            decision = PermissionDecision.Decline;
        }

        // A queued dialog can be answered after another screen-sharing session started: on
        // 0.9.87 LNK-363 accepted a second request while the first was still connecting and ran
        // two capture/encode pipelines on a 2-core laptop.
        if (decision == PermissionDecision.Accept
            && grantedPermissions.HasFlag(SessionPermission.ViewScreen)
            && await WaitForScreenSharingSlotAsync(notification.SessionId, cancellationToken) is { } activeShare)
        {
            await DeclineBusyAsync(notification.SessionId, notification.FromDeviceId, activeShare, cancellationToken);
            Fire(runtime, SessionTrigger.PermissionRefused);
            await CloseAsync(runtime, SessionEndReason.RemoteBusy);
            return;
        }

        if (decision == PermissionDecision.Accept
            && notification.AccessKind == SessionAccessKind.SupportInvitation
            && (supportAuthorization is null
                || _supportInvitations is null
                || !await _supportInvitations.TryConsumeApprovedAsync(
                    supportAuthorization,
                    cancellationToken).WaitAsync(cancellationToken)))
        {
            decision = PermissionDecision.Decline;
        }

        if (decision == PermissionDecision.Block)
        {
            await _blockedDevices.BlockAsync(notification.FromDeviceId, cancellationToken)
                .WaitAsync(cancellationToken);
        }

        if (decision == PermissionDecision.Accept && notification.RemoteScopeSelectionRequired)
        {
            runtime.ApplyGrantedScope(grantedMode, grantedPermissions);
            await _signaling.SendPermissionDecisionAsync(
                    notification.SessionId,
                    decision,
                    grantedMode,
                    grantedPermissions,
                    cancellationToken)
                .WaitAsync(cancellationToken);
        }
        else
        {
            await _signaling.SendPermissionDecisionAsync(notification.SessionId, decision, cancellationToken)
                .WaitAsync(cancellationToken);
        }

        if (decision != PermissionDecision.Accept)
        {
            var reason = decision switch
            {
                PermissionDecision.Block => SessionEndReason.PermissionBlocked,
                PermissionDecision.Timeout => SessionEndReason.PermissionTimeout,
                _ => SessionEndReason.PermissionDeclined,
            };

            Fire(runtime, SessionTrigger.PermissionRefused);
            await CloseAsync(runtime, reason);
            return;
        }

        Fire(runtime, SessionTrigger.PermissionAccepted);
        runtime.PermissionAccepted = true;
        _timeline.Record(runtime.SessionId, "session.permission.accepted", "Permission accepted");
        Notify(runtime);

        try
        {
            if (!IsRuntimeActive(runtime)) return;
            await StartSharingAsync(runtime, cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || runtime.IsEnding || runtime.IsReleased)
        {
            if (cancellationToken.IsCancellationRequested) throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start sharing for session {SessionId}", runtime.SessionId);
            await FailAsync(runtime, SessionEndReason.ProtocolError);
        }
    }

    private async Task StartSharingAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            runtime.LifecycleToken);
        var operationToken = operationCancellation.Token;
        await runtime.LifecycleGate.WaitAsync(operationToken);
        try
        {
            operationToken.ThrowIfCancellationRequested();
            if (!IsRuntimeActive(runtime)) return;

            var profile = runtime.MediaProfile;
            var iceConfiguration = await _signaling.GetIceConfigurationAsync(
                runtime.SessionId,
                operationToken).WaitAsync(operationToken);
            operationToken.ThrowIfCancellationRequested();

            IScreenCaptureSource? capture = null;
            CaptureRequest? captureRequest = null;
            if (runtime.Permissions.HasFlag(SessionPermission.ViewScreen))
            {
                var target = PreferredCaptureTarget
                             ?? throw new InvalidOperationException(
                                 "No capture target was selected. PeerOnQ never captures the desktop implicitly.");
                capture = _captureFactory();
                runtime.Capture = capture;
                runtime.CaptureStoppedHandler = (_, reason) => OnCaptureStopped(runtime, capture, reason);
                capture.CaptureStopped += runtime.CaptureStoppedHandler;

                captureRequest = new CaptureRequest
                {
                    Target = target,
                    // Full Control already renders the viewer's immediate local pointer. Capturing
                    // the sharer's cursor too produces a second, latency-delayed cursor image.
                    IncludeCursor = profile.IncludeCursor
                        && !runtime.Permissions.HasFlag(SessionPermission.ControlInput),
                    MaxFramesPerSecond = profile.TargetFps,
                    Resolution = profile.Resolution,
                };
            }

            operationToken.ThrowIfCancellationRequested();
            var media = await _mediaEngine.CreateSharerSessionAsync(
                runtime.SessionId,
                capture,
                profile,
                iceConfiguration,
                runtime.Permissions,
                operationToken).WaitAsync(operationToken);
            operationToken.ThrowIfCancellationRequested();
            var pendingSignals = AttachMedia(runtime, media);
            if (capture is not null && captureRequest is not null)
            {
                // Windows.Graphics.Capture can emit its initial frame immediately and then stay
                // quiet while a desktop is static. Start only after the media session subscribed
                // so that first frame cannot disappear in the capture/media hand-off gap.
                await capture.StartAsync(captureRequest, operationToken).WaitAsync(operationToken);

                // Arm the display mapping before the offer can reach the viewer. The peer may
                // otherwise request input focus as soon as its data channel is ready, before this
                // host's media-connected callback sets the target. SetCaptureTarget keeps
                // injection disabled; the acknowledged focus request still enables it explicitly.
                if (runtime.Permissions.HasFlag(SessionPermission.ControlInput))
                {
                    var activeTarget = capture.Target
                        ?? throw new InvalidOperationException(
                            "Full Control capture did not expose an active input target.");
                    _remoteInputFocus.ExecuteIfUnowned(() => _remoteInputSink!.SetCaptureTarget(activeTarget));
                }
            }
            await ApplyPendingNegotiationSignalsAsync(runtime, media, pendingSignals, operationToken);

            var offer = await media.CreateOfferAsync(operationToken).WaitAsync(operationToken);
            await _signaling.SendSdpAsync(runtime.SessionId, "offer", offer, operationToken)
                .WaitAsync(operationToken);

            runtime.ArmTimeout(_options.NegotiationTimeout, () => TimeoutAsync(runtime, SessionEndReason.NegotiationTimeout));

            if (capture is not null) await PublishDisplaysAsync(runtime, operationToken);

            await _auditLog.RecordAsync(new SessionAuditEntry
            {
                SessionId = runtime.SessionId,
                Role = runtime.Role,
                PeerMaskedId = runtime.PeerId.Masked,
                PeerDisplayName = runtime.PeerDisplayName,
                Mode = runtime.Mode,
                StartedAt = runtime.StartedAt,
            }, operationToken).WaitAsync(operationToken);
            runtime.SessionStartAudited = true;
        }
        finally
        {
            runtime.LifecycleGate.Release();
        }
    }

    /// <summary>
    /// Viewer: ask the sharer to switch display. The sharer decides whether to honour it and
    /// can only ever switch between its own screens, so this grants no new capability.
    /// </summary>
    public async Task RequestRemoteDisplayAsync(
        SessionId sessionId, string displayId, CancellationToken cancellationToken = default)
    {
        var runtime = Find(sessionId);
        if (runtime is null || runtime.Role != SessionRole.Viewer) return;
        if (IsSignalingHeld(runtime))
        {
            _log.LogInformation(
                "Display switch for session {SessionId} is unavailable until signaling owns the session again",
                sessionId);
            return;
        }

        await _signaling.RequestDisplayAsync(sessionId, displayId, cancellationToken);
    }

    private void OnRemoteDisplays(object? sender, RemoteDisplaysNotification notification)
    {
        var runtime = Find(notification.SessionId);
        if (runtime is null) return;

        // The sharer publishes its displays right after the offer, before the viewer UI exists.
        // Keep the newest list so a viewer opened later still shows every remote monitor.
        lock (_gate) runtime.RemoteDisplays = notification;
        RemoteDisplaysChanged?.Invoke(this, notification);
    }

    /// <summary>Viewer: the newest display list the sharer published for this session.</summary>
    public RemoteDisplaysNotification? RemoteDisplaysOf(SessionId id)
    {
        lock (_gate)
            return _sessions.TryGetValue(id, out var runtime) ? runtime.RemoteDisplays : null;
    }

    /// <summary>
    /// Viewer: asks the sharer for a fresh key frame, e.g. when the viewer window opens after the
    /// first frames were decoded; a static remote desktop would otherwise send nothing new.
    /// </summary>
    public void RequestKeyFrame(SessionId id)
    {
        IMediaSession? media;
        lock (_gate) media = _sessions.TryGetValue(id, out var runtime) ? runtime.Media : null;
        media?.RequestKeyFrame();
    }

    private async void OnDisplaySelectionRequested(object? sender, SelectDisplayNotification notification)
    {
        var runtime = Find(notification.SessionId);

        // Only a sharer with a live capture may act on this.
        if (runtime is null || runtime.Role != SessionRole.Sharer) return;
        if (runtime.Capture is not IAdaptiveCaptureSource adaptive) return;

        try
        {
            // The id must be one of this device's own displays; anything else is ignored.
            var target = adaptive.AvailableTargets.FirstOrDefault(t => t.Id == notification.DisplayId);
            if (target is null)
            {
                _log.LogWarning("Ignoring a request for unknown display {DisplayId}", notification.DisplayId);
                return;
            }

            _log.LogInformation("Switching shared display to {Display}", target.DisplayName);
            await adaptive.SwitchTargetAsync(target);
            // A new display is probed from the start level; it must not inherit the old one's 10 fps.
            runtime.Media?.RestartQuality();
            PreferredCaptureTarget = target;

            await PublishDisplaysAsync(runtime);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not switch the shared display");
            if (runtime.Capture?.IsCapturing == false)
                OnCaptureStopped(runtime, runtime.Capture, CaptureStoppedReason.Error);
        }
    }

    private void OnCaptureStopped(SessionRuntime runtime, IScreenCaptureSource capture, CaptureStoppedReason reason)
    {
        if (!ReferenceEquals(runtime.Capture, capture) || !IsRuntimeActive(runtime)) return;

        // Disable synchronously: teardown may wait for a startup/media operation, but a frozen
        // or unavailable screen must never leave its remote input route active during that wait.
        runtime.BeginEnding();
        runtime.Collaboration?.ConnectionInterrupted();
        DisablePlatformInput(runtime);
        _timeline.Record(runtime.SessionId, "capture.stopped", $"Screen capture stopped ({reason})");
        RunStartupOperation(
            _ => FailAsync(runtime, SessionEndReason.MediaLost),
            "Failed to end a session after screen capture stopped");
    }

    /// <summary>Sharer: tell the viewer which displays exist and which one is live.</summary>
    private async Task PublishDisplaysAsync(
        SessionRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        if (runtime.Capture is not IAdaptiveCaptureSource adaptive) return;
        if (IsSignalingHeld(runtime))
        {
            // Sent after the session is resumed on signaling.
            runtime.DisplaysPublishPending = true;
            return;
        }

        var active = runtime.Capture.Target?.Id ?? PreferredCaptureTarget?.Id ?? string.Empty;

        try
        {
            await _signaling.SendDisplaysAsync(
                runtime.SessionId,
                adaptive.AvailableTargets,
                active,
                cancellationToken).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not publish the display list");
        }
    }

    // ------------------------------------------------------------------ signaling events

    private void OnPermissionResolved(object? sender, PermissionResultNotification notification) =>
        RunStartupOperation(
            cancellationToken => HandlePermissionResolvedAsync(notification, cancellationToken),
            $"Failed to continue session {notification.SessionId} after permission");

    private async Task HandlePermissionResolvedAsync(
        PermissionResultNotification notification,
        CancellationToken cancellationToken)
    {
        var runtime = Find(notification.SessionId);
        if (runtime is null) return;

        try
        {
            // The peer answered, so the request reached it: the session exists for the cloud now.
            await EnsureCloudStartQueuedAsync(runtime);
            if (notification.Decision != PermissionDecision.Accept)
            {
                var busy = notification.Decision == PermissionDecision.Decline
                           && string.Equals(notification.DeclineReason, PermissionDeclineReasons.Busy, StringComparison.Ordinal);
                if (busy)
                    _log.LogInformation(
                        "Session {SessionId} was declined because the remote device is already sharing its screen in another session",
                        runtime.SessionId);
                Fire(runtime, SessionTrigger.PermissionRefused);
                await CloseAsync(runtime, notification.Decision switch
                {
                    PermissionDecision.Block => SessionEndReason.PermissionBlocked,
                    PermissionDecision.Timeout => SessionEndReason.PermissionTimeout,
                    _ when busy => SessionEndReason.RemoteBusy,
                    _ => SessionEndReason.PermissionDeclined,
                });
                return;
            }

            runtime.PeerDisplayName = notification.PeerDisplayName ?? runtime.PeerDisplayName;
            runtime.PeerKeyFingerprint = notification.PeerKeyFingerprint;
            runtime.FileRelayEnabled = notification.FileRelay;
            runtime.UnicodeInputEnabled = notification.UnicodeInput;
            if (!TryResolveGrantedScope(
                    runtime.Mode,
                    runtime.Permissions,
                    runtime.AccessKind,
                    runtime.RemoteScopeSelectionRequired,
                    notification.GrantedMode,
                    notification.GrantedPermissions,
                    out var grantedMode,
                    out var grantedPermissions))
            {
                throw new InvalidOperationException(
                    "The signaling server did not confirm a valid remote-selected session scope.");
            }
            runtime.ApplyGrantedScope(grantedMode, grantedPermissions);
            Fire(runtime, SessionTrigger.PermissionAccepted);
            runtime.PermissionAccepted = true;
            _timeline.Record(runtime.SessionId, "session.permission.accepted", "Permission accepted");
            Notify(runtime);

            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                runtime.LifecycleToken);
            var operationToken = operationCancellation.Token;
            await runtime.LifecycleGate.WaitAsync(operationToken);
            try
            {
                operationToken.ThrowIfCancellationRequested();
                if (!IsRuntimeActive(runtime)) return;

                // The viewer waits for the sharer's offer; arm the negotiation timeout now.
                runtime.ArmTimeout(
                    _options.NegotiationTimeout,
                    () => TimeoutAsync(runtime, SessionEndReason.NegotiationTimeout));

                var iceConfiguration = await _signaling.GetIceConfigurationAsync(
                    runtime.SessionId,
                    operationToken).WaitAsync(operationToken);
                var media = await _mediaEngine.CreateViewerSessionAsync(
                    runtime.SessionId,
                    iceConfiguration,
                    runtime.Permissions,
                    operationToken).WaitAsync(operationToken);
                operationToken.ThrowIfCancellationRequested();
                var pendingSignals = AttachMedia(runtime, media);
                await ApplyPendingNegotiationSignalsAsync(runtime, media, pendingSignals, operationToken);
            }
            finally
            {
                runtime.LifecycleGate.Release();
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || runtime.IsReleased)
        {
            // Coordinator/session teardown owns cleanup after cancelling initialization.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to continue session {SessionId} after permission", notification.SessionId);
            await FailAsync(runtime, SessionEndReason.ProtocolError);
        }
    }

    private void OnSdpReceived(object? sender, SdpNotification notification) =>
        _ = HandleSdpReceivedAsync(notification, _lifetimeCancellation.Token);

    private async Task HandleSdpReceivedAsync(
        SdpNotification notification,
        CancellationToken cancellationToken)
    {
        var runtime = Find(notification.SessionId);
        if (runtime is null) return;

        try
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                runtime.LifecycleToken);
            var operationToken = operationCancellation.Token;
            await runtime.LifecycleGate.WaitAsync(operationToken);
            try
            {
                if (!IsRuntimeActive(runtime)) return;
                if (runtime.Media is null)
                {
                    if (!runtime.TryQueuePendingSignal(new PendingSdpSignal(notification)))
                    {
                        throw new InvalidOperationException(
                            "Too many negotiation messages arrived before media initialization.");
                    }

                    _log.LogDebug(
                        "Queued early {SdpType} until media is ready for session {SessionId}",
                        notification.SdpType,
                        notification.SessionId);
                    return;
                }

                await ApplySdpAsync(runtime, runtime.Media, notification, operationToken);
            }
            finally
            {
                runtime.LifecycleGate.Release();
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || runtime.IsReleased)
        {
            // Coordinator/session teardown owns cleanup after cancelling signaling work.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SDP handling failed for session {SessionId}", notification.SessionId);
            await FailAsync(runtime, SessionEndReason.ProtocolError);
        }
    }

    private void OnIceReceived(object? sender, IceNotification notification) =>
        _ = HandleIceReceivedAsync(notification, _lifetimeCancellation.Token);

    private async Task HandleIceReceivedAsync(
        IceNotification notification,
        CancellationToken cancellationToken)
    {
        var runtime = Find(notification.SessionId);
        if (runtime is null) return;

        try
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                runtime.LifecycleToken);
            var operationToken = operationCancellation.Token;
            await runtime.LifecycleGate.WaitAsync(operationToken);
            try
            {
                if (!IsRuntimeActive(runtime)) return;
                if (runtime.Media is null)
                {
                    if (!runtime.TryQueuePendingSignal(new PendingIceSignal(notification)))
                    {
                        _log.LogWarning(
                            "Discarding excess early ICE for session {SessionId}",
                            notification.SessionId);
                    }

                    return;
                }

                await ApplyIceAsync(runtime.Media, notification, operationToken);
            }
            finally
            {
                runtime.LifecycleGate.Release();
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || runtime.IsReleased)
        {
            // Coordinator/session teardown owns cleanup after cancelling signaling work.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Discarding an unusable ICE candidate for session {SessionId}", notification.SessionId);
        }
    }

    private async void OnSessionEnded(object? sender, SessionEndedNotification notification)
    {
        var runtime = Find(notification.SessionId);
        if (runtime is null) return;

        // The server sweeps a session whose signaling ownership was not restored in time. That
        // says nothing about the media path: in the 0.9.86 restart test video was flowing when
        // the sweep ended both sides. Keep a connected peer-to-peer session; a media loss ends it.
        if (notification.Reason == SessionEndReason.ReconnectFailed
            && runtime.Machine.State == SessionState.ConnectedViewOnly
            && runtime.Media?.State == MediaConnectionState.Connected)
        {
            DetachFromSignaling(runtime, "the server ended its signaling state (ReconnectFailed)");
            return;
        }

        await CloseAsync(runtime, notification.Reason);
    }

    // ------------------------------------------------------------------ media wiring

    private PendingNegotiationSignal[] AttachMedia(SessionRuntime runtime, IMediaSession media)
    {
        var pendingSignals = runtime.AttachMedia(media);

        ICollaborationTransport transport;
        if (_collaborationTransportFactory is not null)
        {
            transport = _collaborationTransportFactory(
                media,
                runtime.Permissions,
                runtime.PeerKeyFingerprint ?? string.Empty);
        }
        else
        {
            if (_hybridIdentity is null)
                throw new InvalidOperationException("The mandatory hybrid device identity provider is not configured.");
            if (string.IsNullOrWhiteSpace(runtime.PeerKeyFingerprint))
                throw new InvalidOperationException("The signaling-bound peer identity fingerprint is missing.");
            transport = new MediaCollaborationTransport(
                media,
                runtime.Permissions,
                _hybridIdentity,
                runtime.PeerKeyFingerprint,
                fileRelay: runtime.FileRelayEnabled ? _fileRelaySignaling : null,
                nativeBulkTransportFactory: _nativeBulkTransportFactory);
        }
        {
            var fileTransfers = runtime.Permissions.HasFlag(SessionPermission.FileTransfer)
                ? new FileTransferService(transport, malwareScanner: _malwareScanner, audit: _securityAudit)
                : null;
            var clipboard = runtime.Permissions.HasFlag(SessionPermission.ClipboardText) && _clipboardFactory is not null
                ? new ClipboardSyncService(transport, _clipboardFactory(), audit: _securityAudit)
                : null;
            var remoteInput = runtime.Permissions.HasFlag(SessionPermission.ControlInput)
                ? new RemoteInputSession(
                    runtime.SessionId,
                    runtime.Role,
                    transport,
                    runtime.Role == SessionRole.Sharer ? _remoteInputSink : null,
                    runtime.Role == SessionRole.Sharer ? _remoteInputFocus : null,
                    captureTarget: runtime.Role == SessionRole.Sharer
                        ? () => !runtime.IsEnding
                                && runtime.EffectivePermissions.HasFlag(SessionPermission.ControlInput)
                            ? runtime.Capture?.Target : null
                        : null,
                    unicodeInputEnabled: runtime.UnicodeInputEnabled)
                : null;
            if (remoteInput is not null)
            {
                remoteInput.Warning += (_, warningCode) =>
                    _log.LogWarning(
                        "Remote input warning {WarningCode} for session {SessionId}",
                        warningCode,
                        runtime.SessionId);
            }
            runtime.Collaboration = new SessionCollaborationContext(
                runtime.SessionId,
                runtime.Permissions,
                transport,
                fileTransfers,
                clipboard,
                remoteInput,
                runtime.Permissions.HasFlag(SessionPermission.SystemAudio)
                    ? new SystemAudioSession(media, runtime.Permissions,
                        runtime.Role == SessionRole.Sharer ? _systemAudioCaptureFactory : null,
                        runtime.Role == SessionRole.Viewer ? _systemAudioPlaybackFactory : null,
                        _time)
                    : null);
            if (fileTransfers is not null)
            {
                // Start every authorized file-transfer session with the largest bounded window and
                // provisional bulk ceiling. Media telemetry reduces file traffic first, while a
                // capable link can still meet the high-throughput transfer target.
                runtime.Collaboration.SetTransferPriorityMode(TransferPriorityMode.FileTransferPriority);
            }
            runtime.Collaboration.Ready += async (_, _) =>
            {
                if (!IsRuntimeActive(runtime) || media.State != MediaConnectionState.Connected) return;
                try { await CompleteSecureConnectionAsync(runtime, media); }
                catch (Exception ex) { await FailSecureConnectionAsync(runtime, ex); }
            };
            runtime.Collaboration.ProtocolError += async (_, reason) =>
            {
                if (!IsRuntimeActive(runtime)) return;
                _log.LogWarning(
                    "Secure session protocol failed for session {SessionId}: {Reason}",
                    runtime.SessionId,
                    reason);
                await FailAsync(runtime, SessionEndReason.AuthenticationMismatch);
            };
            CollaborationAvailable?.Invoke(this, runtime.Collaboration);
        }

        media.LocalIceCandidate += async (_, candidate) =>
        {
            if (!IsRuntimeActive(runtime) || IsSignalingHeld(runtime)) return;
            try
            {
                await _signaling.SendIceAsync(
                    runtime.SessionId, candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not forward a local ICE candidate");
            }
        };

        media.RemoteFrameReceived += (_, frame) =>
        {
            if (!IsRuntimeActive(runtime)) return;
            SessionRemoteFrameReceived?.Invoke(this, new SessionRemoteFrame(runtime.SessionId, frame));
            RemoteFrameReceived?.Invoke(this, frame);
        };

        media.StateChanged += async (_, state) =>
        {
            if (!IsRuntimeActive(runtime)) return;
            switch (state)
            {
                case MediaConnectionState.Connected:
                    if (runtime.Collaboration?.IsReady == true)
                        await CompleteSecureConnectionAsync(runtime, media);
                    break;

                case MediaConnectionState.Disconnected:
                    runtime.SecureConnected = false;
                    runtime.Collaboration?.ConnectionInterrupted();
                    if (runtime.Machine.State == SessionState.ConnectedViewOnly)
                    {
                        Fire(runtime, SessionTrigger.ConnectionLost);
                        BeginReconnect(runtime, SessionEndReason.MediaLost.ToString());
                        Notify(runtime);
                    }
                    break;

                case MediaConnectionState.Failed:
                    runtime.SecureConnected = false;
                    runtime.Collaboration?.ConnectionInterrupted();
                    if (runtime.Machine.State == SessionState.ConnectedViewOnly)
                    {
                        Fire(runtime, SessionTrigger.ConnectionLost);
                        BeginReconnect(runtime, SessionEndReason.MediaLost.ToString());
                        Notify(runtime);
                    }
                    else if (runtime.Machine.State != SessionState.Reconnecting)
                    {
                        await FailAsync(runtime, SessionEndReason.MediaLost);
                    }
                    break;

                case MediaConnectionState.Closed when IsSignalingHeld(runtime):
                    // The peer closed its peer connection (DTLS close_notify). Its session end
                    // normally arrives over signaling; without a signaling owner this is the only
                    // notice, and the session would otherwise stay open on a dead connection.
                    _log.LogInformation(
                        "Session {SessionId} peer closed the peer-to-peer connection while signaling was unavailable",
                        runtime.SessionId);
                    await CloseAsync(runtime, runtime.Role == SessionRole.Viewer
                        ? SessionEndReason.EndedBySharer
                        : SessionEndReason.EndedByViewer);
                    break;
            }
        };

        return pendingSignals;
    }

    private async Task CompleteSecureConnectionAsync(SessionRuntime runtime, IMediaSession media)
    {
        await runtime.LifecycleGate.WaitAsync();
        try
        {
            if (!IsRuntimeActive(runtime)
                || runtime.Collaboration?.IsReady != true
                || runtime.SecureConnected)
                return;

            var security = runtime.Collaboration.Security;
            if (security is not { PeerAuthenticated: true, PostQuantumProtected: true })
                throw new InvalidOperationException("The mandatory hybrid secure session was not authenticated.");

            runtime.SecureConnected = true;
            var isResuming = runtime.Machine.State == SessionState.Reconnecting;
            if (isResuming)
            {
                _timeline.Record(runtime.SessionId, "security.renegotiated", "Hybrid security renegotiated");
            }
            else
            {
                _timeline.Record(runtime.SessionId, "identity.verified", "Device identity verified");
                _timeline.Record(runtime.SessionId, "security.negotiated", "Hybrid security negotiated");
            }
            var negotiatedPath = media.GetStatistics().ConnectionPath;
            _timeline.Record(
                runtime.SessionId,
                "connection.path.selected",
                negotiatedPath switch
                {
                    ConnectionPath.DirectLan => "Direct LAN path selected",
                    ConnectionPath.DirectInternet => "Direct internet path selected",
                    ConnectionPath.Relayed => "Relay path selected",
                    _ => "Connection path still negotiating",
                });
            runtime.RequiresIceRestart = false;
            runtime.CancelTimeout();
            if (isResuming) runtime.Collaboration.ConnectionResumed();
            if (runtime.Role == SessionRole.Sharer && _remoteInputSink is not null)
                RestorePlatformInput(runtime);
            if (_qualitySignaling is not null)
                runtime.StartQualityPublisher(ct => PublishQualityAsync(runtime, ct));
            if (isResuming)
            {
                Fire(runtime, SessionTrigger.Reconnected);
                MarkResumedCore(runtime, restoreInput: _remoteInputSink is null);
            }
            else
            {
                if (runtime.Machine.State != SessionState.ConnectedViewOnly)
                    Fire(runtime, SessionTrigger.MediaConnected);
                Notify(runtime);
            }
        }
        finally
        {
            runtime.LifecycleGate.Release();
        }

        if (!IsRuntimeActive(runtime)) return;
        if (runtime.Role == SessionRole.Sharer && runtime.Collaboration?.SystemAudio is { } audio
            && !runtime.InitialAudioCaptureStarted)
        {
            runtime.InitialAudioCaptureStarted = true;
            _ = StartApprovedSystemAudioAsync(runtime, audio);
        }
        if (!runtime.SecurityStartAudited)
        {
            runtime.SecurityStartAudited = true;
            await AppendSecurityAuditAsync(new SecurityAuditEvent
            {
                EventId = Guid.NewGuid(),
                EventType = SecurityAuditEventType.SessionStarted,
                OccurredAt = _time.GetUtcNow(),
                SessionId = runtime.SessionId.ToString(),
                PeerMaskedId = runtime.PeerId.Masked,
                PermissionSet = runtime.Permissions,
                Outcome = "hybrid_pq_authenticated",
                NetworkPath = TryGetNetworkPath(media),
                IntegrityMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["access_kind"] = runtime.AccessKind.ToString(),
                    ["role"] = runtime.Role.ToString(),
                    ["secure_protocol"] = SecureSessionProtocol.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["handshake_suite"] = SecureSessionProtocol.HandshakeSuite,
                },
            });
        }
        if (!runtime.CloudConnectedQueued)
        {
            runtime.CloudConnectedQueued = true;
            await EnsureCloudStartQueuedAsync(runtime);
            await QueueClientSessionEventAsync(runtime, ClientSessionEventKind.Connected);
        }
    }

    private async Task FailSecureConnectionAsync(SessionRuntime runtime, Exception exception)
    {
        _log.LogWarning(exception, "Mandatory secure session establishment failed for session {SessionId}", runtime.SessionId);
        if (IsRuntimeActive(runtime))
            await FailAsync(runtime, SessionEndReason.AuthenticationMismatch);
    }

    private async Task StartApprovedSystemAudioAsync(SessionRuntime runtime, SystemAudioSession audio)
    {
        try { await audio.SetCaptureEnabledAsync(true, runtime.LifecycleToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException
            or System.Runtime.InteropServices.COMException)
        {
            audio.RevokeLocalPermission();
            _log.LogWarning("System sound did not start for session {SessionId}; sound remains disabled", runtime.SessionId);
        }
    }

    private async Task ApplyPendingNegotiationSignalsAsync(
        SessionRuntime runtime,
        IMediaSession media,
        IReadOnlyList<PendingNegotiationSignal> pendingSignals,
        CancellationToken cancellationToken)
    {
        foreach (var pending in pendingSignals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRuntimeActive(runtime)) return;

            switch (pending)
            {
                case PendingSdpSignal sdp:
                    await ApplySdpAsync(runtime, media, sdp.Notification, cancellationToken);
                    break;

                case PendingIceSignal ice:
                    try
                    {
                        await ApplyIceAsync(media, ice.Notification, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning(
                            ex,
                            "Discarding an unusable queued ICE candidate for session {SessionId}",
                            runtime.SessionId);
                    }
                    break;
            }
        }
    }

    private async Task ApplySdpAsync(
        SessionRuntime runtime,
        IMediaSession media,
        SdpNotification notification,
        CancellationToken cancellationToken)
    {
        if (notification.SdpType == "offer" && runtime.Role == SessionRole.Viewer)
        {
            // After the first negotiation the sharer only offers for an ICE restart, and it
            // interrupted its input session (a new input generation) before doing so. A viewer
            // whose own media stayed connected answered silently on 0.9.87 and kept the old
            // generation, so the sharer then refused its input (input_session_binding_rejected).
            var peerInitiatedRestart = runtime.SecureConnected
                                       && !runtime.RequiresIceRestart
                                       && runtime.Machine.State == SessionState.ConnectedViewOnly;
            if (peerInitiatedRestart) runtime.Collaboration?.ConnectionInterrupted();
            var answer = await media.CreateAnswerAsync(notification.Sdp, cancellationToken)
                .WaitAsync(cancellationToken);
            await _signaling.SendSdpAsync(runtime.SessionId, "answer", answer, cancellationToken)
                .WaitAsync(cancellationToken);
            if (runtime.RequiresIceRestart)
            {
                _log.LogInformation("Session {SessionId} answered the sharer's ICE-restart offer", runtime.SessionId);
            }
            else if (peerInitiatedRestart)
            {
                _log.LogInformation(
                    "Session {SessionId} answered the sharer's ICE-restart offer while its own media stayed {MediaState}",
                    runtime.SessionId,
                    media.State);
                runtime.Collaboration?.ConnectionResumed();
            }
            CompleteIceRestart(runtime);
            if (runtime.Machine.State == SessionState.Negotiating)
            {
                Fire(runtime, SessionTrigger.NegotiationCompleted);
                runtime.ArmTimeout(_options.ConnectTimeout,
                    () => TimeoutAsync(runtime, SessionEndReason.ConnectTimeout));
            }
            Notify(runtime);
        }
        else if (notification.SdpType == "answer" && runtime.Role == SessionRole.Sharer)
        {
            await media.ApplyRemoteAnswerAsync(notification.Sdp, cancellationToken)
                .WaitAsync(cancellationToken);
            if (runtime.RequiresIceRestart)
                _log.LogInformation("Session {SessionId} received the viewer's ICE-restart answer", runtime.SessionId);
            CompleteIceRestart(runtime);
            if (runtime.Machine.State == SessionState.Negotiating)
            {
                Fire(runtime, SessionTrigger.NegotiationCompleted);
                runtime.ArmTimeout(_options.ConnectTimeout,
                    () => TimeoutAsync(runtime, SessionEndReason.ConnectTimeout));
            }
            Notify(runtime);
        }
        else
        {
            _log.LogWarning("Ignoring {SdpType} for a {Role} session", notification.SdpType, runtime.Role);
        }
    }

    private static Task ApplyIceAsync(
        IMediaSession media,
        IceNotification notification,
        CancellationToken cancellationToken) =>
        media.AddRemoteIceCandidateAsync(
            notification.Candidate,
            notification.SdpMid,
            notification.SdpMLineIndex,
            cancellationToken).WaitAsync(cancellationToken);

    private async void OnUnattendedChallengeRequested(
        object? sender,
        UnattendedChallengeRequestNotification notification)
    {
        if (_unattendedAccess is null || _unattendedSignaling is null) return;
        try
        {
            var challenge = await _unattendedAccess.IssuePasswordChallengeAsync(notification);
            await _unattendedSignaling.SendUnattendedChallengeResponseAsync(
                notification.RequestId,
                notification.RequesterId,
                challenge);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not answer an unattended password challenge");
        }
    }

    private void OnQualityReceived(object? sender, SessionQualityNotification notification)
    {
        IMediaSession? sharerMedia = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(notification.SessionId, out var runtime))
            {
                if (runtime.Role == SessionRole.Viewer)
                    runtime.RemoteQuality = notification;
                else if (runtime.Role == SessionRole.Sharer)
                    sharerMedia = runtime.Media;
            }
        }

        sharerMedia?.ReportRemoteQualityFeedback(new RemoteSessionQualityFeedback(
            notification.DecodeFps,
            notification.RenderFps,
            notification.DecodeToRenderLatencyP95Ms,
            notification.CaptureToPresentLatencyP95Ms,
            notification.FrameAgeClockUncertaintyMs,
            notification.InputToInjectionLatencyP95Ms,
            notification.InputClockUncertaintyMs));
    }

    private async Task PublishQualityAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(QualityPublishInterval, _time);
        var lastAttempt = 0L;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    // Until the session is owned again every report is refused with
                    // not_session_participant (0.9.86: ~2.5 errors a second); hold them meanwhile.
                    if (_qualitySignaling is null
                        || _signaling.State != SignalingConnectionState.Registered
                        || runtime.Media is null
                        || IsSignalingHeld(runtime))
                    {
                        continue;
                    }

                    var now = _time.GetTimestamp();
                    var interval = GetQualityPublishInterval(ActiveSessionCount());
                    if (lastAttempt > 0 && _time.GetElapsedTime(lastAttempt, now) < interval)
                        continue;
                    lastAttempt = now;

                    var statistics = runtime.Media.GetStatistics();
                    await _qualitySignaling.SendQualityAsync(new SessionQualityNotification(
                        runtime.SessionId,
                        statistics.CaptureFps,
                        statistics.EncodeFps,
                        statistics.PacketLossPercent,
                        statistics.JitterMs,
                        statistics.AvailableOutgoingBitrateKbps,
                        statistics.FramesDropped,
                        statistics.SourceWidth,
                        statistics.SourceHeight,
                        statistics.RequestedWidth,
                        statistics.RequestedHeight,
                        statistics.EncodedWidth,
                        statistics.EncodedHeight,
                        statistics.EncoderName,
                        statistics.EncoderHardwareAccelerated,
                        statistics.ConnectionHealth,
                        statistics.ActiveQualityProfile,
                        statistics.AdaptiveQualityLevel,
                        statistics.QualityChangeReason,
                        statistics.TargetFps,
                        statistics.TargetBitrateKbps,
                        statistics.EncoderQueueDepth,
                        statistics.DecodeFps,
                        statistics.RenderFps,
                        statistics.DecodeToRenderLatencyP95Ms,
                        statistics.CaptureToPresentLatencyP95Ms,
                        statistics.FrameAgeClockUncertaintyMs,
                        statistics.InputToInjectionLatencyP95Ms,
                        statistics.InputClockUncertaintyMs,
                        statistics.ConnectionPath.ToString()), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Quality telemetry tick failed; the active publisher will retry");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
    }

    private int ActiveSessionCount()
    {
        lock (_gate) return Math.Max(1, _sessions.Count);
    }

    // ------------------------------------------------------------------ reconnect

    private void OnSignalingStateChanged(object? sender, SignalingConnectionState state)
    {
        if (state != SignalingConnectionState.Reconnecting) return;

        SessionRuntime[] active;
        lock (_gate)
        {
            active = _sessions.Values
                .Where(runtime => runtime.Machine.State is
                    SessionState.ConnectedViewOnly or SessionState.Reconnecting)
                .ToArray();
        }

        foreach (var runtime in active)
        {
            runtime.SignalingInterrupted = true;
            if (runtime.Machine.State == SessionState.ConnectedViewOnly
                && runtime.Media?.State == MediaConnectionState.Connected)
            {
                // Media is peer-to-peer and does not need the server. A signaling outage (server
                // restart or redeploy) used to tear a healthy DirectLan session down in 4.6 s; now
                // video and input continue and the session resumes on signaling when it returns.
                // If media is lost meanwhile, its own handler starts the full reconnect.
                _log.LogWarning(
                    "Session {SessionId} lost its signaling connection; peer-to-peer media stays up while signaling reconnects",
                    runtime.SessionId);
                _timeline.Record(runtime.SessionId, "signaling.interrupted", "Server connection lost; media continues");
                runtime.StartSignalingResume(ct => ResumeSignalingAsync(runtime, ct));
                Notify(runtime);
                continue;
            }

            runtime.RequiresIceRestart = true;
            runtime.Collaboration?.ConnectionInterrupted();
            if (runtime.Machine.State == SessionState.ConnectedViewOnly)
            {
                Fire(runtime, SessionTrigger.ConnectionLost);
                BeginReconnect(runtime, SessionEndReason.SignalingLost.ToString());
            }
            Notify(runtime);
        }
    }

    /// <summary>
    /// Waits, paced and without an attempt budget, for signaling to return while media stays up,
    /// then resumes the session's signaling ownership. A server that no longer knows the session
    /// leaves it running peer-to-peer (it can no longer renegotiate; a later media loss ends it).
    /// </summary>
    private async Task ResumeSignalingAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        var interruptedAt = _time.GetUtcNow();
        var waits = 0;
        var refusals = 0;
        try
        {
            while (IsRuntimeActive(runtime)
                   && runtime.SignalingInterrupted
                   && runtime.Machine.State == SessionState.ConnectedViewOnly)
            {
                if (_signaling.State == SignalingConnectionState.Registered)
                {
                    try
                    {
                        var result = _resumableSignaling is null
                            ? new SessionResumeResult(runtime.SessionId, true, "resume_not_supported", null)
                            : await _resumableSignaling.ResumeSessionAsync(
                                runtime.SessionId,
                                mediaConnected: runtime.Media?.State == MediaConnectionState.Connected,
                                cancellationToken);
                        if (result.Resumed)
                        {
                            runtime.SignalingInterrupted = false;
                            _log.LogInformation(
                                "Session {SessionId} resumed on signaling after {Seconds:0.0} s ({Reason}); media stayed up",
                                runtime.SessionId, (_time.GetUtcNow() - interruptedAt).TotalSeconds, result.Reason);
                            _timeline.Record(runtime.SessionId, "signaling.resumed", "Server connection restored");
                            await RepublishAfterResumeAsync(runtime, cancellationToken);
                            Notify(runtime);
                            return;
                        }

                        refusals++;
                        _log.LogWarning(
                            "Session {SessionId} could not be resumed on signaling ({Reason}, attempt {Attempt} of {Attempts})",
                            runtime.SessionId, result.Reason, refusals, MaxSignalingResumeAttempts);
                        // Without a token there is nothing to retry with; otherwise re-authenticate
                        // and try again while media stays up.
                        if (refusals >= MaxSignalingResumeAttempts
                            || string.Equals(result.Reason, "missing_or_expired_resume_token", StringComparison.Ordinal))
                        {
                            DetachFromSignaling(runtime, result.Reason);
                            return;
                        }

                        await ReauthenticateSignalingAsync(runtime, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogInformation(ex, "Session {SessionId} signaling resume will be retried", runtime.SessionId);
                    }
                }

                await Task.Delay(ReconnectDelay(++waits), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ending, or the full reconnect took over.
        }
    }

    /// <summary>Resume refusals tolerated (with re-authentication and backoff) before going peer-to-peer only.</summary>
    internal const int MaxSignalingResumeAttempts = 3;

    private async Task ReauthenticateSignalingAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        if (_resumableSignaling is null) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SignalingReauthenticationTimeout);
            await _resumableSignaling.ReauthenticateAsync(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log.LogInformation(ex, "Session {SessionId} signaling re-authentication before the next resume failed", runtime.SessionId);
        }
    }

    private static readonly TimeSpan SignalingReauthenticationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Signaling cannot take this session back, but its media is connected peer-to-peer. Keep it
    /// running, stop its session-scoped signaling (0.9.86 sent ~2.5 refused messages a second for
    /// 37 s) and tell the user; a later media loss still ends it.
    /// </summary>
    private void DetachFromSignaling(SessionRuntime runtime, string reason)
    {
        if (runtime.SignalingDetached) return;
        runtime.SignalingDetached = true;
        runtime.SignalingInterrupted = true;
        _log.LogWarning(
            "Session {SessionId} continues peer-to-peer without signaling ({Reason}); it cannot renegotiate until it ends",
            runtime.SessionId,
            reason);
        _timeline.Record(runtime.SessionId, "signaling.detached",
            "Server no longer tracks the session; it continues directly between the devices");
        Notify(runtime);
    }

    /// <summary>Messages held while the session had no signaling owner are sent once it is owned again.</summary>
    private async Task RepublishAfterResumeAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        if (runtime.Role != SessionRole.Sharer || !runtime.DisplaysPublishPending) return;
        runtime.DisplaysPublishPending = false;
        await PublishDisplaysAsync(runtime, cancellationToken);
    }

    /// <summary>Session-scoped signaling is held while this session has no signaling owner.</summary>
    private static bool IsSignalingHeld(SessionRuntime runtime) =>
        runtime.SignalingInterrupted || runtime.SignalingDetached;

    private void OnPeerResumed(object? sender, SessionPeerResumedNotification notification)
    {
        var runtime = Find(notification.SessionId);
        if (runtime is null) return;

        // A signaling restart leaves both peers' media up and both resume. On 0.9.87 the peer's
        // resume still interrupted this side's input session and ran an ICE restart, so the
        // viewer's focus requests were refused (stale_focus) right after a successful resume.
        if (notification.PeerMediaConnected
            && runtime.Machine.State == SessionState.ConnectedViewOnly
            && runtime.SecureConnected
            && runtime.Media?.State == MediaConnectionState.Connected)
        {
            _log.LogInformation(
                "Session {SessionId} peer resumed on signaling with its media still connected; keeping the connection without an ICE restart",
                runtime.SessionId);
            _timeline.Record(runtime.SessionId, "signaling.peer_resumed", "Peer reconnected to the server; media continued");
            return;
        }

        runtime.RequiresIceRestart = true;
        runtime.Collaboration?.ConnectionInterrupted();
        if (runtime.Machine.State == SessionState.ConnectedViewOnly)
        {
            Fire(runtime, SessionTrigger.ConnectionLost);
            BeginReconnect(runtime, "PeerResumed");
        }
        Notify(runtime);
    }

    private async void OnSignalingError(object? sender, SignalingErrorNotification notification)
    {
        if (notification.SessionId is { } erroredSession)
        {
            await EndRefusedRequestAsync(erroredSession, notification);
            return;
        }

        if (!string.Equals(notification.Code, "invalid_proof", StringComparison.Ordinal)) return;

        // A rejected identity proof ends every session that depends on this registration,
        // including one whose media stayed up while it waited for signaling to return.
        SessionRuntime[] reconnecting;
        lock (_gate)
        {
            reconnecting = _sessions.Values
                .Where(runtime => runtime.Machine.State == SessionState.Reconnecting
                                  || (runtime.SignalingInterrupted && runtime.Machine.State == SessionState.ConnectedViewOnly))
                .ToArray();
        }

        foreach (var runtime in reconnecting)
        {
            await FailReconnectAsync(runtime, SessionEndReason.AuthenticationMismatch);
        }
    }

    /// <summary>
    /// A successful session request is never answered with an error, so an error that answers one
    /// (target_is_self, target_offline, invalid id, policy, rate limit...) means signaling did not
    /// create the session. End the request at once; it never reached a peer, so nothing is
    /// reported to the cloud. 0.9.86 waited the full 30 s permission timeout instead.
    /// </summary>
    private async Task EndRefusedRequestAsync(SessionId sessionId, SignalingErrorNotification notification)
    {
        var runtime = Find(sessionId);
        if (runtime is null
            || runtime.Role != SessionRole.Viewer
            || runtime.PermissionAccepted
            || runtime.Machine.State is not (SessionState.ResolvingDevice
                or SessionState.RequestingPermission
                or SessionState.AwaitingPermission))
            return;

        runtime.CloudTelemetrySuppressed = true;
        var reason = notification.Code switch
        {
            "target_offline" => SessionEndReason.DeviceOffline,
            "organization_policy_denied" => SessionEndReason.PermissionDeclined,
            _ => SessionEndReason.ProtocolError,
        };
        _log.LogInformation(
            "Signaling refused the request for session {SessionId} ({Code}); ending it with {Reason}",
            sessionId,
            notification.Code,
            reason);
        _timeline.Record(sessionId, "session.request.refused", $"Signaling refused the request ({notification.Code})");
        runtime.RequestRefusal = notification.Message;
        try
        {
            if (runtime.Machine.CanFire(SessionTrigger.PermissionRefused))
                Fire(runtime, SessionTrigger.PermissionRefused);
            await CloseAsync(runtime, reason);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not end refused session request {SessionId}", sessionId);
        }
    }

    private void BeginReconnect(SessionRuntime runtime, string reason)
    {
        if (runtime.Reconnect.State != ReconnectState.Connected) return;

        runtime.LastInterruptionReason = reason;
        runtime.InterruptedAt = _time.GetUtcNow();
        runtime.RequiresIceRestart = true;
        _timeline.Record(runtime.SessionId, "connection.interrupted", "Network or media connection interrupted");
        _log.LogWarning("Session {SessionId} connection interrupted ({Reason}); reconnecting as {Role}", runtime.SessionId, reason, runtime.Role);
        runtime.Reconnect.Fire(ReconnectTrigger.ConnectionLost);
        runtime.Reconnect.Fire(ReconnectTrigger.RetryStarted);

        // This is deliberately first: uncertain transport must never leave a key/button held.
        DisablePlatformInput(runtime);

        runtime.StartReconnect(ct => RecoverAsync(runtime, ct));
    }

    private async Task RecoverAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            var signalingWaits = 0;
            for (var attempt = 1; attempt <= _options.MaxReconnectAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _timeline.Record(
                    runtime.SessionId,
                    "reconnect.attempt",
                    $"Reconnect attempt {attempt} started");

                var elapsed = _time.GetUtcNow() - runtime.InterruptedAt;
                _log.LogInformation(
                    "Session {SessionId} reconnect attempt {Attempt} after {Seconds:0.0} s (media {MediaState}, signaling {SignalingState})",
                    runtime.SessionId, attempt, elapsed.TotalSeconds, runtime.Media?.State, _signaling.State);
                if (elapsed >= _options.ResumeWithoutConfirmationLimit)
                {
                    await FailReconnectAsync(runtime, SessionEndReason.ResumeConfirmationRequired);
                    return;
                }
                if (elapsed >= _options.ReconnectTimeout)
                {
                    await FailReconnectAsync(runtime, SessionEndReason.ReconnectFailed);
                    return;
                }

                runtime.ReconnectAttempts = attempt;
                Notify(runtime);

                try
                {
                    if (_signaling.State != SignalingConnectionState.Registered)
                    {
                        if (runtime.Reconnect.State == ReconnectState.Renegotiating)
                            runtime.Reconnect.TryFire(ReconnectTrigger.RetryStarted, out _);
                        if (runtime.Reconnect.State == ReconnectState.Reconnecting)
                            runtime.Reconnect.TryFire(ReconnectTrigger.ReauthenticationRequired, out _);

                        var registered = await WaitForSignalingAsync(cancellationToken);
                        if (!registered)
                        {
                            // Signaling is down (server restart, network). A Faulted client returns at
                            // once, which used to burn all 20 attempts in milliseconds: wait out the
                            // backoff instead; the time limits above, not attempts, end the session.
                            signalingWaits++;
                            attempt--;
                            await Task.Delay(ReconnectDelay(signalingWaits), cancellationToken);
                            continue;
                        }
                    }

                    // A fast re-registration can complete before this loop observes the transient
                    // Reconnecting state. Resume whenever the interruption flag is set, not only in
                    // the branch that happened to wait for registration. A session signaling no
                    // longer tracks cannot be resumed or renegotiated; the time limits end it.
                    if (runtime.SignalingInterrupted && !runtime.SignalingDetached)
                    {
                        if (_resumableSignaling is not null)
                        {
                            var result = await _resumableSignaling.ResumeSessionAsync(
                                runtime.SessionId,
                                mediaConnected: runtime.Media?.State == MediaConnectionState.Connected
                                                && !runtime.RequiresIceRestart,
                                cancellationToken);
                            if (!result.Resumed)
                            {
                                await FailReconnectAsync(runtime, SessionEndReason.AuthenticationMismatch);
                                return;
                            }
                        }

                        runtime.SignalingInterrupted = false;
                        runtime.Reconnect.TryFire(ReconnectTrigger.Reauthenticated, out _);
                    }

                    if (runtime.Media?.State == MediaConnectionState.Connected
                        && !runtime.RequiresIceRestart)
                    {
                        await MarkResumedAsync(runtime);
                        return;
                    }

                    if (runtime.Reconnect.State == ReconnectState.Renegotiating)
                        runtime.Reconnect.TryFire(ReconnectTrigger.RetryStarted, out _);
                    runtime.Reconnect.TryFire(ReconnectTrigger.RenegotiationStarted, out _);

                    // The sharer remains the deterministic offerer. The viewer waits for that offer,
                    // preventing glare from two simultaneous ICE restarts.
                    if (runtime.SignalingDetached)
                    {
                        _log.LogInformation(
                            "Session {SessionId} has no signaling owner and cannot renegotiate (attempt {Attempt})",
                            runtime.SessionId, attempt);
                    }
                    else if (runtime.Role == SessionRole.Sharer && runtime.Media is not null)
                    {
                        var offer = await runtime.Media.RestartIceAsync(cancellationToken);
                        await _signaling.SendSdpAsync(runtime.SessionId, "offer", offer, cancellationToken);
                        _log.LogInformation("Session {SessionId} sent an ICE-restart offer (attempt {Attempt})", runtime.SessionId, attempt);
                    }
                    else if (runtime.Role == SessionRole.Viewer)
                    {
                        _log.LogInformation("Session {SessionId} waits for the sharer's ICE-restart offer (attempt {Attempt})", runtime.SessionId, attempt);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (SignalingAuthenticationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A Wi-Fi transition can reject the first resume/offer send before the socket
                    // state catches up. Keep the session fail-closed and use the bounded retry
                    // window instead of ending it on that one transient transport failure.
                    _log.LogInformation(
                        ex,
                        "Reconnect attempt {Attempt} was interrupted for session {SessionId}",
                        attempt,
                        runtime.SessionId);
                }

                var delay = ReconnectDelay(attempt);
                var remaining = _options.ReconnectTimeout - (_time.GetUtcNow() - runtime.InterruptedAt);
                if (remaining < delay) delay = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                await Task.Delay(delay, cancellationToken);

                if (runtime.Media?.State == MediaConnectionState.Connected
                    && !runtime.RequiresIceRestart)
                {
                    await MarkResumedAsync(runtime);
                    return;
                }

                runtime.Reconnect.TryFire(ReconnectTrigger.RetryStarted, out _);
            }

            await FailReconnectAsync(runtime, SessionEndReason.ReconnectFailed);
        }
        catch (OperationCanceledException)
        {
            // User cancelled or the session ended.
        }
        catch (SignalingAuthenticationException)
        {
            await FailReconnectAsync(runtime, SessionEndReason.AuthenticationMismatch);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reconnect failed for session {SessionId}", runtime.SessionId);
            await FailReconnectAsync(runtime, SessionEndReason.ReconnectFailed);
        }
    }

    private async Task<bool> WaitForSignalingAsync(CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + _options.ReconnectMaxDelay;

        while (_time.GetUtcNow() < deadline)
        {
            if (_signaling.State == SignalingConnectionState.Registered) return true;
            if (_signaling.State == SignalingConnectionState.Faulted) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return false;
    }

    private TimeSpan ReconnectDelay(int attempt)
    {
        var exponential = _options.ReconnectInitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var capped = Math.Min(exponential, _options.ReconnectMaxDelay.TotalMilliseconds);
        var jitter = capped * Math.Clamp(_options.ReconnectJitterFraction, 0, 1);
        return TimeSpan.FromMilliseconds(Math.Max(
            0,
            capped + ((Random.Shared.NextDouble() * 2 - 1) * jitter)));
    }

    private async Task MarkResumedAsync(SessionRuntime runtime)
    {
        await runtime.LifecycleGate.WaitAsync();
        try
        {
            if (!IsRuntimeActive(runtime)) return;
            _log.LogInformation(
                "Session {SessionId} restored after {Seconds:0.0} s and {Attempts} attempts (path {Path})",
                runtime.SessionId, (_time.GetUtcNow() - runtime.InterruptedAt).TotalSeconds, runtime.ReconnectAttempts,
                TryGetNetworkPath(runtime.Media) ?? "unknown");
            MarkResumedCore(runtime, restoreInput: true);
        }
        finally
        {
            runtime.LifecycleGate.Release();
        }
    }

    private void MarkResumedCore(SessionRuntime runtime, bool restoreInput)
    {
        if (runtime.Machine.State == SessionState.Reconnecting)
            Fire(runtime, SessionTrigger.Reconnected);

        if (runtime.Reconnect.State == ReconnectState.ConnectionInterrupted)
            runtime.Reconnect.TryFire(ReconnectTrigger.RetryStarted, out _);
        if (runtime.Reconnect.State == ReconnectState.Reauthenticating)
            runtime.Reconnect.TryFire(ReconnectTrigger.Reauthenticated, out _);
        if (runtime.Reconnect.State == ReconnectState.Reconnecting)
            runtime.Reconnect.TryFire(ReconnectTrigger.RenegotiationStarted, out _);
        if (runtime.Reconnect.State == ReconnectState.Renegotiating)
            runtime.Reconnect.TryFire(ReconnectTrigger.ResumeSucceeded, out _);
        if (runtime.Reconnect.State == ReconnectState.Resumed)
            runtime.Reconnect.TryFire(ReconnectTrigger.Stabilized, out _);

        _timeline.Record(runtime.SessionId, "reconnect.completed", "Secure session reconnect completed");
        runtime.CancelReconnect();
        if (restoreInput && runtime.Role == SessionRole.Sharer)
        {
            RestorePlatformInput(runtime);
        }
        Notify(runtime);
    }

    private void CompleteIceRestart(SessionRuntime runtime)
    {
        if (runtime.Machine.State != SessionState.Reconnecting || !runtime.RequiresIceRestart) return;

        runtime.RequiresIceRestart = false;
        runtime.Collaboration?.ConnectionResumed();
        MarkResumedCore(runtime, restoreInput: true);
    }

    private async Task FailReconnectAsync(SessionRuntime runtime, SessionEndReason reason)
    {
        _log.LogWarning(
            "Session {SessionId} could not be restored after {Seconds:0.0} s and {Attempts} attempts; ending with {Reason}",
            runtime.SessionId, (_time.GetUtcNow() - runtime.InterruptedAt).TotalSeconds, runtime.ReconnectAttempts, reason);
        if (reason == SessionEndReason.AuthenticationMismatch)
            runtime.Reconnect.TryFire(ReconnectTrigger.AuthenticationMismatch, out _);
        else
            runtime.Reconnect.TryFire(ReconnectTrigger.RetryExhausted, out _);

        await DisableRuntimeInputAsync(runtime);
        await NotifyPeerOfEndAsync(runtime.SessionId, reason);
        await CloseAsync(runtime, reason);
    }

    // ------------------------------------------------------------------ ending

    /// <summary>Either side can end at any time; this is what the Stop button calls.</summary>
    public async Task EndSessionAsync(SessionId sessionId, SessionEndReason reason, CancellationToken cancellationToken = default)
    {
        var runtime = Find(sessionId);
        if (runtime is null) return;

        await DisableRuntimeInputAsync(runtime);
        await NotifyPeerOfEndAsync(sessionId, reason, cancellationToken);

        await CloseAsync(runtime, reason);
    }

    private async Task TimeoutAsync(SessionRuntime runtime, SessionEndReason reason)
    {
        _log.LogInformation("Session {SessionId} timed out: {Reason}", runtime.SessionId, reason);

        await DisableRuntimeInputAsync(runtime);
        await NotifyPeerOfEndAsync(runtime.SessionId, reason);

        await CloseAsync(runtime, reason);
    }

    private async Task FailAsync(SessionRuntime runtime, SessionEndReason reason)
    {
        Fire(runtime, SessionTrigger.Fault);
        await DisableRuntimeInputAsync(runtime);
        await NotifyPeerOfEndAsync(runtime.SessionId, reason);
        await ReleaseAsync(runtime, reason);
    }

    private async Task DisableRuntimeInputAsync(SessionRuntime runtime)
    {
        runtime.BeginEnding();
        runtime.CancelTimeout();
        runtime.CancelReconnect();
        runtime.CancelQualityPublisher();
        await runtime.LifecycleGate.WaitAsync();
        try
        {
            // Serialize against the Connected callback that grants the approved input scope. Once
            // IsEnding is set, no late media event can restore input after this fail-closed reset.
            runtime.Collaboration?.ConnectionInterrupted();
            DisablePlatformInput(runtime);
        }
        finally
        {
            runtime.LifecycleGate.Release();
        }
    }

    private void DisablePlatformInput(SessionRuntime runtime)
    {
        if (_remoteInputSink is null)
        {
            _inputSafety.DisableAndReleaseAll();
            return;
        }
        if (runtime.Role != SessionRole.Sharer) return;
        if (!_remoteInputFocus.ExecuteIfOwner(runtime.SessionId, _inputSafety.DisableAndReleaseAll))
            _remoteInputFocus.ExecuteIfUnowned(_inputSafety.DisableAndReleaseAll);
    }

    private void RestorePlatformInput(SessionRuntime runtime)
    {
        if (_remoteInputSink is null)
        {
            _inputSafety.RestoreApprovedScope(runtime.EffectivePermissions);
            return;
        }
        if (!_remoteInputFocus.ExecuteIfOwner(runtime.SessionId,
                () => _inputSafety.RestoreApprovedScope(runtime.EffectivePermissions)))
        {
            // Connection readiness alone never grants injection. Only an acknowledged focus
            // request may restore ControlInput, and another session's sink is left untouched.
            _remoteInputFocus.ExecuteIfUnowned(() => _inputSafety.RestoreApprovedScope(
                runtime.EffectivePermissions & ~SessionPermission.ControlInput));
        }
    }

    private async Task NotifyPeerOfEndAsync(
        SessionId sessionId,
        SessionEndReason reason,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EndNotificationTimeout);
        try
        {
            await _signaling.EndSessionAsync(sessionId, reason, timeout.Token)
                .WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug(
                "Peer end notification was cancelled or exceeded {TimeoutMs} ms for session {SessionId}",
                EndNotificationTimeout.TotalMilliseconds,
                sessionId);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not notify the peer that session {SessionId} is ending", sessionId);
        }
    }

    private async Task CloseAsync(SessionRuntime runtime, SessionEndReason reason)
    {
        runtime.CloseReason ??= reason;
        runtime.PermissionWithdrawn = true;
        try
        {
            runtime.PermissionPrompt?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The prompt already completed.
        }

        runtime.Reconnect.TryFire(ReconnectTrigger.EndRequested, out _);

        if (runtime.Machine.State is not (SessionState.Ended or SessionState.Failed))
        {
            if (runtime.Machine.CanFire(SessionTrigger.EndRequested))
            {
                Fire(runtime, SessionTrigger.EndRequested);
            }

            if (runtime.Machine.CanFire(SessionTrigger.Closed))
            {
                Fire(runtime, SessionTrigger.Closed);
            }
        }

        await ReleaseAsync(runtime, reason);
    }

    /// <summary>Releases every resource the session held. Safe to call twice.</summary>
    private async Task ReleaseAsync(SessionRuntime runtime, SessionEndReason reason)
    {
        if (!runtime.TryBeginRelease())
        {
            await runtime.ReleaseCompletion;
            return;
        }
        await runtime.LifecycleGate.WaitAsync();
        try
        {

            var finalNetworkPath = TryGetNetworkPath(runtime.Media);
            _timeline.Record(
                runtime.SessionId,
                "session.ended",
                $"Session ended ({reason})");

            runtime.CancelTimeout();
            runtime.CancelReconnect();
            runtime.CancelQualityPublisher();
            DisablePlatformInput(runtime);

            _log.LogInformation("Session {SessionId} is closing: {Reason}", runtime.SessionId, reason);

            if (runtime.Collaboration is { } collaboration)
                await RunReleaseStepAsync(runtime, "Collaboration cleanup", () => collaboration.DisposeAsync().AsTask());

            if (runtime.Media is { } media)
            {
                await RunReleaseStepAsync(runtime, "Media close", () => media.CloseAsync());
                await RunReleaseStepAsync(runtime, "Media disposal", () => media.DisposeAsync().AsTask());
            }

            if (runtime.Capture is { } capture)
            {
                if (runtime.CaptureStoppedHandler is not null)
                {
                    capture.CaptureStopped -= runtime.CaptureStoppedHandler;
                    runtime.CaptureStoppedHandler = null;
                }
                await RunReleaseStepAsync(runtime, "Capture stop", () => capture.StopAsync());
                await RunReleaseStepAsync(runtime, "Capture disposal", () => capture.DisposeAsync().AsTask());
            }

            lock (_gate)
            {
                _sessions.Remove(runtime.SessionId);
            }

            if (runtime.PermissionAccepted && !runtime.SessionStartAudited)
            {
                try
                {
                    await _auditLog.RecordAsync(new SessionAuditEntry
                    {
                        SessionId = runtime.SessionId,
                        Role = runtime.Role,
                        PeerMaskedId = runtime.PeerId.Masked,
                        PeerDisplayName = runtime.PeerDisplayName,
                        Mode = runtime.Mode,
                        StartedAt = runtime.StartedAt,
                    });
                    runtime.SessionStartAudited = true;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not write the start audit entry for session {SessionId}", runtime.SessionId);
                }
            }

            try
            {
                await _auditLog.RecordAsync(new SessionAuditEntry
                {
                    SessionId = runtime.SessionId,
                    Role = runtime.Role,
                    PeerMaskedId = runtime.PeerId.Masked,
                    PeerDisplayName = runtime.PeerDisplayName,
                    Mode = runtime.Mode,
                    StartedAt = runtime.StartedAt,
                    EndedAt = _time.GetUtcNow(),
                    EndReason = reason,
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not write the audit entry for session {SessionId}", runtime.SessionId);
            }

            await AppendSecurityAuditAsync(new SecurityAuditEvent
            {
                EventId = Guid.NewGuid(),
                EventType = SecurityAuditEventType.SessionEnded,
                OccurredAt = _time.GetUtcNow(),
                SessionId = runtime.SessionId.ToString(),
                PeerMaskedId = runtime.PeerId.Masked,
                PermissionSet = runtime.Permissions,
                Outcome = reason is SessionEndReason.EndedByViewer or SessionEndReason.EndedBySharer or SessionEndReason.ApplicationShutdown
                    ? "ended"
                    : "failed",
                FailureCategory = reason.ToString(),
                NetworkPath = finalNetworkPath,
                IntegrityMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["access_kind"] = runtime.AccessKind.ToString(),
                    ["role"] = runtime.Role.ToString(),
                },
            });

            if (!runtime.CloudTelemetrySuppressed)
            {
                await EnsureCloudStartQueuedAsync(runtime);
                await QueueClientSessionEventAsync(
                    runtime,
                    reason is SessionEndReason.EndedByViewer or SessionEndReason.EndedBySharer or SessionEndReason.ApplicationShutdown
                        ? ClientSessionEventKind.Ended
                        : ClientSessionEventKind.Failed,
                    reason);
            }

            _log.LogInformation("Session {SessionId} closed: {Reason}", runtime.SessionId, reason);
            SessionClosed?.Invoke(this, (runtime.SessionId, reason));
        }
        finally
        {
            runtime.LifecycleGate.Release();
            runtime.CompleteRelease();
        }
    }

    private static readonly TimeSpan ScreenShareTeardownWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One screen-sharing session at a time on this device. A previous session that is already
    /// ending is waited for (bounded); a live or connecting one is returned, and the new request
    /// is declined as busy.
    /// </summary>
    private async Task<SessionRuntime?> WaitForScreenSharingSlotAsync(SessionId requestId, CancellationToken cancellationToken)
    {
        if (_options.AllowConcurrentScreenSharing) return null;
        while (FindActiveScreenShare(requestId) is { } active)
        {
            if (!active.IsEnding) return active;
            try
            {
                await active.ReleaseCompletion.WaitAsync(ScreenShareTeardownWait, cancellationToken);
            }
            catch (TimeoutException)
            {
                return active;
            }
        }
        return null;
    }

    private SessionRuntime? FindActiveScreenShare(SessionId excluding)
    {
        lock (_gate)
        {
            return _sessions.Values.FirstOrDefault(runtime =>
                runtime.SessionId != excluding
                && runtime.Role == SessionRole.Sharer
                && runtime.EffectivePermissions.HasFlag(SessionPermission.ViewScreen)
                && (runtime.PermissionAccepted
                    || runtime.Machine.State is SessionState.Negotiating or SessionState.Connecting
                        or SessionState.ConnectedViewOnly or SessionState.Reconnecting or SessionState.Ending));
        }
    }

    private async Task DeclineBusyAsync(
        SessionId requestId, PeerOnQId requester, SessionRuntime active, CancellationToken cancellationToken)
    {
        _log.LogInformation(
            "Declining session {SessionId} from {Device} as busy: this device is already sharing its screen in session {ActiveSessionId} ({State})",
            requestId, requester.Masked, active.SessionId, active.Machine.State);
        _timeline.Record(requestId, "session.request.busy", "Declined: this device is already sharing its screen");
        await _signaling.SendPermissionDeclineAsync(requestId, PermissionDeclineReasons.Busy, cancellationToken)
            .WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Runs one release step off the calling thread and waits at most
    /// <see cref="SessionOptions.ReleaseStepTimeout"/>. A step that hangs (a native close that
    /// never returns) is logged and left behind; the session still completes its release.
    /// </summary>
    private async Task RunReleaseStepAsync(SessionRuntime runtime, string step, Func<Task> operation)
    {
        var task = Task.Run(operation);
        try
        {
            await task.WaitAsync(_options.ReleaseStepTimeout);
        }
        catch (TimeoutException)
        {
            _log.LogWarning(
                "{Step} for session {SessionId} did not finish within {Seconds:0} s; the session ends without waiting for it",
                step, runtime.SessionId, _options.ReleaseStepTimeout.TotalSeconds);
            _ = task.ContinueWith(
                completed => _log.LogInformation(
                    completed.Exception?.GetBaseException(),
                    "{Step} for session {SessionId} finished late ({Status})",
                    step, runtime.SessionId, completed.Status),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Step} failed for session {SessionId}", step, runtime.SessionId);
        }
    }

    private SessionRuntime? Find(SessionId id)
    {
        lock (_gate)
        {
            if (_disposing || !_sessions.TryGetValue(id, out var runtime)
                || runtime.IsEnding || runtime.IsReleased)
                return null;
            return runtime;
        }
    }

    private bool IsRuntimeActive(SessionRuntime runtime)
    {
        lock (_gate)
        {
            return !_disposing
                   && !runtime.IsEnding
                   && !runtime.IsReleased
                   && _sessions.TryGetValue(runtime.SessionId, out var current)
                   && ReferenceEquals(current, runtime);
        }
    }

    private void RunStartupOperation(
        Func<CancellationToken, Task> operation,
        string failureMessage)
    {
        Task task;
        lock (_gate)
        {
            if (_disposing) return;
            task = RunStartupOperationAsync(operation, failureMessage);
            _startupOperations.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _startupOperations.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunStartupOperationAsync(
        Func<CancellationToken, Task> operation,
        string failureMessage)
    {
        // Ensure the task is registered before user code can complete synchronously.
        await Task.Yield();
        try
        {
            await operation(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // DisposeAsync cancelled negotiation before releasing its runtime resources.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{SessionStartupFailure}", failureMessage);
        }
    }

    private void Fire(SessionRuntime runtime, SessionTrigger trigger) => runtime.Machine.TryFire(trigger, out _);

    private void Notify(SessionRuntime runtime) => SessionChanged?.Invoke(this, runtime.ToInfo());

    private async Task AppendSecurityAuditAsync(SecurityAuditEvent auditEvent)
    {
        if (_securityAudit is null) return;
        try { await _securityAudit.AppendAsync(auditEvent); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not append security audit event {EventType}", auditEvent.EventType);
        }
    }

    /// <summary>Queues the cloud Started event once; every later lifecycle event follows it.</summary>
    private async Task EnsureCloudStartQueuedAsync(SessionRuntime runtime)
    {
        if (runtime.CloudTelemetrySuppressed || !runtime.TryMarkCloudStartQueued()) return;
        await QueueClientSessionEventAsync(runtime, ClientSessionEventKind.Started);
    }

    private async Task QueueClientSessionEventAsync(
        SessionRuntime runtime,
        ClientSessionEventKind kind,
        SessionEndReason? reason = null)
    {
        try
        {
            var statistics = runtime.Media?.GetStatistics() ?? MediaStatistics.Empty;
            await _clientSessionEvents.EnqueueAsync(new ClientSessionTelemetryEvent
            {
                EventId = Guid.NewGuid(),
                Kind = kind,
                SessionId = runtime.SessionId,
                Role = runtime.Role,
                // The cloud identifies devices by the 12-digit public ID; Value carries the "LNK-" prefix.
                PeerPublicDeviceId = runtime.PeerId.Display,
                PermissionMode = runtime.Mode,
                Permissions = runtime.Permissions,
                StartedAtUtc = runtime.StartedAt,
                OccurredAtUtc = _time.GetUtcNow(),
                ConnectionPath = statistics.ConnectionPath.ToString(),
                RelayServerId = statistics.RelayServerId,
                ServerRegion = statistics.RelayRegion,
                EndReason = reason?.ToString(),
                FailureStage = kind == ClientSessionEventKind.Failed ? "media_or_session" : null,
                FailureCode = kind == ClientSessionEventKind.Failed ? reason?.ToString() : null,
                UsedTurn = statistics.ConnectionPath == ConnectionPath.Relayed,
                ReconnectCount = runtime.ReconnectAttempts,
            });
        }
        catch (Exception ex)
        {
            // Operational telemetry must never alter the remote-session or permission path.
            _log.LogWarning(ex, "Could not queue sanitized client session telemetry");
        }
    }

    private static string? TryGetNetworkPath(IMediaSession? media)
    {
        if (media is null) return null;
        try { return media.GetStatistics().ConnectionPath.ToString(); }
        catch { return null; }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_disposeCompletion is not null)
                return new ValueTask(_disposeCompletion.Task);

            _disposing = true;
            completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeCompletion = completion;
        }

        _lifetimeCancellation.Cancel();
        _ = CompleteDisposeAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await DisposeCoreAsync();
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _signaling.SessionRequested -= OnSessionRequested;
        _signaling.PermissionResolved -= OnPermissionResolved;
        _signaling.SdpReceived -= OnSdpReceived;
        _signaling.IceReceived -= OnIceReceived;
        _signaling.SessionEnded -= OnSessionEnded;
        _signaling.RemoteDisplaysReceived -= OnRemoteDisplays;
        _signaling.DisplaySelectionRequested -= OnDisplaySelectionRequested;
        _signaling.StateChanged -= OnSignalingStateChanged;
        _signaling.ErrorReceived -= OnSignalingError;
        if (_qualitySignaling is not null) _qualitySignaling.QualityReceived -= OnQualityReceived;
        if (_resumableSignaling is not null) _resumableSignaling.PeerResumed -= OnPeerResumed;
        if (_unattendedSignaling is not null)
            _unattendedSignaling.UnattendedChallengeRequested -= OnUnattendedChallengeRequested;

        Task[] startupOperations;
        SessionRuntime[] runtimes;
        lock (_gate)
        {
            startupOperations = _startupOperations.ToArray();
            runtimes = _sessions.Values.ToArray();
        }

        // Fail closed immediately. An already-connected input scope must not remain authorized
        // while an unrelated startup callback drains during application shutdown.
        await Task.WhenAll(runtimes.Select(DisableRuntimeInputAsync));

        // Cancellation is issued before this wait. Startup handlers either finish normally or
        // observe the coordinator lifetime token; no handler may attach capture/media afterward.
        if (startupOperations.Length > 0)
        {
            await Task.WhenAll(startupOperations);
        }

        SessionRuntime[] currentRuntimes;
        lock (_gate)
        {
            currentRuntimes = _sessions.Values.ToArray();
        }

        var knownSessionIds = runtimes.Select(runtime => runtime.SessionId).ToHashSet();
        var lateRuntimes = currentRuntimes
            .Where(runtime => !knownSessionIds.Contains(runtime.SessionId))
            .ToArray();
        if (lateRuntimes.Length > 0)
        {
            await Task.WhenAll(lateRuntimes.Select(DisableRuntimeInputAsync));
            runtimes = [.. runtimes, .. lateRuntimes];
        }

        // Notify in parallel under one short per-session bound, then always release local input,
        // media and capture even if a WebSocket send gate is wedged.
        await Task.WhenAll(runtimes.Select(runtime =>
            NotifyPeerOfEndAsync(runtime.SessionId, SessionEndReason.ApplicationShutdown)));

        foreach (var runtime in runtimes)
        {
            await ReleaseAsync(runtime, SessionEndReason.ApplicationShutdown);
        }
    }

    private abstract record PendingNegotiationSignal;

    private sealed record PendingSdpSignal(SdpNotification Notification) : PendingNegotiationSignal;

    private sealed record PendingIceSignal(IceNotification Notification) : PendingNegotiationSignal;

    private static bool TryResolveGrantedScope(
        SessionMode requestedMode,
        SessionPermission requestedPermissions,
        SessionAccessKind accessKind,
        bool remoteScopeSelectionRequired,
        SessionMode? grantedMode,
        SessionPermission? grantedPermissions,
        out SessionMode resolvedMode,
        out SessionPermission resolvedPermissions)
    {
        resolvedMode = requestedMode;
        resolvedPermissions = requestedPermissions;

        if (grantedMode is null || grantedPermissions is null)
            return !remoteScopeSelectionRequired && grantedMode is null && grantedPermissions is null;

        if (!SessionPermissionPolicy.IsValid(grantedMode.Value, grantedPermissions.Value)
            || !Phase1SessionScope.IsAllowed(grantedMode.Value, grantedPermissions.Value, accessKind))
        {
            return false;
        }

        if (!remoteScopeSelectionRequired)
        {
            if (grantedMode != requestedMode || grantedPermissions != requestedPermissions)
                return false;
        }
        else if (accessKind != SessionAccessKind.Attended
                 || requestedMode != SessionMode.FullControl
                 || !Phase1SessionScope.IsRemoteScopeSelectionEnvelope(requestedPermissions)
                 || !Phase1SessionScope.IsRemoteScopeSelectionGrant(grantedMode.Value, grantedPermissions.Value, requestedPermissions))
        {
            return false;
        }

        resolvedMode = grantedMode.Value;
        resolvedPermissions = grantedPermissions.Value;
        return true;
    }

    private sealed class SessionRuntime(
        SessionId sessionId,
        SessionRole role,
        PeerOnQId peerId,
        string peerDisplayName,
        DateTimeOffset startedAt,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        string? peerKeyFingerprint,
        MediaProfile mediaProfile,
        bool remoteScopeSelectionRequired = false)
    {
        private const int MaxPendingNegotiationSignals = 256;
        private CancellationTokenSource? _timeout;
        private CancellationTokenSource? _reconnectCancellation;
        private Task? _reconnectTask;
        private CancellationTokenSource? _signalingResumeCancellation;
        private Task? _signalingResumeTask;
        private CancellationTokenSource? _qualityCancellation;
        private Task? _qualityTask;
        private readonly CancellationTokenSource _lifecycleCancellation = new();
        private readonly TaskCompletionSource<bool> _releaseCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Queue<PendingNegotiationSignal> _pendingNegotiationSignals = [];
        private int _ending;
        private int _released;

        public SessionId SessionId { get; } = sessionId;
        public SessionRole Role { get; } = role;
        public PeerOnQId PeerId { get; } = peerId;
        public string PeerDisplayName { get; set; } = peerDisplayName;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public SessionMode Mode { get; private set; } = mode;
        public SessionPermission Permissions { get; private set; } = permissions;
        public SessionPermission RevokedPermissions { get; set; }
        public SessionPermission EffectivePermissions => Permissions & ~RevokedPermissions;
        public SessionAccessKind AccessKind { get; } = accessKind;
        public bool RemoteScopeSelectionRequired { get; } = remoteScopeSelectionRequired;
        public string? PeerKeyFingerprint { get; set; } = peerKeyFingerprint;
        public MediaProfile MediaProfile { get; } = mediaProfile;
        public SessionStateMachine Machine { get; } = new();
        public IMediaSession? Media { get; set; }
        public IScreenCaptureSource? Capture { get; set; }
        public EventHandler<CaptureStoppedReason>? CaptureStoppedHandler { get; set; }

        /// <summary>The open permission prompt of an incoming request, withdrawn when the runtime closes.</summary>
        public CancellationTokenSource? PermissionPrompt { get; set; }

        /// <summary>Set when the runtime closes: a prompt still open must not send a late answer.</summary>
        public volatile bool PermissionWithdrawn;

        /// <summary>The first reason this runtime was closed with.</summary>
        public SessionEndReason? CloseReason { get; set; }
        public SessionCollaborationContext? Collaboration { get; set; }
        public ReconnectStateMachine Reconnect { get; } = new();
        public int ReconnectAttempts { get; set; }
        public string? LastInterruptionReason { get; set; }
        public DateTimeOffset InterruptedAt { get; set; }
        public bool SignalingInterrupted { get; set; }
        public bool RequiresIceRestart { get; set; }
        public SessionQualityNotification? RemoteQuality { get; set; }
        public RemoteDisplaysNotification? RemoteDisplays { get; set; }
        public bool PermissionAccepted { get; set; }
        public bool FileRelayEnabled { get; set; }
        public bool UnicodeInputEnabled { get; set; }
        public bool InitialAudioCaptureStarted { get; set; }
        public bool SessionStartAudited { get; set; }
        public bool SecurityStartAudited { get; set; }
        private int _cloudStartQueued;

        /// <summary>True exactly once: the caller queues the cloud Started event.</summary>
        public bool TryMarkCloudStartQueued() => Interlocked.Exchange(ref _cloudStartQueued, 1) == 0;
        public bool CloudConnectedQueued { get; set; }

        /// <summary>Set for a request signaling refused: it never reached the peer, so the cloud never hears of it.</summary>
        public bool CloudTelemetrySuppressed { get; set; }

        /// <summary>The server's message for a refused request.</summary>
        public volatile string? RequestRefusal;

        /// <summary>
        /// Signaling no longer tracks this session (its ownership could not be restored), but its
        /// peer-to-peer media is connected. It continues without renegotiation until media ends.
        /// </summary>
        public bool SignalingDetached { get; set; }

        /// <summary>The sharer's display list changed while signaling was held.</summary>
        public bool DisplaysPublishPending { get; set; }
        public bool SecureConnected { get; set; }
        public bool FirstFrameRendered { get; set; }
        public CancellationToken LifecycleToken => _lifecycleCancellation.Token;
        public SemaphoreSlim LifecycleGate { get; } = new(1, 1);
        public bool IsEnding => Volatile.Read(ref _ending) != 0;
        public bool IsReleased => Volatile.Read(ref _released) != 0;
        public Task ReleaseCompletion => _releaseCompletion.Task;

        public bool TryQueuePendingSignal(PendingNegotiationSignal signal)
        {
            if (_pendingNegotiationSignals.Count >= MaxPendingNegotiationSignals) return false;
            _pendingNegotiationSignals.Enqueue(signal);
            return true;
        }

        public void ApplyGrantedScope(SessionMode grantedMode, SessionPermission grantedPermissions)
        {
            if (Media is not null || PermissionAccepted)
                throw new InvalidOperationException("A session scope cannot change after media initialization.");
            if (!SessionPermissionPolicy.IsValid(grantedMode, grantedPermissions)
                || !Phase1SessionScope.IsAllowed(grantedMode, grantedPermissions, AccessKind))
            {
                throw new ArgumentException("The granted session scope is invalid.");
            }

            Mode = grantedMode;
            Permissions = grantedPermissions;
            RevokedPermissions &= grantedPermissions;
        }

        public PendingNegotiationSignal[] AttachMedia(IMediaSession media)
        {
            if (Media is not null && !ReferenceEquals(Media, media))
            {
                throw new InvalidOperationException("A different media session is already attached.");
            }

            Media = media;
            var pending = _pendingNegotiationSignals.ToArray();
            _pendingNegotiationSignals.Clear();
            return pending;
        }

        public bool TryBeginRelease()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return false;
            BeginEnding();
            return true;
        }

        public void BeginEnding()
        {
            if (Interlocked.Exchange(ref _ending, 1) != 0) return;
            _lifecycleCancellation.Cancel();
        }

        public void CompleteRelease() => _releaseCompletion.TrySetResult(true);

        public void ArmTimeout(TimeSpan delay, Func<Task> onElapsed)
        {
            CancelTimeout();

            var cts = new CancellationTokenSource();
            _timeout = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cts.Token);
                    await onElapsed();
                }
                catch (OperationCanceledException)
                {
                    // Disarmed because the expected event arrived.
                }
            });
        }

        public void CancelTimeout()
        {
            var cts = Interlocked.Exchange(ref _timeout, null);
            if (cts is null) return;

            cts.Cancel();
            cts.Dispose();
        }

        public void StartReconnect(Func<CancellationToken, Task> reconnect)
        {
            if (_reconnectTask is { IsCompleted: false }) return;

            CancelReconnect();
            var cts = new CancellationTokenSource();
            _reconnectCancellation = cts;
            _reconnectTask = Task.Run(() => reconnect(cts.Token), CancellationToken.None);
        }

        /// <summary>
        /// Waits for signaling while peer-to-peer media stays up. A media loss hands over to the
        /// full reconnect (which resumes signaling itself) and cancels this wait.
        /// </summary>
        public void StartSignalingResume(Func<CancellationToken, Task> resume)
        {
            if (_signalingResumeTask is { IsCompleted: false }) return;

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _signalingResumeCancellation, cts)?.Dispose();
            _signalingResumeTask = Task.Run(() => resume(cts.Token), CancellationToken.None);
        }

        public void CancelReconnect()
        {
            var resume = Interlocked.Exchange(ref _signalingResumeCancellation, null);
            resume?.Cancel();
            resume?.Dispose();

            var cts = Interlocked.Exchange(ref _reconnectCancellation, null);
            if (cts is null) return;

            cts.Cancel();
            cts.Dispose();
        }

        public void StartQualityPublisher(Func<CancellationToken, Task> publish)
        {
            if (_qualityTask is { IsCompleted: false }) return;

            CancelQualityPublisher();
            var cts = new CancellationTokenSource();
            _qualityCancellation = cts;
            _qualityTask = Task.Run(() => publish(cts.Token), CancellationToken.None);
        }

        public void CancelQualityPublisher()
        {
            var cts = Interlocked.Exchange(ref _qualityCancellation, null);
            if (cts is null) return;

            cts.Cancel();
            cts.Dispose();
        }

        public ActiveSessionInfo ToInfo()
        {
            var security = Collaboration?.Security;
            if (security is not null && Media is not null)
                security = security with { ConnectionPath = Media.GetStatistics().ConnectionPath };
            return new ActiveSessionInfo(
                SessionId,
                Role,
                PeerDisplayName,
                PeerId.MaskedDisplay,
                Mode,
                Machine.State,
                StartedAt,
                Permissions,
                AccessKind,
                PeerKeyFingerprint,
                PeerId,
                security,
                SignalingInterrupted || SignalingDetached);
        }
    }
}
