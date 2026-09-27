using System.Text.Json.Serialization;

namespace PeerOnQ.Transport.Protocol;

/// <summary>
/// Control-plane messages. This channel carries registration, permission, SDP and ICE only -
/// never media. Screen frames travel over the WebRTC transport negotiated with these messages.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(ChallengeMessage), "challenge")]
[JsonDerivedType(typeof(RegisterMessage), "register")]
[JsonDerivedType(typeof(RegisteredMessage), "registered")]
[JsonDerivedType(typeof(SessionRequestMessage), "session.request")]
[JsonDerivedType(typeof(SessionIncomingMessage), "session.incoming")]
[JsonDerivedType(typeof(PermissionDecisionMessage), "session.permission")]
[JsonDerivedType(typeof(PermissionResultMessage), "session.permission.result")]
[JsonDerivedType(typeof(SdpMessage), "sdp")]
[JsonDerivedType(typeof(IceCandidateMessage), "ice")]
[JsonDerivedType(typeof(IceServersRequestMessage), "ice.servers.request")]
[JsonDerivedType(typeof(IceServersMessage), "ice.servers")]
[JsonDerivedType(typeof(SessionResumeTokenMessage), "session.resume.token")]
[JsonDerivedType(typeof(SessionResumeMessage), "session.resume")]
[JsonDerivedType(typeof(SessionResumedMessage), "session.resumed")]
[JsonDerivedType(typeof(SessionQualityMessage), "session.quality")]
[JsonDerivedType(typeof(SessionDisplaysMessage), "session.displays")]
[JsonDerivedType(typeof(SelectDisplayMessage), "session.display.select")]
[JsonDerivedType(typeof(SessionEndMessage), "session.end")]
[JsonDerivedType(typeof(PresenceQueryMessage), "presence.query")]
[JsonDerivedType(typeof(PresenceResultMessage), "presence.result")]
[JsonDerivedType(typeof(UnattendedChallengeRequestMessage), "unattended.challenge.request")]
[JsonDerivedType(typeof(UnattendedChallengeIncomingMessage), "unattended.challenge.incoming")]
[JsonDerivedType(typeof(UnattendedChallengeResponseMessage), "unattended.challenge.response")]
[JsonDerivedType(typeof(UnattendedChallengeResultMessage), "unattended.challenge.result")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record SignalingMessage
{
    /// <summary>Correlation id, echoed by the server on errors.</summary>
    [JsonPropertyName("mid")]
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");
}

// ---------------------------------------------------------------- registration

public static class SignalingProtocol
{
    public const int MinimumSupportedVersion = 3;
    public const int CurrentVersion = 3;
    public const int MaximumSupportedVersion = CurrentVersion;

    public static bool IsSupported(int version) =>
        version is >= MinimumSupportedVersion and <= MaximumSupportedVersion;
}

public sealed record HelloMessage : SignalingMessage
{
    [JsonPropertyName("deviceId")] public required string DeviceId { get; init; }
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }
    [JsonPropertyName("clientVersion")] public required string ClientVersion { get; init; }
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; init; }

    // Nullable so a pre-v3 hello can still be decoded and receive the exact unsupported_version
    // contract instead of being misreported as malformed JSON.
    [JsonPropertyName("clientCapabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClientCapabilityManifest? ClientCapabilities { get; init; }
}

public sealed record ChallengeMessage : SignalingMessage
{
    [JsonPropertyName("nonce")] public required string Nonce { get; init; }
    [JsonPropertyName("expiresAt")] public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record RegisterMessage : SignalingMessage
{
    [JsonPropertyName("deviceId")] public required string DeviceId { get; init; }
    [JsonPropertyName("displayName")] public required string DisplayName { get; init; }

    /// <summary>ECDSA P-256 signature over the challenge nonce. Proves the ID is ours.</summary>
    [JsonPropertyName("proof")] public required string Proof { get; init; }

    /// <summary>Base64 SubjectPublicKeyInfo. Pinned by the server on first registration.</summary>
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }

    /// <summary>Short-lived cloud signature binding the assigned alias to this exact key.</summary>
    [JsonPropertyName("attestation")] public string? CloudAttestation { get; init; }
}

public sealed record RegisteredMessage : SignalingMessage
{
    [JsonPropertyName("deviceId")] public required string DeviceId { get; init; }
    [JsonPropertyName("protocolVersion")] public int ProtocolVersion { get; init; }

    /// <summary>Short-lived connection token; the client must re-register when it expires.</summary>
    [JsonPropertyName("token")] public required string ConnectionToken { get; init; }

    [JsonPropertyName("expiresAt")] public required DateTimeOffset ExpiresAt { get; init; }
    [JsonPropertyName("serverTime")] public required DateTimeOffset ServerTime { get; init; }
    [JsonPropertyName("heartbeatSeconds")] public required int HeartbeatSeconds { get; init; }
    [JsonPropertyName("acceptedClientCapabilities")]
    public required IReadOnlyList<string> AcceptedClientCapabilities { get; init; }
    [JsonPropertyName("serverCapabilities")]
    public required IReadOnlyList<string> ServerCapabilities { get; init; }
    [JsonPropertyName("negotiatedOptionalFeatures")]
    public required IReadOnlyList<string> NegotiatedOptionalFeatures { get; init; }
}

// ---------------------------------------------------------------- session setup

public sealed record SessionRequestMessage : SignalingMessage
{
    /// <summary>Chosen by the requester so it can correlate replies; the server rejects duplicates.</summary>
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }

    [JsonPropertyName("targetId")] public required string TargetId { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("permissions")] public int Permissions { get; init; } = 1;
    [JsonPropertyName("accessKind")] public string AccessKind { get; init; } = "attended";
    [JsonPropertyName("remoteScopeSelection")] public bool RemoteScopeSelectionRequired { get; init; }
    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; init; }
    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; init; }
    [JsonPropertyName("unattendedChallengeId")] public Guid? UnattendedChallengeId { get; init; }
    [JsonPropertyName("unattendedProof")] public string? UnattendedProof { get; init; }
    [JsonPropertyName("supportInvitationToken")] public string? SupportInvitationToken { get; init; }
    [JsonPropertyName("supportInvitationPassword")] public string? SupportInvitationPassword { get; init; }

    /// <summary>Single-use value; the server rejects replays inside its nonce window.</summary>
    [JsonPropertyName("nonce")] public required string Nonce { get; init; }

    [JsonPropertyName("sentAt")] public required DateTimeOffset SentAt { get; init; }
}

public sealed record SessionIncomingMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("fromId")] public required string FromDeviceId { get; init; }
    [JsonPropertyName("fromName")] public required string FromDisplayName { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("permissions")] public int Permissions { get; init; } = 1;
    [JsonPropertyName("accessKind")] public string AccessKind { get; init; } = "attended";
    [JsonPropertyName("remoteScopeSelection")] public bool RemoteScopeSelectionRequired { get; init; }
    [JsonPropertyName("fileRelay")] public bool FileRelay { get; init; }
    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; init; }
    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; init; }
    [JsonPropertyName("fromKeyFingerprint")] public string? FromKeyFingerprint { get; init; }
    [JsonPropertyName("unattendedChallengeId")] public Guid? UnattendedChallengeId { get; init; }
    [JsonPropertyName("unattendedProof")] public string? UnattendedProof { get; init; }
    [JsonPropertyName("supportInvitationToken")] public string? SupportInvitationToken { get; init; }
    [JsonPropertyName("supportInvitationPassword")] public string? SupportInvitationPassword { get; init; }
    [JsonPropertyName("expiresAt")] public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record PermissionDecisionMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }

    /// <summary>accept | decline | block | timeout</summary>
    [JsonPropertyName("decision")] public required string Decision { get; init; }
    [JsonPropertyName("grantedMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GrantedMode { get; init; }
    [JsonPropertyName("grantedPermissions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GrantedPermissions { get; init; }
}

public sealed record PermissionResultMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("decision")] public required string Decision { get; init; }
    [JsonPropertyName("peerName")] public string? PeerDisplayName { get; init; }
    [JsonPropertyName("peerKeyFingerprint")] public string? PeerKeyFingerprint { get; init; }
    [JsonPropertyName("fileRelay")] public bool FileRelay { get; init; }
    [JsonPropertyName("grantedMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GrantedMode { get; init; }
    [JsonPropertyName("grantedPermissions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GrantedPermissions { get; init; }
}

// ---------------------------------------------------------------- media negotiation

public sealed record SdpMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }

    /// <summary>offer | answer</summary>
    [JsonPropertyName("sdpType")] public required string SdpType { get; init; }

    [JsonPropertyName("sdp")] public required string Sdp { get; init; }
}

public sealed record IceCandidateMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("candidate")] public required string Candidate { get; init; }
    [JsonPropertyName("sdpMid")] public string? SdpMid { get; init; }
    [JsonPropertyName("sdpMLineIndex")] public ushort SdpMLineIndex { get; init; }
}

/// <summary>Requests session-scoped ICE servers after the permission decision is accepted.</summary>
public sealed record IceServersRequestMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
}

public sealed record IceServersMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("servers")] public required IReadOnlyList<IceServerDescriptor> Servers { get; init; }
    [JsonPropertyName("relayServerId")] public string? RelayServerId { get; init; }
    [JsonPropertyName("relayRegion")] public string? RelayRegion { get; init; }
    [JsonPropertyName("relayOnly")] public bool RelayOnly { get; init; }
}

public sealed record IceServerDescriptor
{
    [JsonPropertyName("urls")] public required IReadOnlyList<string> Urls { get; init; }
    [JsonPropertyName("username")] public string? Username { get; init; }
    [JsonPropertyName("credential")] public string? Credential { get; init; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Short-lived, participant-bound capability. Never grants a different session mode.</summary>
public sealed record SessionResumeTokenMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("resumeToken")] public required string ResumeToken { get; init; }
    [JsonPropertyName("expiresAt")] public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record SessionResumeMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("resumeToken")] public required string ResumeToken { get; init; }
}

public sealed record SessionResumedMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("resumed")] public required bool Resumed { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
    [JsonPropertyName("resumeExpiresAt")] public DateTimeOffset? ResumeExpiresAt { get; init; }
}

public sealed record SessionQualityMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("captureFps")] public required double CaptureFps { get; init; }
    [JsonPropertyName("encodeFps")] public required double EncodeFps { get; init; }
    [JsonPropertyName("packetLossPercent")] public required double PacketLossPercent { get; init; }
    [JsonPropertyName("jitterMs")] public required double JitterMs { get; init; }
    [JsonPropertyName("availableOutgoingBitrateKbps")] public required double AvailableOutgoingBitrateKbps { get; init; }
    [JsonPropertyName("framesDropped")] public required long FramesDropped { get; init; }
    [JsonPropertyName("sourceWidth")] public int SourceWidth { get; init; }
    [JsonPropertyName("sourceHeight")] public int SourceHeight { get; init; }
    [JsonPropertyName("requestedWidth")] public int RequestedWidth { get; init; }
    [JsonPropertyName("requestedHeight")] public int RequestedHeight { get; init; }
    [JsonPropertyName("encodedWidth")] public int EncodedWidth { get; init; }
    [JsonPropertyName("encodedHeight")] public int EncodedHeight { get; init; }
    [JsonPropertyName("encoderName")] public string? EncoderName { get; init; }
    [JsonPropertyName("encoderHardwareAccelerated")] public bool? EncoderHardwareAccelerated { get; init; }
    [JsonPropertyName("connectionHealth")] public string? ConnectionHealth { get; init; }
    [JsonPropertyName("activeQualityProfile")] public string? ActiveQualityProfile { get; init; }
    [JsonPropertyName("adaptiveQualityLevel")] public int? AdaptiveQualityLevel { get; init; }
    [JsonPropertyName("qualityChangeReason")] public string? QualityChangeReason { get; init; }
    [JsonPropertyName("targetFps")] public int TargetFps { get; init; }
    [JsonPropertyName("targetBitrateKbps")] public int TargetBitrateKbps { get; init; }
    [JsonPropertyName("encoderQueueDepth")] public int EncoderQueueDepth { get; init; }
    [JsonPropertyName("decodeFps")] public double DecodeFps { get; init; }
    [JsonPropertyName("renderFps")] public double RenderFps { get; init; }
    [JsonPropertyName("decodeToRenderLatencyP95Ms")] public double DecodeToRenderLatencyP95Ms { get; init; }
    [JsonPropertyName("captureToPresentLatencyP95Ms")] public double CaptureToPresentLatencyP95Ms { get; init; }
    [JsonPropertyName("frameAgeClockUncertaintyMs")] public double FrameAgeClockUncertaintyMs { get; init; }
    [JsonPropertyName("inputToInjectionLatencyP95Ms")] public double InputToInjectionLatencyP95Ms { get; init; }
    [JsonPropertyName("inputClockUncertaintyMs")] public double InputClockUncertaintyMs { get; init; }
}

// ---------------------------------------------------------------- display selection

/// <summary>
/// Sharer to viewer: which displays this device can share, and which one is live.
/// Sent after capture starts and again after every switch.
/// </summary>
public sealed record SessionDisplaysMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("displays")] public required IReadOnlyList<DisplayDescriptor> Displays { get; init; }
    [JsonPropertyName("activeId")] public required string ActiveDisplayId { get; init; }
}

public sealed record DisplayDescriptor
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }
}

/// <summary>
/// Viewer to sharer: please switch to this display. It is a request, not a command - the
/// sharer only ever honours ids from its own display list, and it grants no new capability.
/// </summary>
public sealed record SelectDisplayMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("displayId")] public required string DisplayId { get; init; }
}

// ---------------------------------------------------------------- address-book presence

public sealed record PresenceQueryMessage : SignalingMessage
{
    [JsonPropertyName("deviceIds")] public required IReadOnlyList<string> DeviceIds { get; init; }
}

public sealed record PresenceResultMessage : SignalingMessage
{
    [JsonPropertyName("inReplyTo")] public required string InReplyTo { get; init; }
    [JsonPropertyName("devices")] public required IReadOnlyList<PresenceDescriptor> Devices { get; init; }
}

public sealed record PresenceDescriptor
{
    [JsonPropertyName("deviceId")] public required string DeviceId { get; init; }
    [JsonPropertyName("online")] public required bool Online { get; init; }
}

public sealed record UnattendedChallengeRequestMessage : SignalingMessage
{
    [JsonPropertyName("targetId")] public required string TargetId { get; init; }
}

public sealed record UnattendedChallengeIncomingMessage : SignalingMessage
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("fromId")] public required string FromDeviceId { get; init; }
    [JsonPropertyName("fromKeyFingerprint")] public required string FromKeyFingerprint { get; init; }
    [JsonPropertyName("expiresAt")] public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record UnattendedChallengeResponseMessage : SignalingMessage
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("requesterId")] public required string RequesterId { get; init; }
    [JsonPropertyName("available")] public required bool Available { get; init; }
    [JsonPropertyName("challengeId")] public Guid? ChallengeId { get; init; }
    [JsonPropertyName("challenge")] public string? Challenge { get; init; }
    [JsonPropertyName("salt")] public string? Salt { get; init; }
    [JsonPropertyName("iterations")] public int Iterations { get; init; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
    [JsonPropertyName("allowedPermissions")] public int? AllowedPermissions { get; init; }
}

public sealed record UnattendedChallengeResultMessage : SignalingMessage
{
    [JsonPropertyName("requestId")] public required string RequestId { get; init; }
    [JsonPropertyName("available")] public required bool Available { get; init; }
    [JsonPropertyName("challengeId")] public Guid? ChallengeId { get; init; }
    [JsonPropertyName("challenge")] public string? Challenge { get; init; }
    [JsonPropertyName("salt")] public string? Salt { get; init; }
    [JsonPropertyName("iterations")] public int Iterations { get; init; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("reasonCode")] public required string ReasonCode { get; init; }
    [JsonPropertyName("allowedPermissions")] public int? AllowedPermissions { get; init; }
}

// ---------------------------------------------------------------- lifecycle

public sealed record SessionEndMessage : SignalingMessage
{
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}

public sealed record PingMessage : SignalingMessage
{
    [JsonPropertyName("sentAt")] public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PongMessage : SignalingMessage
{
    [JsonPropertyName("sentAt")] public required DateTimeOffset SentAt { get; init; }
    [JsonPropertyName("serverTime")] public required DateTimeOffset ServerTime { get; init; }
}

public sealed record ErrorMessage : SignalingMessage
{
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }

    /// <summary>MessageId of the offending message when the server can attribute it.</summary>
    [JsonPropertyName("inReplyTo")] public string? InReplyTo { get; init; }

    [JsonPropertyName("receivedProtocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReceivedProtocolVersion { get; init; }

    [JsonPropertyName("minimumProtocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinimumProtocolVersion { get; init; }

    [JsonPropertyName("maximumProtocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumProtocolVersion { get; init; }

    [JsonPropertyName("capabilitySide")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CapabilitySide { get; init; }

    [JsonPropertyName("requiredCapabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredCapabilities { get; init; }
}

/// <summary>
/// A syntactically valid frame whose discriminator is not known by this protocol revision.
/// It is never serialized; callers ignore it or return unsupported_message without guessing.
/// </summary>
public sealed record UnknownSignalingMessage : SignalingMessage
{
    public required string MessageType { get; init; }
}

public static class SignalingErrorCodes
{
    public const string MalformedMessage = "malformed_message";
    public const string MessageTooLarge = "message_too_large";
    public const string NotRegistered = "not_registered";
    public const string AlreadyRegistered = "already_registered";
    public const string InvalidDeviceId = "invalid_device_id";
    public const string InvalidProof = "invalid_proof";
    public const string InvalidAttestation = "invalid_attestation";
    public const string ChallengeExpired = "challenge_expired";
    public const string TokenExpired = "token_expired";
    public const string RateLimited = "rate_limited";
    public const string ReplayDetected = "replay_detected";
    public const string TargetOffline = "target_offline";
    public const string TargetIsSelf = "target_is_self";
    public const string UnknownSession = "unknown_session";
    public const string UnsupportedMode = "unsupported_mode";
    public const string UnsupportedVersion = "unsupported_version";
    public const string UnsupportedMessage = "unsupported_message";
    public const string InvalidCapabilities = "invalid_capabilities";
    public const string CapabilityMismatch = "capability_mismatch";
    public const string NotSessionParticipant = "not_session_participant";
    public const string TurnUnavailable = "turn_unavailable";
    public const string ResumeRejected = "resume_rejected";
    public const string InvalidSessionState = "invalid_session_state";
    public const string OrganizationPolicyDenied = "organization_policy_denied";
}
