using System.Net.WebSockets;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Signaling.Server.Diagnostics;
using PeerOnQ.Signaling.Server.Registry;
using PeerOnQ.Signaling.Server.Security;
using PeerOnQ.Signaling.Server.Sessions;
using PeerOnQ.Transport.Protocol;
using Microsoft.Extensions.Options;
using PeerOnQ.Shared.Contracts.Security;

namespace PeerOnQ.Signaling.Server;

/// <summary>
/// Drives one signaling socket: handshake, validation, routing and cleanup.
/// The server relays control-plane frames and, for negotiated active file-transfer sessions,
/// opaque encrypted file records. It never sees screen data or file contents, and it never
/// creates a session that the target device has not explicitly accepted.
/// </summary>
public sealed class SignalingConnectionHandler(
    DeviceRegistry devices,
    ISessionStore sessions,
    IUnattendedChallengeStore unattendedChallenges,
    TokenService tokens,
    TurnCredentialService turnCredentials,
    DevicePublicKeyRegistry keys,
    CloudSignalingAttestationValidator attestations,
    ISessionRequestReplayGuard replayGuard,
    IOptions<SignalingOptions> options,
    SignalingMetrics metrics,
    ILogger<SignalingConnectionHandler> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task HandleAsync(WebSocket socket, string connectionId, CancellationToken cancellationToken)
    {
        var connection = new DeviceConnection(
            connectionId,
            socket,
            _time,
            options.Value.MaxPendingSendsPerConnection);
        var limiter = new MessageRateLimiter(options.Value.MessagesPerSecond, options.Value.MessageBurst, _time);
        devices.Add(connection);

        logger.LogInformation("Signaling connection {ConnectionId} opened", connectionId);

        metrics.ConnectionOpened();

        var messageLimit = Math.Clamp(options.Value.MaxMessageBytes, 1024, SignalingCodec.MaxFrameBytes);
        var buffer = new byte[Math.Max(messageLimit, FileRelayFrameCodec.MaxFrameBytes)];

        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var offset = 0;
                ValueWebSocketReceiveResult result;

                do
                {
                    if (offset == buffer.Length)
                    {
                        await SendErrorAsync(connection, SignalingErrorCodes.MessageTooLarge,
                            "WebSocket frame exceeds the permitted limit.", null, cancellationToken);
                        await connection.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame_too_large");
                        return;
                    }

                    result = await socket.ReceiveAsync(buffer.AsMemory(offset), cancellationToken);
                    offset += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                connection.Touch();

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (!FileRelayFrameCodec.TryDecode(buffer.AsSpan(0, offset), out var sessionId, out _))
                    {
                        logger.LogWarning("Malformed file relay frame from {Device}", connection.MaskedId);
                        await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "invalid_file_relay_frame");
                        return;
                    }

                    await RelayFileFrameAsync(connection, sessionId, buffer.AsMemory(0, offset), cancellationToken);
                    continue;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unsupported_frame_type");
                    return;
                }
                if (offset > messageLimit)
                {
                    await SendErrorAsync(connection, SignalingErrorCodes.MessageTooLarge,
                        "Signaling frame exceeds the configured limit.", null, cancellationToken);
                    await connection.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame_too_large");
                    return;
                }

                if (!limiter.TryConsume())
                {
                    logger.LogWarning("Rate limit hit by {Device} on {ConnectionId}",
                        connection.MaskedId, connectionId);
                    await SendErrorAsync(connection, SignalingErrorCodes.RateLimited,
                        "Too many signaling messages.", null, cancellationToken);
                    continue;
                }

                if (!SignalingCodec.TryDecode(buffer.AsSpan(0, offset), out var message, out var error))
                {
                    logger.LogWarning("Malformed frame from {Device}: {Error}", connection.MaskedId, error);
                    await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                        error ?? "Malformed message.", null, cancellationToken);
                    continue;
                }

                await DispatchAsync(connection, message!, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Server shutdown.
        }
        catch (WebSocketException ex)
        {
            logger.LogInformation("Connection {ConnectionId} dropped: {Reason}", connectionId, ex.WebSocketErrorCode);
        }
        finally
        {
            await CleanupAsync(connection);
        }
    }

    private async Task DispatchAsync(DeviceConnection connection, SignalingMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case UnknownSignalingMessage unknown:
                await SendErrorAsync(
                    connection,
                    SignalingErrorCodes.UnsupportedMessage,
                    $"This server does not support that signaling message in protocol v{SignalingProtocol.CurrentVersion}.",
                    unknown.MessageId,
                    ct);
                break;

            case HelloMessage hello:
                await HandleHelloAsync(connection, hello, ct);
                break;

            case RegisterMessage register:
                await HandleRegisterAsync(connection, register, ct);
                break;

            case PingMessage ping when connection.IsRegistered:
                await connection.SendAsync(
                    new PongMessage { SentAt = ping.SentAt, ServerTime = _time.GetUtcNow() }, ct);
                break;

            case SessionRequestMessage request:
                await RequireRegisteredAsync(connection, request, () => HandleSessionRequestAsync(connection, request, ct), ct);
                break;

            case PermissionDecisionMessage decision:
                await RequireRegisteredAsync(connection, decision, () => HandlePermissionAsync(connection, decision, ct), ct);
                break;

            case SdpMessage sdp:
                await RequireRegisteredAsync(connection, sdp, () => RelayAsync(connection, sdp.SessionId, sdp, ct), ct);
                break;

            case IceCandidateMessage ice:
                await RequireRegisteredAsync(connection, ice, () => RelayAsync(connection, ice.SessionId, ice, ct), ct);
                break;

            case IceServersRequestMessage iceServers:
                await RequireRegisteredAsync(connection, iceServers,
                    () => HandleIceServersRequestAsync(connection, iceServers, ct), ct);
                break;

            case SessionResumeMessage resume:
                await RequireRegisteredAsync(connection, resume,
                    () => HandleResumeAsync(connection, resume, ct), ct);
                break;

            case SessionQualityMessage quality:
                await RequireRegisteredAsync(connection, quality,
                    () => HandleQualityAsync(connection, quality, ct), ct);
                break;

            case SessionDisplaysMessage displays:
                await RequireRegisteredAsync(connection, displays,
                    () => RelayAsync(connection, displays.SessionId, displays, ct), ct);
                break;

            case SelectDisplayMessage select:
                await RequireRegisteredAsync(connection, select,
                    () => RelayAsync(connection, select.SessionId, select, ct), ct);
                break;

            case SessionEndMessage end:
                await RequireRegisteredAsync(connection, end, () => HandleSessionEndAsync(connection, end, ct), ct);
                break;

            case PresenceQueryMessage presence:
                await RequireRegisteredAsync(connection, presence, () => HandlePresenceQueryAsync(connection, presence, ct), ct);
                break;

            case UnattendedChallengeRequestMessage unattendedRequest:
                await RequireRegisteredAsync(connection, unattendedRequest,
                    () => HandleUnattendedChallengeRequestAsync(connection, unattendedRequest, ct), ct);
                break;

            case UnattendedChallengeResponseMessage unattendedResponse:
                await RequireRegisteredAsync(connection, unattendedResponse,
                    () => HandleUnattendedChallengeResponseAsync(connection, unattendedResponse, ct), ct);
                break;

            default:
                await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                    $"Unexpected message {message.GetType().Name} in the current state.", message.MessageId, ct);
                break;
        }
    }

    private async Task RequireRegisteredAsync(
        DeviceConnection connection, SignalingMessage message, Func<Task> action, CancellationToken ct)
    {
        if (!connection.IsRegistered)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.NotRegistered,
                "Register before using the session API.", message.MessageId, ct);
            return;
        }

        if (!tokens.Validate(connection.Token, connection.DeviceId!.Value.Value))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.TokenExpired,
                "The connection token expired; register again.", message.MessageId, ct);
            return;
        }

        await action();
    }

    private async Task HandleHelloAsync(DeviceConnection connection, HelloMessage hello, CancellationToken ct)
    {
        if (!SignalingProtocol.IsSupported(hello.ProtocolVersion))
        {
            await SendErrorAsync(
                connection,
                SignalingErrorCodes.UnsupportedVersion,
                $"Signaling protocol {hello.ProtocolVersion} is unsupported; " +
                $"this server supports {SignalingProtocol.MinimumSupportedVersion}-" +
                $"{SignalingProtocol.MaximumSupportedVersion}.",
                hello.MessageId,
                ct,
                receivedProtocolVersion: hello.ProtocolVersion,
                minimumProtocolVersion: SignalingProtocol.MinimumSupportedVersion,
                maximumProtocolVersion: SignalingProtocol.MaximumSupportedVersion);
            return;
        }

        if (!CapabilityNegotiator.TryNormalize(
                hello.ClientCapabilities,
                out var clientCapabilities,
                out var capabilityError))
        {
            await SendErrorAsync(
                connection,
                SignalingErrorCodes.InvalidCapabilities,
                capabilityError,
                hello.MessageId,
                ct,
                capabilitySide: "client");
            return;
        }

        var missingServerCapabilities = CapabilityNegotiator.MissingServerCapabilities(clientCapabilities);
        if (missingServerCapabilities.Count > 0)
        {
            await SendErrorAsync(
                connection,
                SignalingErrorCodes.CapabilityMismatch,
                "The server does not provide all capabilities required by this client.",
                hello.MessageId,
                ct,
                capabilitySide: "server",
                requiredCapabilities: missingServerCapabilities);
            return;
        }

        if (!PeerOnQId.IsValid(hello.DeviceId))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.InvalidDeviceId,
                "Device ID must look like 000-000-000-000.", hello.MessageId, ct);
            return;
        }

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        connection.Challenge = nonce;
        connection.ChallengeExpiresAt = _time.GetUtcNow() + options.Value.ChallengeLifetime;
        connection.DisplayName = Sanitize(hello.DisplayName);
        connection.ClientCapabilities = clientCapabilities;

        await connection.SendAsync(
            new ChallengeMessage { Nonce = nonce, ExpiresAt = connection.ChallengeExpiresAt }, ct);
    }

    private async Task HandleRegisterAsync(DeviceConnection connection, RegisterMessage register, CancellationToken ct)
    {
        if (!PeerOnQId.TryParse(register.DeviceId, out var deviceId))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.InvalidDeviceId,
                "Device ID must look like 000-000-000-000.", register.MessageId, ct);
            return;
        }

        if (connection.Challenge is null)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.NotRegistered,
                "Send hello before register.", register.MessageId, ct);
            return;
        }

        if (connection.ChallengeExpiresAt <= _time.GetUtcNow())
        {
            await SendErrorAsync(connection, SignalingErrorCodes.ChallengeExpired,
                "The challenge expired; start the handshake again.", register.MessageId, ct);
            return;
        }

        if (!keys.VerifySignature(register.PublicKey, connection.Challenge, register.Proof))
        {
            logger.LogWarning("Rejected registration for {Device}: bad signature", deviceId.Masked);
            await SendErrorAsync(connection, SignalingErrorCodes.InvalidProof,
                "The challenge signature did not verify.", register.MessageId, ct);
            return;
        }

        SignalingAttestationClaimsV1? attestationClaims = null;
        if (options.Value.Attestation.Required)
        {
            if (!attestations.TryValidate(
                    register.CloudAttestation,
                    deviceId,
                    register.PublicKey,
                    out attestationClaims,
                    out var attestationError))
            {
                logger.LogWarning(
                    "Rejected registration for {Device}: cloud attestation validation failed ({AttestationError})",
                    deviceId.Masked,
                    attestationError);
                await SendErrorAsync(connection, SignalingErrorCodes.InvalidAttestation,
                    "The cloud signaling attestation is invalid or expired.", register.MessageId, ct);
                return;
            }
        }
        else if (!options.Value.Attestation.AllowDevelopmentTofuFallback
                 || !keys.TryPinOrMatch(deviceId.Value, register.PublicKey))
        {
            logger.LogWarning("Rejected registration for {Device}: public key does not match the pinned key",
                deviceId.Masked);
            await SendErrorAsync(connection, SignalingErrorCodes.InvalidProof,
                "This PeerOnQ ID is pinned to a different key.", register.MessageId, ct);
            return;
        }

        connection.DisplayName = Sanitize(register.DisplayName);
        connection.PublicKeyFingerprint = ComputePublicKeyFingerprint(register.PublicKey);
        connection.OrganizationId = attestationClaims?.OrganizationId;
        connection.OrganizationPolicyFlags = attestationClaims?.OrganizationPolicyFlags ?? SignalingOrganizationPolicyFlags.Unmanaged;
        connection.MinimumClientVersion = attestationClaims?.MinimumClientVersion ?? string.Empty;
        connection.ApprovedRelayRegionsCsv = attestationClaims?.ApprovedRelayRegionsCsv ?? string.Empty;
        connection.Challenge = null;

        await devices.RegisterAsync(connection, deviceId);

        var (token, expiresAt) = tokens.Issue(deviceId.Value);
        connection.Token = token;

        await connection.SendAsync(new RegisteredMessage
        {
            DeviceId = deviceId.Value,
            ProtocolVersion = SignalingProtocol.CurrentVersion,
            ConnectionToken = token,
            ExpiresAt = expiresAt,
            ServerTime = _time.GetUtcNow(),
            HeartbeatSeconds = (int)options.Value.HeartbeatInterval.TotalSeconds,
            AcceptedClientCapabilities = connection.ClientCapabilities!.Capabilities,
            ServerCapabilities = CapabilityNegotiator.ServerCapabilities,
            NegotiatedOptionalFeatures = CapabilityNegotiator.NegotiateServerFeatures(
                connection.ClientCapabilities),
        }, ct);

        logger.LogInformation("Device {Device} registered ({Online} online)", deviceId.Masked, devices.OnlineCount);
    }

    private async Task HandleSessionRequestAsync(
        DeviceConnection connection, SessionRequestMessage request, CancellationToken ct)
    {
        var requester = connection.DeviceId!.Value;

        if (!TryValidateSessionScope(request.Mode, request.Permissions, request.AccessKind))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.UnsupportedMode,
                "The requested session mode, permissions, or access kind is invalid.", request.MessageId, ct);
            return;
        }

        if (request.RemoteScopeSelectionRequired
            && !IsRemoteScopeSelectionEnvelope(request.Mode, request.Permissions, request.AccessKind))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.UnsupportedMode,
                "Remote scope selection requires an attended full-control request.", request.MessageId, ct);
            return;
        }

        if (!QualityProfileWire.TryParse(request.Quality, out var requestedQuality))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                "The requested quality profile is invalid.", request.MessageId, ct);
            return;
        }

        CaptureResolution? requestedResolution = null;
        if (request.Resolution is not null)
        {
            if (!CaptureResolutionWire.TryParse(request.Resolution, out var parsedResolution))
            {
                await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                    "The requested capture resolution is invalid.", request.MessageId, ct);
                return;
            }

            requestedResolution = parsedResolution;
        }

        var hasChallenge = request.UnattendedChallengeId is not null;
        var hasProof = !string.IsNullOrEmpty(request.UnattendedProof);
        var hasSupportToken = !string.IsNullOrEmpty(request.SupportInvitationToken);
        var hasSupportPassword = !string.IsNullOrEmpty(request.SupportInvitationPassword);
        var credentialsValid = request.AccessKind switch
        {
            "attended" => !hasChallenge && !hasProof && !hasSupportToken && !hasSupportPassword,
            "unattended" => hasChallenge == hasProof && !hasSupportToken && !hasSupportPassword,
            "support-invitation" => !hasChallenge && !hasProof && hasSupportToken,
            _ => false,
        };
        if (!credentialsValid
            || (request.UnattendedProof?.Length ?? 0) > 256
            || (request.SupportInvitationToken?.Length ?? 0) > 64
            || (request.SupportInvitationPassword?.Length ?? 0) > 128)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                "The session authentication credential fields are invalid.", request.MessageId, ct);
            return;
        }

        if (!Guid.TryParse(request.SessionId, out _))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                "sessionId must be a GUID.", request.MessageId, ct);
            return;
        }

        if (!PeerOnQId.TryParse(request.TargetId, out var target))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.InvalidDeviceId,
                "The target ID is not a valid PeerOnQ ID.", request.MessageId, ct);
            return;
        }

        if (target == requester)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.TargetIsSelf,
                "A device cannot connect to itself.", request.MessageId, ct);
            return;
        }

        if (!replayGuard.IsWithinClockSkew(request.SentAt))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.ReplayDetected,
                "The request timestamp is outside the accepted window.", request.MessageId, ct);
            return;
        }

        if (!await replayGuard.TryRegisterAsync(requester.Value, request.Nonce, ct))
        {
            logger.LogWarning("Replayed session request from {Device}", requester.Masked);
            await SendErrorAsync(connection, SignalingErrorCodes.ReplayDetected,
                "This request nonce was already used.", request.MessageId, ct);
            return;
        }

        var targetRoute = await devices.ResolveAsync(target, ct);
        if (targetRoute is null)
        {
            logger.LogInformation("Session request from {From} to offline device {To}",
                requester.Masked, target.Masked);
            await SendErrorAsync(connection, SignalingErrorCodes.TargetOffline,
                "That device is not online.", request.MessageId, ct);
            return;
        }

        var sessionCapabilities = CapabilityNegotiator.NegotiateSession(
            connection.ClientCapabilities!,
            targetRoute.ClientCapabilities,
            (SessionPermission)request.Permissions,
            request.AccessKind switch
            {
                "unattended" => SessionAccessKind.Unattended,
                "support-invitation" => SessionAccessKind.SupportInvitation,
                _ => SessionAccessKind.Attended,
            });
        if (!sessionCapabilities.IsCompatible)
        {
            var requesterMissing = sessionCapabilities.MissingRequesterCapabilities;
            var capabilitySide = requesterMissing.Count > 0 ? "requester" : "target";
            var missing = requesterMissing.Count > 0
                ? requesterMissing
                : sessionCapabilities.MissingTargetCapabilities;
            await SendErrorAsync(
                connection,
                SignalingErrorCodes.CapabilityMismatch,
                $"The requested session requires capabilities not advertised by the {capabilitySide}.",
                request.MessageId,
                ct,
                capabilitySide: capabilitySide,
                requiredCapabilities: missing);
            return;
        }

        if (!OrganizationSessionPolicy.AllowsSession(
                connection.OrganizationId, connection.OrganizationPolicyFlags,
                targetRoute.OrganizationId, targetRoute.OrganizationPolicyFlags,
                request.Mode, request.Permissions, request.AccessKind))
        {
            logger.LogWarning("Organization policy denied a session from {From} to {To}", requester.Masked, target.Masked);
            await SendErrorAsync(connection, SignalingErrorCodes.OrganizationPolicyDenied,
                "Organization policy does not allow this session.", request.MessageId, ct);
            return;
        }

        var useFileRelay = sessionCapabilities.NegotiatedOptionalFeatures.Contains(
            OptionalProtocolFeatureNames.FileRelay,
            StringComparer.Ordinal);
        var createResult = await sessions.TryCreateAsync(
            request.SessionId,
            requester,
            target,
            request.Mode,
            request.Permissions,
            request.AccessKind,
            useFileRelay,
            ct,
            request.RemoteScopeSelectionRequired);
        if (!createResult.Created)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                "That session id is already in use.", request.MessageId, ct);
            return;
        }
        var session = createResult.Session;

        await sessions.BindOwnerAsync(session.SessionId, requester, connection.ConnectionId, ct);
        await sessions.BindOwnerAsync(session.SessionId, target, targetRoute.ConnectionId, ct);

        logger.LogInformation("Session {SessionId}: {From} -> {To} awaiting permission",
            session.SessionId, requester.Masked, target.Masked);

        var routed = await devices.SendAsync(target, new SessionIncomingMessage
        {
            SessionId = session.SessionId,
            FromDeviceId = requester.Value,
            FromDisplayName = connection.DisplayName,
            Mode = session.Mode,
            Permissions = session.Permissions,
            AccessKind = session.AccessKind,
            RemoteScopeSelectionRequired = session.RemoteScopeSelectionRequired,
            FileRelay = useFileRelay,
            Quality = QualityProfileWire.Format(requestedQuality),
            Resolution = requestedResolution is { } resolution
                ? CaptureResolutionWire.Format(resolution)
                : null,
            FromKeyFingerprint = connection.PublicKeyFingerprint,
            UnattendedChallengeId = request.UnattendedChallengeId,
            UnattendedProof = request.UnattendedProof,
            SupportInvitationToken = request.SupportInvitationToken,
            SupportInvitationPassword = request.SupportInvitationPassword,
            ExpiresAt = session.PermissionDeadline,
        }, ct);
        if (!routed)
        {
            await sessions.RemoveAsync(session.SessionId, ct);
            await SendErrorAsync(connection, SignalingErrorCodes.TargetOffline,
                "That device went offline before the request could be delivered.", request.MessageId, ct);
        }
    }

    private static bool TryValidateSessionScope(string mode, int rawPermissions, string accessKind)
    {
        var permissions = (PeerOnQ.Domain.Sessions.SessionPermission)rawPermissions;
        PeerOnQ.Domain.Sessions.SessionMode parsedMode;
        try
        {
            parsedMode = mode switch
            {
                "view-only" => PeerOnQ.Domain.Sessions.SessionMode.ViewOnly,
                "full-control" => PeerOnQ.Domain.Sessions.SessionMode.FullControl,
                "file-transfer-only" => PeerOnQ.Domain.Sessions.SessionMode.FileTransferOnly,
                "custom" => PeerOnQ.Domain.Sessions.SessionMode.Custom,
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var parsedAccessKind = accessKind switch
        {
            "attended" => SessionAccessKind.Attended,
            "unattended" => SessionAccessKind.Unattended,
            "support-invitation" => SessionAccessKind.SupportInvitation,
            _ => (SessionAccessKind?)null,
        };

        return parsedAccessKind is { } kind
               && SessionPermissionPolicy.IsValid(parsedMode, permissions)
               && Phase1SessionScope.IsAllowed(parsedMode, permissions, kind);
    }

    private static bool IsRemoteScopeSelectionEnvelope(string mode, int permissions, string accessKind) =>
        accessKind == "attended"
        && mode == "full-control"
        && permissions == (int)Phase1SessionScope.FullControlPermissions;

    private static string ComputePublicKeyFingerprint(string publicKey)
    {
        var digest = SHA256.HashData(Convert.FromBase64String(publicKey));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private async Task HandlePermissionAsync(
        DeviceConnection connection, PermissionDecisionMessage decision, CancellationToken ct)
    {
        var session = await sessions.GetAsync(decision.SessionId, ct);
        if (session is null)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.UnknownSession,
                "Unknown or expired session.", decision.MessageId, ct);
            return;
        }

        // Only the device that was asked may answer.
        if (session.TargetId != connection.DeviceId!.Value)
        {
            logger.LogWarning("Device {Device} tried to answer a session it does not own", connection.MaskedId);
            await SendErrorAsync(connection, SignalingErrorCodes.NotSessionParticipant,
                "Only the target device can answer a permission request.", decision.MessageId, ct);
            return;
        }

        if (decision.Decision is not ("accept" or "decline" or "block" or "timeout"))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                "Unknown permission decision.", decision.MessageId, ct);
            return;
        }

        var accepted = decision.Decision == "accept";
        var grantedMode = session.Mode;
        var grantedPermissions = session.Permissions;
        if (accepted
            && !TryResolveGrantedScope(
                session,
                decision,
                out grantedMode,
                out grantedPermissions))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.UnsupportedMode,
                "The granted session scope is invalid for this request.", decision.MessageId, ct);
            return;
        }

        var transitioned = accepted
            ? await sessions.TryAcceptScopeAsync(session.SessionId, grantedMode, grantedPermissions, ct)
            : await sessions.TryTransitionAsync(
                session.SessionId,
                ServerSessionState.AwaitingPermission,
                ServerSessionState.Ended,
                ct);

        if (!transitioned)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.InvalidSessionState,
                "The permission request has already been resolved.", decision.MessageId, ct);
            return;
        }

        if (accepted)
        {
            await SendResumeTokenAsync(session.RequesterId, session, session.RequesterId, ct);
        }

        await devices.SendAsync(session.RequesterId, new PermissionResultMessage
        {
            SessionId = session.SessionId,
            Decision = decision.Decision,
            PeerDisplayName = connection.DisplayName,
            PeerKeyFingerprint = connection.PublicKeyFingerprint,
            FileRelay = session.FileRelayEnabled,
            GrantedMode = accepted && session.RemoteScopeSelectionRequired ? grantedMode : null,
            GrantedPermissions = accepted && session.RemoteScopeSelectionRequired ? grantedPermissions : null,
        }, ct);

        logger.LogInformation("Session {SessionId} permission: {Decision}", session.SessionId, decision.Decision);

        if (accepted)
        {
            await SendResumeTokenAsync(session.TargetId, session, session.TargetId, ct);
        }

        if (!accepted)
        {
            await sessions.RemoveAsync(session.SessionId, ct);
        }
    }

    private static bool TryResolveGrantedScope(
        ServerSession session,
        PermissionDecisionMessage decision,
        out string grantedMode,
        out int grantedPermissions)
    {
        grantedMode = session.Mode;
        grantedPermissions = session.Permissions;

        var hasGrantedMode = decision.GrantedMode is not null;
        var hasGrantedPermissions = decision.GrantedPermissions is not null;
        if (hasGrantedMode != hasGrantedPermissions) return false;

        if (!session.RemoteScopeSelectionRequired)
            return !hasGrantedMode;

        if (!hasGrantedMode
            || !TryValidateSessionScope(
                decision.GrantedMode!,
                decision.GrantedPermissions!.Value,
                session.AccessKind)
            || !IsRemoteScopeSelectionEnvelope(session.Mode, session.Permissions, session.AccessKind))
        {
            return false;
        }

        var parsedMode = decision.GrantedMode! switch
        {
            "view-only" => SessionMode.ViewOnly,
            "full-control" => SessionMode.FullControl,
            _ => SessionMode.Custom,
        };
        var parsedPermissions = (SessionPermission)decision.GrantedPermissions.Value;
        if (parsedMode is not (SessionMode.ViewOnly or SessionMode.FullControl)
            || parsedPermissions != SessionPermissionPolicy.ForMode(parsedMode)
            || (parsedPermissions & ~(SessionPermission)session.Permissions) != 0)
        {
            return false;
        }

        grantedMode = decision.GrantedMode!;
        grantedPermissions = decision.GrantedPermissions.Value;
        return true;
    }

    private async Task RelayAsync(
        DeviceConnection connection, string sessionId, SignalingMessage message, CancellationToken ct)
    {
        var session = await sessions.GetAsync(sessionId, ct);
        if (session is null)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.UnknownSession,
                "Unknown or expired session.", message.MessageId, ct);
            return;
        }

        var self = connection.DeviceId!.Value;
        if (!session.Involves(self))
        {
            logger.LogWarning("Device {Device} tried to relay into a session it is not part of", connection.MaskedId);
            await SendErrorAsync(connection, SignalingErrorCodes.NotSessionParticipant,
                "You are not part of that session.", message.MessageId, ct);
            return;
        }

        if (!await sessions.IsOwnedByAsync(sessionId, self, connection.ConnectionId, ct))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.NotSessionParticipant,
                "This connection does not own the session. Resume it after reauthentication.", message.MessageId, ct);
            return;
        }

        if (session.State == ServerSessionState.AwaitingPermission)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.NotSessionParticipant,
                "The session has not been accepted yet.", message.MessageId, ct);
            return;
        }

        if (message is SdpMessage) await sessions.MarkActiveAsync(sessionId, ct);

        if (!await devices.SendAsync(session.PeerOf(self), message, ct))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.TargetOffline,
                "The peer went offline.", message.MessageId, ct);
        }
    }

    private async Task HandleSessionEndAsync(
        DeviceConnection connection, SessionEndMessage end, CancellationToken ct)
    {
        var session = await sessions.GetAsync(end.SessionId, ct);
        if (session is null) return;

        var self = connection.DeviceId!.Value;
        if (!session.Involves(self))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.NotSessionParticipant,
                "You are not part of that session.", end.MessageId, ct);
            return;
        }

        await sessions.RemoveAsync(session.SessionId, ct);

        await devices.SendAsync(session.PeerOf(self), end, ct);

        logger.LogInformation("Session {SessionId} ended: {Reason}", session.SessionId, end.Reason);
    }

    private async Task CleanupAsync(DeviceConnection connection)
    {
        var removedCurrentOwner = await devices.RemoveAsync(connection);

        if (removedCurrentOwner && connection.DeviceId is { } deviceId)
        {
            var resumable = await sessions.MarkDisconnectedAsync(deviceId, connection.ConnectionId);

            logger.LogInformation(
                "Device {Device} disconnected; {Count} session(s) entered the resume grace window",
                deviceId.Masked,
                resumable.Count);
        }

        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed");
    }

    private Task SendErrorAsync(
        DeviceConnection connection,
        string code,
        string message,
        string? inReplyTo,
        CancellationToken ct,
        int? receivedProtocolVersion = null,
        int? minimumProtocolVersion = null,
        int? maximumProtocolVersion = null,
        string? capabilitySide = null,
        IReadOnlyList<string>? requiredCapabilities = null)
    {
        metrics.MessageRejected();
        return connection.SendAsync(new ErrorMessage
        {
            Code = code,
            Message = message,
            InReplyTo = inReplyTo,
            ReceivedProtocolVersion = receivedProtocolVersion,
            MinimumProtocolVersion = minimumProtocolVersion,
            MaximumProtocolVersion = maximumProtocolVersion,
            CapabilitySide = capabilitySide,
            RequiredCapabilities = requiredCapabilities,
        }, ct);
    }

    private async Task HandleIceServersRequestAsync(
        DeviceConnection connection,
        IceServersRequestMessage request,
        CancellationToken ct)
    {
        var session = await sessions.GetAsync(request.SessionId, ct);
        if (session is null
            || session.State is ServerSessionState.AwaitingPermission or ServerSessionState.Ended
            || !session.Involves(connection.DeviceId!.Value)
            || !await sessions.IsOwnedByAsync(
                request.SessionId,
                connection.DeviceId.Value,
                connection.ConnectionId,
                ct))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.NotSessionParticipant,
                "ICE servers are issued only to authenticated participants of an accepted session.",
                request.MessageId,
                ct);
            return;
        }

        if (!OrganizationSessionPolicy.AllowsRelayRegion(
                connection.OrganizationId, connection.ApprovedRelayRegionsCsv, options.Value.Turn.Region))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.OrganizationPolicyDenied,
                "Organization policy does not allow this relay region.", request.MessageId, ct);
            return;
        }

        try
        {
            await connection.SendAsync(
                turnCredentials.Issue(request.SessionId, connection.DeviceId.Value),
                ct);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "TURN configuration is not ready");
            await SendErrorAsync(connection, SignalingErrorCodes.TurnUnavailable,
                "Relay configuration is temporarily unavailable.", request.MessageId, ct);
        }
    }

    /// <summary>
    /// Routes an opaque encrypted file record after validating the two active, authorized session
    /// participants. The server deliberately cannot inspect names, file contents, or record keys.
    /// </summary>
    private async Task RelayFileFrameAsync(
        DeviceConnection connection,
        SessionId sessionId,
        ReadOnlyMemory<byte> frame,
        CancellationToken ct)
    {
        if (!connection.IsRegistered || connection.DeviceId is not { } self)
        {
            await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unregistered_file_relay");
            return;
        }

        var session = await sessions.GetAsync(sessionId.ToString(), ct);
        if (session is null
            || !session.FileRelayEnabled
            || !((SessionPermission)session.Permissions).HasFlag(SessionPermission.FileTransfer)
            || session.State is ServerSessionState.AwaitingPermission or ServerSessionState.Ended
            || !session.Involves(self)
            || !await sessions.IsOwnedByAsync(session.SessionId, self, connection.ConnectionId, ct))
        {
            logger.LogWarning("Rejected unauthorized file relay frame from {Device}", connection.MaskedId);
            await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized_file_relay");
            return;
        }

        var peer = session.PeerOf(self);
        var peerRoute = await devices.ResolveAsync(peer, ct);
        if (peerRoute is null
            || !await sessions.IsOwnedByAsync(session.SessionId, peer, peerRoute.ConnectionId, ct))
            return;

        await devices.SendFileRelayAsync(peer, frame, ct);
    }

    private async Task HandlePresenceQueryAsync(
        DeviceConnection connection,
        PresenceQueryMessage query,
        CancellationToken cancellationToken)
    {
        if (query.DeviceIds.Count > 200)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                "A presence query can contain at most 200 device ids.", query.MessageId, cancellationToken);
            return;
        }

        var result = new List<PresenceDescriptor>();
        foreach (var rawId in query.DeviceIds.Distinct(StringComparer.Ordinal))
        {
            if (!PeerOnQId.TryParse(rawId, out var id))
            {
                await SendErrorAsync(connection, SignalingErrorCodes.InvalidDeviceId,
                    "A presence query contains an invalid device id.", query.MessageId, cancellationToken);
                return;
            }
            result.Add(new PresenceDescriptor
            {
                DeviceId = id.Value,
                Online = await devices.IsOnlineAsync(id, cancellationToken),
            });
        }

        await connection.SendAsync(new PresenceResultMessage
        {
            InReplyTo = query.MessageId,
            Devices = result,
        }, cancellationToken);
    }

    private async Task HandleUnattendedChallengeRequestAsync(
        DeviceConnection connection,
        UnattendedChallengeRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requester = connection.DeviceId!.Value;
        if (!PeerOnQId.TryParse(request.TargetId, out var target) || target == requester)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.InvalidDeviceId,
                "The unattended challenge target is invalid.", request.MessageId, cancellationToken);
            return;
        }
        var targetRoute = await devices.ResolveAsync(target, cancellationToken);
        if (targetRoute is null)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.TargetOffline,
                "That device is not online.", request.MessageId, cancellationToken);
            return;
        }
        var lifetime = TimeSpan.FromSeconds(30);
        if (!await unattendedChallenges.TryCreateAsync(
                request.MessageId, requester, target, lifetime, cancellationToken))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.RateLimited,
                "The unattended challenge service is busy.", request.MessageId, cancellationToken);
            return;
        }

        if (!await devices.SendAsync(target, new UnattendedChallengeIncomingMessage
        {
            RequestId = request.MessageId,
            FromDeviceId = requester.Value,
            FromKeyFingerprint = connection.PublicKeyFingerprint ?? string.Empty,
            ExpiresAt = _time.GetUtcNow() + lifetime,
        }, cancellationToken))
        {
            await SendErrorAsync(connection, SignalingErrorCodes.TargetOffline,
                "That device went offline before the challenge could be delivered.", request.MessageId,
                cancellationToken);
        }
    }

    private async Task HandleUnattendedChallengeResponseAsync(
        DeviceConnection connection,
        UnattendedChallengeResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!PeerOnQId.TryParse(response.RequesterId, out var requester)
            || await unattendedChallenges.TryConsumeAsync(
                response.RequestId,
                connection.DeviceId!.Value,
                requester,
                cancellationToken) is null)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.InvalidSessionState,
                "The unattended challenge is unknown, expired, or not owned by this device.",
                response.MessageId,
                cancellationToken);
            return;
        }

        await devices.SendAsync(requester, new UnattendedChallengeResultMessage
        {
            RequestId = response.RequestId,
            Available = response.Available,
            ChallengeId = response.ChallengeId,
            Challenge = response.Challenge,
            Salt = response.Salt,
            Iterations = response.Iterations,
            ExpiresAt = response.ExpiresAt,
            ReasonCode = response.ReasonCode,
            AllowedPermissions = response.AllowedPermissions,
        }, cancellationToken);
    }

    private async Task HandleQualityAsync(
        DeviceConnection connection, SessionQualityMessage quality, CancellationToken ct)
    {
        if (!IsValidFrameRate(quality.CaptureFps)
            || !IsValidFrameRate(quality.EncodeFps)
            || !IsValidFrameRate(quality.DecodeFps)
            || !IsValidFrameRate(quality.RenderFps)
            || !double.IsFinite(quality.PacketLossPercent) || quality.PacketLossPercent is < 0 or > 100
            || !IsValidQualityLatency(quality.JitterMs)
            || !IsValidQualityLatency(quality.DecodeToRenderLatencyP95Ms)
            || !IsValidQualityLatency(quality.CaptureToPresentLatencyP95Ms)
            || !IsValidQualityLatency(quality.FrameAgeClockUncertaintyMs)
            || !IsValidQualityLatency(quality.InputToInjectionLatencyP95Ms)
            || !IsValidQualityLatency(quality.InputClockUncertaintyMs)
            || !double.IsFinite(quality.AvailableOutgoingBitrateKbps)
            || quality.AvailableOutgoingBitrateKbps is < 0 or > 1_000_000_000
            || quality.FramesDropped < 0
            || !IsValidVideoDimension(quality.SourceWidth)
            || !IsValidVideoDimension(quality.SourceHeight)
            || !IsValidVideoDimension(quality.RequestedWidth)
            || !IsValidVideoDimension(quality.RequestedHeight)
            || !IsValidVideoDimension(quality.EncodedWidth)
            || !IsValidVideoDimension(quality.EncodedHeight)
            || !IsSupportedEncoderName(quality.EncoderName)
            || !IsSupportedConnectionHealth(quality.ConnectionHealth)
            || !IsSupportedQualityProfile(quality.ActiveQualityProfile)
            || quality.AdaptiveQualityLevel is < 0 or > 32
            || !IsSupportedQualityReason(quality.QualityChangeReason)
            || quality.TargetFps is < 0 or > 240
            || quality.TargetBitrateKbps is < 0 or > 1_000_000_000
            || quality.EncoderQueueDepth is < 0 or > 10_000)
        {
            await SendErrorAsync(connection, SignalingErrorCodes.MalformedMessage,
                "Quality metrics are outside the supported range.", quality.MessageId, ct);
            return;
        }

        await RelayAsync(connection, quality.SessionId, quality, ct);
    }

    private static bool IsValidVideoDimension(int value) => value is >= 0 and <= 16_384;

    private static bool IsValidFrameRate(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 240;

    private static bool IsValidQualityLatency(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 60_000;

    private static bool IsSupportedEncoderName(string? value) =>
        value is null or "SoftwareVp8" or "HardwareH264";

    private static bool IsSupportedConnectionHealth(string? value) =>
        value is null or "Unknown" or "Excellent" or "Good" or "Fair" or "Poor";

    private static bool IsSupportedQualityProfile(string? value) =>
        value is null or "Automatic" or "Performance" or "Balanced" or "Quality" or "Office" or "LowBandwidth";

    private static bool IsSupportedQualityReason(string? value) =>
        value is null or "awaiting_measurements" or "invalid_telemetry" or "packet_loss"
            or "encoder_backpressure" or "high_rtt" or "high_jitter"
            or "insufficient_bandwidth" or "viewer_input_latency" or "viewer_frame_age" or "viewer_render_latency"
            or "viewer_render_backpressure" or "marginal_conditions" or "stable_headroom" or "stable";

    private async Task HandleResumeAsync(
        DeviceConnection connection,
        SessionResumeMessage resume,
        CancellationToken ct)
    {
        var deviceId = connection.DeviceId!.Value;
        var session = await sessions.TryResumeAsync(
            resume.SessionId,
            deviceId,
            resume.ResumeToken,
            connection.ConnectionId,
            SignalingProtocol.CurrentVersion,
            ct);

        if (session is null || session.State is ServerSessionState.AwaitingPermission or ServerSessionState.Ended)
        {
            metrics.ResumeRejected();
            await connection.SendAsync(new SessionResumedMessage
            {
                SessionId = resume.SessionId,
                Resumed = false,
                Reason = "invalid_or_expired_resume_token",
            }, ct);
            return;
        }

        var replacement = await sessions.IssueResumeTokenAsync(
            resume.SessionId,
            deviceId,
            SignalingProtocol.CurrentVersion,
            ct);
        metrics.ResumeSucceeded();

        await connection.SendAsync(new SessionResumeTokenMessage
        {
            SessionId = resume.SessionId,
            ResumeToken = replacement.Token,
            ExpiresAt = replacement.ExpiresAt,
        }, ct);

        await connection.SendAsync(new SessionResumedMessage
        {
            SessionId = resume.SessionId,
            Resumed = true,
            Reason = "resumed",
            ResumeExpiresAt = replacement.ExpiresAt,
        }, ct);

        // The participant that stayed online must renegotiate too. In particular, when the
        // viewer changes Wi-Fi, only the sharer is allowed to create the ICE-restart offer.
        try
        {
            if (!await devices.SendAsync(session.PeerOf(deviceId), new SessionResumedMessage
            {
                SessionId = resume.SessionId,
                Resumed = true,
                Reason = "peer_resumed",
                ResumeExpiresAt = replacement.ExpiresAt,
            }, ct))
            {
                logger.LogInformation(
                    "Could not notify the online peer that session {SessionId} resumed",
                    resume.SessionId);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            logger.LogInformation(
                "Could not notify the online peer that session {SessionId} resumed",
                resume.SessionId);
        }
    }

    private async Task SendResumeTokenAsync(
        PeerOnQId recipient,
        ServerSession session,
        PeerOnQId deviceId,
        CancellationToken ct)
    {
        var issued = await sessions.IssueResumeTokenAsync(
            session.SessionId,
            deviceId,
            SignalingProtocol.CurrentVersion,
            ct);
        await devices.SendAsync(recipient, new SessionResumeTokenMessage
        {
            SessionId = session.SessionId,
            ResumeToken = issued.Token,
            ExpiresAt = issued.ExpiresAt,
        }, ct);
    }

    private static string Sanitize(string displayName)
    {
        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length > 64) trimmed = trimmed[..64];

        return new string(trimmed.Where(c => !char.IsControl(c)).ToArray());
    }
}
