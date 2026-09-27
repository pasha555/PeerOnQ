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
    SecureSessionInfo? Security = null);

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
        INativeBulkTransportFactory? nativeBulkTransportFactory = null)
    {
        _signaling = signaling;
        _mediaEngine = mediaEngine;
        _permissionPrompt = permissionPrompt;
        _blockedDevices = blockedDevices;
        _auditLog = auditLog;
        _captureFactory = captureFactory;
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
            _inputSafety.DisableAndReleaseAll();
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
        await RequestSessionCoreAsync(
            target,
            SessionMode.FullControl,
            Phase1SessionScope.FullControlPermissions,
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
                || permissions != Phase1SessionScope.FullControlPermissions))
        {
            throw new ArgumentException(
                "Remote scope selection is available only for attended full-control requests.",
                nameof(remoteScopeSelectionRequired));
        }

        var sessionId = SessionId.New();
        var requestedProfile = Profile;
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
        await QueueClientSessionEventAsync(runtime, ClientSessionEventKind.Started);

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
            await FailAsync(runtime, SessionEndReason.PermissionDeclined);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Session request failed for {Target}", target.Masked);
            await FailAsync(runtime, SessionEndReason.SignalingLost);
            throw;
        }

        operationToken.ThrowIfCancellationRequested();
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
        var requestedProfile = MediaProfile.For(notification.Quality);
        if (notification.Resolution is { } requestedResolution)
            requestedProfile = requestedProfile with { Resolution = requestedResolution };

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

        lock (_gate)
        {
            if (_disposing) return;
            _sessions[notification.SessionId] = runtime;
        }

        _timeline.Record(notification.SessionId, "session.request.received", "Incoming session request received");

        Fire(runtime, SessionTrigger.PermissionRequestSent);
        await QueueClientSessionEventAsync(runtime, ClientSessionEventKind.Started);
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
            cancellationToken.IsCancellationRequested || runtime.IsReleased)
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
                    _remoteInputSink!.SetCaptureTarget(activeTarget);
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

        await _signaling.RequestDisplayAsync(sessionId, displayId, cancellationToken);
    }

    private void OnRemoteDisplays(object? sender, RemoteDisplaysNotification notification)
    {
        if (Find(notification.SessionId) is null) return;

        RemoteDisplaysChanged?.Invoke(this, notification);
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
            PreferredCaptureTarget = target;
            _remoteInputSink?.SetCaptureTarget(target);

            await PublishDisplaysAsync(runtime);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not switch the shared display");
        }
    }

    /// <summary>Sharer: tell the viewer which displays exist and which one is live.</summary>
    private async Task PublishDisplaysAsync(
        SessionRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        if (runtime.Capture is not IAdaptiveCaptureSource adaptive) return;

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
            if (notification.Decision != PermissionDecision.Accept)
            {
                Fire(runtime, SessionTrigger.PermissionRefused);
                await CloseAsync(runtime, notification.Decision switch
                {
                    PermissionDecision.Block => SessionEndReason.PermissionBlocked,
                    PermissionDecision.Timeout => SessionEndReason.PermissionTimeout,
                    _ => SessionEndReason.PermissionDeclined,
                });
                return;
            }

            runtime.PeerDisplayName = notification.PeerDisplayName ?? runtime.PeerDisplayName;
            runtime.PeerKeyFingerprint = notification.PeerKeyFingerprint;
            runtime.FileRelayEnabled = notification.FileRelay;
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
                    runtime.Role == SessionRole.Sharer ? _remoteInputFocus : null)
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
                remoteInput);
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
            if (!IsRuntimeActive(runtime)) return;
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
            {
                _remoteInputSink.SetCaptureTarget(runtime.Capture?.Target);
                _inputSafety.RestoreApprovedScope(runtime.EffectivePermissions);
            }
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
            await QueueClientSessionEventAsync(runtime, ClientSessionEventKind.Connected);
        }
    }

    private async Task FailSecureConnectionAsync(SessionRuntime runtime, Exception exception)
    {
        _log.LogWarning(exception, "Mandatory secure session establishment failed for session {SessionId}", runtime.SessionId);
        if (IsRuntimeActive(runtime))
            await FailAsync(runtime, SessionEndReason.AuthenticationMismatch);
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
            var answer = await media.CreateAnswerAsync(notification.Sdp, cancellationToken)
                .WaitAsync(cancellationToken);
            await _signaling.SendSdpAsync(runtime.SessionId, "answer", answer, cancellationToken)
                .WaitAsync(cancellationToken);
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
                    if (_qualitySignaling is null
                        || _signaling.State != SignalingConnectionState.Registered
                        || runtime.Media is null)
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
                        statistics.InputClockUncertaintyMs), cancellationToken);
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

    private void OnPeerResumed(object? sender, SessionPeerResumedNotification notification)
    {
        var runtime = Find(notification.SessionId);
        if (runtime is null) return;

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
        if (!string.Equals(notification.Code, "invalid_proof", StringComparison.Ordinal)) return;

        SessionRuntime[] reconnecting;
        lock (_gate)
        {
            reconnecting = _sessions.Values
                .Where(runtime => runtime.Machine.State == SessionState.Reconnecting)
                .ToArray();
        }

        foreach (var runtime in reconnecting)
        {
            await FailReconnectAsync(runtime, SessionEndReason.AuthenticationMismatch);
        }
    }

    private void BeginReconnect(SessionRuntime runtime, string reason)
    {
        if (runtime.Reconnect.State != ReconnectState.Connected) return;

        runtime.LastInterruptionReason = reason;
        runtime.InterruptedAt = _time.GetUtcNow();
        runtime.RequiresIceRestart = true;
        _timeline.Record(runtime.SessionId, "connection.interrupted", "Network or media connection interrupted");
        runtime.Reconnect.Fire(ReconnectTrigger.ConnectionLost);
        runtime.Reconnect.Fire(ReconnectTrigger.RetryStarted);

        // This is deliberately first: uncertain transport must never leave a key/button held.
        _inputSafety.DisableAndReleaseAll();

        runtime.StartReconnect(ct => RecoverAsync(runtime, ct));
    }

    private async Task RecoverAsync(SessionRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= _options.MaxReconnectAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _timeline.Record(
                    runtime.SessionId,
                    "reconnect.attempt",
                    $"Reconnect attempt {attempt} started");

                var elapsed = _time.GetUtcNow() - runtime.InterruptedAt;
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
                        if (!registered) continue;
                    }

                    // A fast re-registration can complete before this loop observes the transient
                    // Reconnecting state. Resume whenever the interruption flag is set, not only in
                    // the branch that happened to wait for registration.
                    if (runtime.SignalingInterrupted)
                    {
                        if (_resumableSignaling is not null)
                        {
                            var result = await _resumableSignaling.ResumeSessionAsync(
                                runtime.SessionId,
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
                    if (runtime.Role == SessionRole.Sharer && runtime.Media is not null)
                    {
                        var offer = await runtime.Media.RestartIceAsync(cancellationToken);
                        await _signaling.SendSdpAsync(runtime.SessionId, "offer", offer, cancellationToken);
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
            _remoteInputSink?.SetCaptureTarget(runtime.Capture?.Target);
            _inputSafety.RestoreApprovedScope(runtime.EffectivePermissions);
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
            _inputSafety.DisableAndReleaseAll();
        }
        finally
        {
            runtime.LifecycleGate.Release();
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
            _inputSafety.DisableAndReleaseAll();

            if (runtime.Collaboration is not null)
            {
                try { await runtime.Collaboration.DisposeAsync(); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Collaboration cleanup failed for session {SessionId}", runtime.SessionId);
                }
            }

            if (runtime.Media is not null)
            {
                try
                {
                    await runtime.Media.CloseAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Media close failed for session {SessionId}", runtime.SessionId);
                }

                try
                {
                    await runtime.Media.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Media disposal failed for session {SessionId}", runtime.SessionId);
                }
            }

            if (runtime.Capture is not null)
            {
                try
                {
                    await runtime.Capture.StopAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Capture stop failed for session {SessionId}", runtime.SessionId);
                }

                try
                {
                    await runtime.Capture.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Capture disposal failed for session {SessionId}", runtime.SessionId);
                }
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

            await QueueClientSessionEventAsync(
                runtime,
                reason is SessionEndReason.EndedByViewer or SessionEndReason.EndedBySharer or SessionEndReason.ApplicationShutdown
                    ? ClientSessionEventKind.Ended
                    : ClientSessionEventKind.Failed,
                reason);

            _log.LogInformation("Session {SessionId} closed: {Reason}", runtime.SessionId, reason);
            SessionClosed?.Invoke(this, (runtime.SessionId, reason));
        }
        finally
        {
            runtime.LifecycleGate.Release();
            runtime.CompleteRelease();
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
                PeerPublicDeviceId = runtime.PeerId.Value,
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
                 || requestedPermissions != Phase1SessionScope.FullControlPermissions
                 || (grantedMode != SessionMode.ViewOnly
                     && grantedMode != SessionMode.FullControl)
                 || grantedPermissions != SessionPermissionPolicy.ForMode(grantedMode.Value)
                 || (grantedPermissions.Value & ~requestedPermissions) != 0)
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
        public SessionCollaborationContext? Collaboration { get; set; }
        public ReconnectStateMachine Reconnect { get; } = new();
        public int ReconnectAttempts { get; set; }
        public string? LastInterruptionReason { get; set; }
        public DateTimeOffset InterruptedAt { get; set; }
        public bool SignalingInterrupted { get; set; }
        public bool RequiresIceRestart { get; set; }
        public SessionQualityNotification? RemoteQuality { get; set; }
        public bool PermissionAccepted { get; set; }
        public bool FileRelayEnabled { get; set; }
        public bool SessionStartAudited { get; set; }
        public bool SecurityStartAudited { get; set; }
        public bool CloudConnectedQueued { get; set; }
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

        public void CancelReconnect()
        {
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
                security);
        }
    }
}
