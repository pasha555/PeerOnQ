using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Abstractions;

public enum SignalingConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Registering = 2,
    Registered = 3,
    Reconnecting = 4,
    Faulted = 5,
}

public sealed record IncomingSessionNotification(
    SessionId SessionId,
    PeerOnQId FromDeviceId,
    string FromDisplayName,
    SessionMode Mode,
    DateTimeOffset ExpiresAt,
    SessionPermission Permissions = SessionPermission.ViewScreen,
    SessionAccessKind AccessKind = SessionAccessKind.Attended,
    string? FromKeyFingerprint = null,
    Guid? UnattendedChallengeId = null,
    string? UnattendedProof = null,
    QualityProfile Quality = QualityProfile.Automatic,
    CaptureResolution? Resolution = null,
    string? SupportInvitationToken = null,
    string? SupportInvitationPassword = null,
    bool FileRelay = false,
    bool RemoteScopeSelectionRequired = false);

public sealed record PermissionResultNotification(
    SessionId SessionId,
    PermissionDecision Decision,
    string? PeerDisplayName,
    string? PeerKeyFingerprint = null,
    bool FileRelay = false,
    SessionMode? GrantedMode = null,
    SessionPermission? GrantedPermissions = null);

public sealed record SdpNotification(SessionId SessionId, string SdpType, string Sdp);

public sealed record IceNotification(SessionId SessionId, string Candidate, string? SdpMid, ushort SdpMLineIndex);

public sealed record SessionEndedNotification(SessionId SessionId, SessionEndReason Reason);

/// <summary>Displays the sharer offers, and which one it is currently sending.</summary>
public sealed record RemoteDisplaysNotification(
    SessionId SessionId,
    IReadOnlyList<CaptureTargetInfo> Displays,
    string ActiveDisplayId);

/// <summary>The viewer asked the sharer to switch display.</summary>
public sealed record SelectDisplayNotification(SessionId SessionId, string DisplayId);

public sealed record SignalingErrorNotification(
    string Code,
    string Message,
    string? InReplyTo,
    string? CapabilitySide = null,
    IReadOnlyList<string>? RequiredCapabilities = null);

/// <summary>
/// Control-plane client. Carries registration, permission, SDP and ICE; optional opaque,
/// hybrid-session-protected file records use <see cref="IFileRelaySignaling"/>.
/// </summary>
public interface ISignalingClient : IAsyncDisposable
{
    SignalingConnectionState State { get; }
    PeerOnQId? RegisteredId { get; }

    event EventHandler<SignalingConnectionState>? StateChanged;
    event EventHandler<IncomingSessionNotification>? SessionRequested;
    event EventHandler<PermissionResultNotification>? PermissionResolved;
    event EventHandler<SdpNotification>? SdpReceived;
    event EventHandler<IceNotification>? IceReceived;
    event EventHandler<SessionEndedNotification>? SessionEnded;
    event EventHandler<SignalingErrorNotification>? ErrorReceived;
    event EventHandler<RemoteDisplaysNotification>? RemoteDisplaysReceived;
    event EventHandler<SelectDisplayNotification>? DisplaySelectionRequested;

    Task ConnectAsync(DeviceIdentity identity, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task RequestSessionAsync(SessionId sessionId, PeerOnQId target, SessionMode mode, CancellationToken cancellationToken = default);

    Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        CancellationToken cancellationToken = default) =>
        RequestSessionAsync(sessionId, target, mode, cancellationToken);

    Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        Guid? unattendedChallengeId,
        string? unattendedProof,
        CancellationToken cancellationToken = default) =>
        RequestSessionAsync(sessionId, target, mode, permissions, accessKind, cancellationToken);

    Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        Guid? unattendedChallengeId,
        string? unattendedProof,
        QualityProfile quality,
        CancellationToken cancellationToken = default) =>
        RequestSessionAsync(
            sessionId,
            target,
            mode,
            permissions,
            accessKind,
            unattendedChallengeId,
            unattendedProof,
            cancellationToken);

    Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        Guid? unattendedChallengeId,
        string? unattendedProof,
        QualityProfile quality,
        CaptureResolution resolution,
        CancellationToken cancellationToken = default) =>
        RequestSessionAsync(
            sessionId,
            target,
            mode,
            permissions,
            accessKind,
            unattendedChallengeId,
            unattendedProof,
            quality,
            cancellationToken);

    /// <summary>
    /// Attended full-control requests can require the remote owner to choose the final scope.
    /// Implementations that do not support it must not silently advertise that behavior.
    /// </summary>
    Task RequestSessionAsync(
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
        CancellationToken cancellationToken = default) =>
        remoteScopeSelectionRequired
            ? Task.FromException(new NotSupportedException(
                "This signaling client cannot enforce remote scope selection."))
            : RequestSessionAsync(
                sessionId,
                target,
                mode,
                permissions,
                accessKind,
                unattendedChallengeId,
                unattendedProof,
                quality,
                resolution,
                cancellationToken);

    Task RequestSupportSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        string invitationToken,
        string? invitationPassword,
        QualityProfile quality,
        CaptureResolution resolution,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This signaling client does not support invitation-bound sessions."));
    Task SendPermissionDecisionAsync(SessionId sessionId, PermissionDecision decision, CancellationToken cancellationToken = default);
    Task SendPermissionDecisionAsync(
        SessionId sessionId,
        PermissionDecision decision,
        SessionMode grantedMode,
        SessionPermission grantedPermissions,
        CancellationToken cancellationToken = default) =>
        SendPermissionDecisionAsync(sessionId, decision, cancellationToken);
    Task SendSdpAsync(SessionId sessionId, string sdpType, string sdp, CancellationToken cancellationToken = default);
    Task SendIceAsync(SessionId sessionId, string candidate, string? sdpMid, ushort sdpMLineIndex, CancellationToken cancellationToken = default);
    Task EndSessionAsync(SessionId sessionId, SessionEndReason reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns session-scoped STUN/TURN configuration. Implementations without a credential
    /// service remain direct-only, preserving the existing local-network behavior.
    /// </summary>
    Task<IceConfiguration> GetIceConfigurationAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(IceConfiguration.DirectOnly);

    /// <summary>Sharer: publish the displays it can share and the one that is live.</summary>
    Task SendDisplaysAsync(SessionId sessionId, IReadOnlyList<CaptureTargetInfo> displays, string activeDisplayId, CancellationToken cancellationToken = default);

    /// <summary>Viewer: ask the sharer to switch to another of its displays.</summary>
    Task RequestDisplayAsync(SessionId sessionId, string displayId, CancellationToken cancellationToken = default);
}

/// <summary>Produces the HMAC proof for a signaling challenge without exposing the device secret.</summary>
public interface IRegistrationProofProvider
{
    Task<string> ComputeRegistrationProofAsync(string challenge, CancellationToken cancellationToken = default);
}

/// <summary>Returns the latest memory-only cloud attestation for signaling registration.</summary>
public interface ISignalingAttestationProvider
{
    Task<string?> GetSignalingAttestationAsync(CancellationToken cancellationToken = default);
}
