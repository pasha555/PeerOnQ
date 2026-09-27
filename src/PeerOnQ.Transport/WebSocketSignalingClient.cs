using System.Net.WebSockets;
using System.Net.Security;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Concurrent;
using System.Diagnostics;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Errors;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Transport.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeerOnQ.Transport;

public sealed record SignalingClientOptions
{
    /// <summary>ws:// is accepted for local development only; wss:// everywhere else.</summary>
    public required Uri ServerUri { get; init; }

    public string ClientVersion { get; init; } = "4.0.0";
    public ClientCapabilityManifest? ClientCapabilities { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool AutoReconnect { get; init; } = true;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaxReconnectWindow { get; init; } = TimeSpan.FromSeconds(60);
    public double ReconnectJitterFraction { get; init; } = 0.20;
    public int MaxReconnectAttempts { get; init; } = 5;

    /// <summary>
    /// Only settable for loopback development. Production builds must never disable validation.
    /// </summary>
    public bool AllowInsecureTransport { get; init; }

    /// <summary>
    /// Optional public CA certificate used only to validate an explicitly configured development
    /// WSS server. Hostname validation remains mandatory and the CA is not added to an OS store.
    /// </summary>
    public byte[]? TrustedDevelopmentRootCertificate { get; init; }
}

/// <summary>
/// WebSocket implementation of the control plane. One socket per device; the receive loop
/// translates protocol frames into typed notifications.
/// </summary>
public sealed class WebSocketSignalingClient(
    SignalingClientOptions options,
    IRegistrationProofProvider proofProvider,
    ILogger<WebSocketSignalingClient>? logger = null,
    ISignalingAttestationProvider? attestationProvider = null,
    TimeProvider? timeProvider = null) :
    ISignalingClient,
    IResumableSignalingClient,
    ISessionQualitySignaling,
    IDevicePresenceProvider,
    IUnattendedSignalingClient,
    IFileRelaySignaling
{
    private readonly ILogger _log = logger ?? NullLogger<WebSocketSignalingClient>.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _receiveLoop;
    private Task? _heartbeatLoop;
    private DeviceIdentity? _identity;
    private ClientCapabilityManifest? _capabilities;
    private TaskCompletionSource<ChallengeMessage>? _pendingChallenge;
    private TaskCompletionSource<RegisteredMessage>? _pendingRegistration;
    private readonly ConcurrentDictionary<SessionId, TaskCompletionSource<IceConfiguration>> _pendingIce = new();
    private readonly ConcurrentDictionary<SessionId, TaskCompletionSource<SessionResumeResult>> _pendingResumes = new();
    private readonly ConcurrentDictionary<SessionId, CachedResumeToken> _resumeTokens = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PresenceResultMessage>> _pendingPresence = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<UnattendedChallengeResultMessage>> _pendingUnattendedChallenges = new(StringComparer.Ordinal);
    private SignalingConnectionState _state = SignalingConnectionState.Disconnected;
    private int _disposed;
    private int _reconnecting;
    private int _terminallyReplaced;
    private int _networkMonitoringStarted;
    private int _fileRelayReceivesInProgress;
    private long _lastHeartbeatResponseTimestamp;
    private long _lastFileRelayReceiveCompletionTimestamp;
    private long _serverClockOffsetTicks;
    private long _registrationRefreshAtUtcTicks;

    public SignalingConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public PeerOnQId? RegisteredId { get; private set; }
    public DateTimeOffset? TokenExpiresAt { get; private set; }
    public TimeSpan EstimatedServerClockOffset =>
        TimeSpan.FromTicks(Volatile.Read(ref _serverClockOffsetTicks));
    public IReadOnlyList<string> ServerCapabilities { get; private set; } = [];
    public IReadOnlyList<string> NegotiatedOptionalFeatures { get; private set; } = [];
    public bool IsFileRelayAvailable =>
        State == SignalingConnectionState.Registered
        && NegotiatedOptionalFeatures.Contains(OptionalProtocolFeatureNames.FileRelay, StringComparer.Ordinal);
    public string? LocalKeyFingerprint
    {
        get
        {
            if (string.IsNullOrEmpty(_identity?.PublicKey)) return null;
            try
            {
                return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Convert.FromBase64String(_identity.PublicKey))).ToLowerInvariant();
            }
            catch (FormatException) { return null; }
        }
    }

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
    public event EventHandler<UnattendedChallengeRequestNotification>? UnattendedChallengeRequested;
    public event EventHandler<SessionPeerResumedNotification>? PeerResumed;
    public event EventHandler<FileRelayFrame>? FileRelayReceived;

    public async Task ConnectAsync(DeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);

        if (options.ServerUri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException($"Unsupported signaling scheme '{options.ServerUri.Scheme}'.");
        }

        if (options.ServerUri.Scheme == "ws" && !options.AllowInsecureTransport && !IsLoopback(options.ServerUri))
        {
            throw new InvalidOperationException(
                "Plain ws:// is only allowed for loopback development. Use wss:// or set AllowInsecureTransport.");
        }
        if (options.TrustedDevelopmentRootCertificate is not null && options.ServerUri.Scheme != "wss")
        {
            throw new InvalidOperationException("A pinned development root certificate requires WSS.");
        }
        if (options.HeartbeatInterval <= TimeSpan.Zero
            || options.HeartbeatTimeout <= options.HeartbeatInterval)
        {
            throw new InvalidOperationException(
                "The signaling heartbeat timeout must be greater than its positive interval.");
        }

        if (!CapabilityNegotiator.TryNormalize(options.ClientCapabilities, out var capabilities, out var capabilityError))
        {
            throw new InvalidOperationException($"Invalid client capability manifest: {capabilityError}");
        }

        _identity = identity;
        _capabilities = capabilities;
        Interlocked.Exchange(ref _terminallyReplaced, 0);
        StartNetworkMonitoring();
        await OpenAndRegisterAsync(cancellationToken);
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1" or "::1";

    internal static bool ValidateDevelopmentServerCertificate(
        X509Certificate? certificate,
        SslPolicyErrors errors,
        ReadOnlySpan<byte> trustedRootCertificate)
    {
        if (certificate is null
            || trustedRootCertificate.IsEmpty
            || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;

        using var trustedRoot = X509CertificateLoader.LoadCertificate(trustedRootCertificate);
        using var serverCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(serverCertificate);
    }

    private async Task OpenAndRegisterAsync(CancellationToken cancellationToken)
    {
        var identity = _identity ?? throw new InvalidOperationException("ConnectAsync was not called.");
        var capabilities = _capabilities
                           ?? throw new InvalidOperationException("ConnectAsync did not validate client capabilities.");

        // Tear the previous attempt down first. Leaving its receive and heartbeat loops alive
        // makes the server displace one connection with the next, which in turn looks like a
        // drop to the stale loop and starts another reconnect - an endless cascade.
        await AbandonCurrentConnectionAsync();

        State = SignalingConnectionState.Connecting;

        var socket = new ClientWebSocket();
        // The authenticated application heartbeat below remains the signaling liveness source.
        // Do not enable ClientWebSocket's independent PING timeout: bounded file-relay dispatch
        // intentionally blocks this receive loop during final integrity/malware work, which can
        // otherwise make a queued PONG tear down an unrelated active screen session.
        socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        if (options.TrustedDevelopmentRootCertificate is { Length: > 0 } trustedRoot)
        {
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                ValidateDevelopmentServerCertificate(certificate, errors, trustedRoot);
        }

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(options.ConnectTimeout);

        try
        {
            await socket.ConnectAsync(options.ServerUri, connectCts.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            socket.Dispose();
            State = SignalingConnectionState.Faulted;
            throw new PeerOnQException(
                $"Cannot reach the signaling server at {options.ServerUri}. " +
                "Check the address, that the server is running, and the local firewall.", ex);
        }

        _socket = socket;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token), CancellationToken.None);

        State = SignalingConnectionState.Registering;

        var registered = await RegisterCurrentConnectionAsync(identity, capabilities, cancellationToken);
        ApplyRegistration(registered, capabilities);
        State = SignalingConnectionState.Registered;

        _heartbeatLoop = Task.Run(
            () => HeartbeatLoopAsync(socket, _lifetime.Token),
            CancellationToken.None);

        _log.LogInformation("Registered with signaling server as {Device}", identity.ToLogString());
    }

    private async Task<RegisteredMessage> RegisterCurrentConnectionAsync(
        DeviceIdentity identity,
        ClientCapabilityManifest capabilities,
        CancellationToken cancellationToken)
    {
        _pendingChallenge = new TaskCompletionSource<ChallengeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRegistration = new TaskCompletionSource<RegisteredMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        await SendAsync(new HelloMessage
        {
            DeviceId = identity.PublicId.Value,
            DisplayName = identity.DisplayName,
            ClientVersion = options.ClientVersion,
            ProtocolVersion = SignalingProtocol.CurrentVersion,
            ClientCapabilities = capabilities,
        }, cancellationToken);

        var challenge = await WithTimeout(_pendingChallenge.Task, options.HandshakeTimeout, "challenge", cancellationToken);
        var proof = await proofProvider.ComputeRegistrationProofAsync(challenge.Nonce, cancellationToken);
        var cloudAttestation = attestationProvider is null
            ? null
            : await attestationProvider.GetSignalingAttestationAsync(cancellationToken);

        await SendAsync(new RegisterMessage
        {
            DeviceId = identity.PublicId.Value,
            DisplayName = identity.DisplayName,
            Proof = proof,
            PublicKey = identity.PublicKey
                        ?? throw new PeerOnQException("The device identity has no public key; re-provision the device."),
            CloudAttestation = cloudAttestation,
        }, cancellationToken);

        return await WithTimeout(_pendingRegistration.Task, options.HandshakeTimeout, "registration", cancellationToken);
    }

    private void ApplyRegistration(RegisteredMessage registered, ClientCapabilityManifest capabilities)
    {
        if (registered.ProtocolVersion != SignalingProtocol.CurrentVersion)
        {
            State = SignalingConnectionState.Faulted;
            throw new SignalingProtocolException(
                $"Signaling protocol mismatch: client {SignalingProtocol.CurrentVersion}, " +
                $"server {registered.ProtocolVersion}.");
        }

        if (!registered.AcceptedClientCapabilities.ToHashSet(StringComparer.Ordinal)
                .SetEquals(capabilities.Capabilities))
        {
            State = SignalingConnectionState.Faulted;
            throw new SignalingProtocolException(
                "The signaling server did not accept the exact advertised client capability set.");
        }

        var missingServerCapabilities = capabilities.RequiredServerCapabilities
            .Except(registered.ServerCapabilities, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missingServerCapabilities.Length > 0)
        {
            State = SignalingConnectionState.Faulted;
            throw new SignalingProtocolException(
                $"The signaling server is missing required capabilities: {string.Join(", ", missingServerCapabilities)}.");
        }

        var unexpectedFeatures = registered.NegotiatedOptionalFeatures
            .Except(capabilities.OptionalFeatures, StringComparer.Ordinal)
            .ToArray();
        if (unexpectedFeatures.Length > 0)
        {
            State = SignalingConnectionState.Faulted;
            throw new SignalingProtocolException(
                "The signaling server negotiated an optional feature that the client did not advertise.");
        }

        UpdateServerClockOffset(registered.ServerTime);
        RegisteredId = PeerOnQId.Parse(registered.DeviceId);
        TokenExpiresAt = registered.ExpiresAt;
        ServerCapabilities = registered.ServerCapabilities.ToArray();
        NegotiatedOptionalFeatures = registered.NegotiatedOptionalFeatures.ToArray();
        Interlocked.Exchange(
            ref _registrationRefreshAtUtcTicks,
            CalculateRegistrationRefreshAt(
                registered.ServerTime,
                registered.ExpiresAt,
                options.HeartbeatInterval,
                options.HandshakeTimeout).UtcTicks);
        Volatile.Write(ref _lastHeartbeatResponseTimestamp, Stopwatch.GetTimestamp());
    }

    /// <summary>Cancels and disposes whatever connection this client currently holds.</summary>
    private async Task AbandonCurrentConnectionAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        var socket = Interlocked.Exchange(ref _socket, null);
        var receiveLoop = Interlocked.Exchange(ref _receiveLoop, null);
        var heartbeatLoop = Interlocked.Exchange(ref _heartbeatLoop, null);

        if (lifetime is not null)
        {
            await lifetime.CancelAsync();
        }

        socket?.Dispose();

        foreach (var loop in new[] { receiveLoop, heartbeatLoop })
        {
            if (loop is null) continue;

            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or WebSocketException)
            {
                // The loop is unwinding on the disposed socket; nothing else to wait for.
            }
        }

        lifetime?.Dispose();
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new PeerOnQException($"The signaling server did not send a {what} within {timeout.TotalSeconds:0}s.");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var socket = _socket;
        _lifetime?.Cancel();

        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // The peer may already be gone; nothing else to do.
            }
        }

        State = SignalingConnectionState.Disconnected;
    }

    public Task RequestSessionAsync(SessionId sessionId, PeerOnQId target, SessionMode mode, CancellationToken cancellationToken = default) =>
        RequestSessionAsync(
            sessionId,
            target,
            mode,
            SessionPermissionPolicy.ForMode(mode),
            SessionAccessKind.Attended,
            cancellationToken);

    public Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        CancellationToken cancellationToken = default) =>
        RequestSessionAsync(
            sessionId,
            target,
            mode,
            permissions,
            accessKind,
            unattendedChallengeId: null,
            unattendedProof: null,
            cancellationToken);

    public Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        Guid? unattendedChallengeId,
        string? unattendedProof,
        CancellationToken cancellationToken = default) =>
        RequestSessionAsync(
            sessionId,
            target,
            mode,
            permissions,
            accessKind,
            unattendedChallengeId,
            unattendedProof,
            QualityProfile.Automatic,
            cancellationToken);

    public Task RequestSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        Guid? unattendedChallengeId,
        string? unattendedProof,
        QualityProfile quality,
        CancellationToken cancellationToken = default) =>
        SendAsync(new SessionRequestMessage
        {
            SessionId = sessionId.ToString(),
            TargetId = target.Value,
            Mode = ToWire(mode),
            Permissions = (int)permissions,
            AccessKind = ToWire(accessKind),
            Quality = QualityProfileWire.Format(quality),
            UnattendedChallengeId = unattendedChallengeId,
            UnattendedProof = unattendedProof,
            Nonce = Guid.NewGuid().ToString("N"),
            SentAt = GetServerUtcNow(),
        }, cancellationToken);

    public Task RequestSupportSessionAsync(
        SessionId sessionId,
        PeerOnQId target,
        SessionMode mode,
        SessionPermission permissions,
        string invitationToken,
        string? invitationPassword,
        QualityProfile quality,
        CaptureResolution resolution,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invitationToken))
            throw new ArgumentException("A support invitation token is required.", nameof(invitationToken));

        return SendAsync(new SessionRequestMessage
        {
            SessionId = sessionId.ToString(),
            TargetId = target.Value,
            Mode = ToWire(mode),
            Permissions = (int)permissions,
            AccessKind = ToWire(SessionAccessKind.SupportInvitation),
            Quality = QualityProfileWire.Format(quality),
            Resolution = CaptureResolutionWire.Format(resolution),
            SupportInvitationToken = invitationToken,
            SupportInvitationPassword = invitationPassword,
            Nonce = Guid.NewGuid().ToString("N"),
            SentAt = GetServerUtcNow(),
        }, cancellationToken);
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
        CancellationToken cancellationToken = default) =>
        SendAsync(new SessionRequestMessage
        {
            SessionId = sessionId.ToString(),
            TargetId = target.Value,
            Mode = ToWire(mode),
            Permissions = (int)permissions,
            AccessKind = ToWire(accessKind),
            Quality = QualityProfileWire.Format(quality),
            Resolution = CaptureResolutionWire.Format(resolution),
            UnattendedChallengeId = unattendedChallengeId,
            UnattendedProof = unattendedProof,
            Nonce = Guid.NewGuid().ToString("N"),
            SentAt = GetServerUtcNow(),
        }, cancellationToken);

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
        CancellationToken cancellationToken = default) =>
        SendAsync(new SessionRequestMessage
        {
            SessionId = sessionId.ToString(),
            TargetId = target.Value,
            Mode = ToWire(mode),
            Permissions = (int)permissions,
            AccessKind = ToWire(accessKind),
            RemoteScopeSelectionRequired = remoteScopeSelectionRequired,
            Quality = QualityProfileWire.Format(quality),
            Resolution = CaptureResolutionWire.Format(resolution),
            UnattendedChallengeId = unattendedChallengeId,
            UnattendedProof = unattendedProof,
            Nonce = Guid.NewGuid().ToString("N"),
            SentAt = GetServerUtcNow(),
        }, cancellationToken);

    public Task SendPermissionDecisionAsync(SessionId sessionId, PermissionDecision decision, CancellationToken cancellationToken = default) =>
        SendAsync(new PermissionDecisionMessage
        {
            SessionId = sessionId.ToString(),
            Decision = ToWire(decision),
        }, cancellationToken);

    public Task SendPermissionDecisionAsync(
        SessionId sessionId,
        PermissionDecision decision,
        SessionMode grantedMode,
        SessionPermission grantedPermissions,
        CancellationToken cancellationToken = default) =>
        SendAsync(new PermissionDecisionMessage
        {
            SessionId = sessionId.ToString(),
            Decision = ToWire(decision),
            GrantedMode = ToWire(grantedMode),
            GrantedPermissions = (int)grantedPermissions,
        }, cancellationToken);

    public Task SendSdpAsync(SessionId sessionId, string sdpType, string sdp, CancellationToken cancellationToken = default) =>
        SendAsync(new SdpMessage { SessionId = sessionId.ToString(), SdpType = sdpType, Sdp = sdp }, cancellationToken);

    public Task SendIceAsync(SessionId sessionId, string candidate, string? sdpMid, ushort sdpMLineIndex, CancellationToken cancellationToken = default) =>
        SendAsync(new IceCandidateMessage
        {
            SessionId = sessionId.ToString(),
            Candidate = candidate,
            SdpMid = sdpMid,
            SdpMLineIndex = sdpMLineIndex,
        }, cancellationToken);

    public async Task EndSessionAsync(SessionId sessionId, SessionEndReason reason, CancellationToken cancellationToken = default)
    {
        try
        {
            await SendAsync(
                new SessionEndMessage { SessionId = sessionId.ToString(), Reason = reason.ToString() },
                cancellationToken);
        }
        finally
        {
            _resumeTokens.TryRemove(sessionId, out _);
        }
    }

    public async Task<IceConfiguration> GetIceConfigurationAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var pending = new TaskCompletionSource<IceConfiguration>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingIce.TryAdd(sessionId, pending))
        {
            throw new InvalidOperationException("An ICE server request is already pending for this session.");
        }

        try
        {
            await SendAsync(new IceServersRequestMessage { SessionId = sessionId.ToString() }, cancellationToken);
            return await pending.Task.WaitAsync(options.HandshakeTimeout, cancellationToken);
        }
        finally
        {
            _pendingIce.TryRemove(sessionId, out _);
        }
    }

    public async Task<SessionResumeResult> ResumeSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!_resumeTokens.TryGetValue(sessionId, out var cached)
            || cached.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new SessionResumeResult(sessionId, false, "missing_or_expired_resume_token", cached?.ExpiresAt);
        }

        var pending = new TaskCompletionSource<SessionResumeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingResumes.TryAdd(sessionId, pending))
        {
            throw new InvalidOperationException("A resume request is already pending for this session.");
        }

        try
        {
            await SendAsync(new SessionResumeMessage
            {
                SessionId = sessionId.ToString(),
                ResumeToken = cached.Token,
            }, cancellationToken);

            return await pending.Task.WaitAsync(options.HandshakeTimeout, cancellationToken);
        }
        finally
        {
            _pendingResumes.TryRemove(sessionId, out _);
        }
    }

    public Task SendQualityAsync(
        SessionQualityNotification quality,
        CancellationToken cancellationToken = default) =>
        SendAsync(new SessionQualityMessage
        {
            SessionId = quality.SessionId.ToString(),
            CaptureFps = quality.CaptureFps,
            EncodeFps = quality.EncodeFps,
            PacketLossPercent = quality.PacketLossPercent,
            JitterMs = quality.JitterMs,
            AvailableOutgoingBitrateKbps = quality.AvailableOutgoingBitrateKbps,
            FramesDropped = quality.FramesDropped,
            SourceWidth = quality.SourceWidth,
            SourceHeight = quality.SourceHeight,
            RequestedWidth = quality.RequestedWidth,
            RequestedHeight = quality.RequestedHeight,
            EncodedWidth = quality.EncodedWidth,
            EncodedHeight = quality.EncodedHeight,
            EncoderName = quality.EncoderName,
            EncoderHardwareAccelerated = quality.EncoderHardwareAccelerated,
            ConnectionHealth = quality.ConnectionHealth.ToString(),
            ActiveQualityProfile = quality.ActiveQualityProfile?.ToString(),
            AdaptiveQualityLevel = quality.AdaptiveQualityLevel,
            QualityChangeReason = quality.QualityChangeReason,
            TargetFps = quality.TargetFps,
            TargetBitrateKbps = quality.TargetBitrateKbps,
            EncoderQueueDepth = quality.EncoderQueueDepth,
            DecodeFps = quality.DecodeFps,
            RenderFps = quality.RenderFps,
            DecodeToRenderLatencyP95Ms = quality.DecodeToRenderLatencyP95Ms,
            CaptureToPresentLatencyP95Ms = quality.CaptureToPresentLatencyP95Ms,
            FrameAgeClockUncertaintyMs = quality.FrameAgeClockUncertaintyMs,
            InputToInjectionLatencyP95Ms = quality.InputToInjectionLatencyP95Ms,
            InputClockUncertaintyMs = quality.InputClockUncertaintyMs,
        }, cancellationToken);

    public Task SendDisplaysAsync(
        SessionId sessionId,
        IReadOnlyList<CaptureTargetInfo> displays,
        string activeDisplayId,
        CancellationToken cancellationToken = default) =>
        SendAsync(new SessionDisplaysMessage
        {
            SessionId = sessionId.ToString(),
            ActiveDisplayId = activeDisplayId,
            Displays = displays
                .Select(d => new DisplayDescriptor
                {
                    Id = d.Id,
                    Name = d.DisplayName,
                    Width = d.Width,
                    Height = d.Height,
                })
                .ToArray(),
        }, cancellationToken);

    public Task RequestDisplayAsync(SessionId sessionId, string displayId, CancellationToken cancellationToken = default) =>
        SendAsync(new SelectDisplayMessage
        {
            SessionId = sessionId.ToString(),
            DisplayId = displayId,
        }, cancellationToken);

    public async Task<IReadOnlyDictionary<PeerOnQId, DevicePresence>> QueryAsync(
        IReadOnlyList<PeerOnQId> deviceIds,
        CancellationToken cancellationToken = default)
    {
        if (deviceIds.Count == 0) return new Dictionary<PeerOnQId, DevicePresence>();
        if (deviceIds.Count > 200) throw new ArgumentOutOfRangeException(nameof(deviceIds), "At most 200 devices can be queried.");
        if (State != SignalingConnectionState.Registered)
            return deviceIds.Distinct().ToDictionary(id => id, _ => DevicePresence.Unknown);

        var query = new PresenceQueryMessage { DeviceIds = deviceIds.Distinct().Select(id => id.Value).ToArray() };
        var pending = new TaskCompletionSource<PresenceResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingPresence.TryAdd(query.MessageId, pending)) throw new InvalidOperationException("Duplicate presence request id.");
        try
        {
            await SendAsync(query, cancellationToken);
            var result = await pending.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return result.Devices.ToDictionary(
                item => PeerOnQId.Parse(item.DeviceId),
                item => item.Online ? DevicePresence.Online : DevicePresence.Offline);
        }
        finally
        {
            _pendingPresence.TryRemove(query.MessageId, out _);
        }
    }

    public async Task<UnattendedChallenge> RequestUnattendedChallengeAsync(
        PeerOnQId target,
        CancellationToken cancellationToken = default)
    {
        var request = new UnattendedChallengeRequestMessage { TargetId = target.Value };
        var pending = new TaskCompletionSource<UnattendedChallengeResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingUnattendedChallenges.TryAdd(request.MessageId, pending))
            throw new InvalidOperationException("Duplicate unattended challenge request id.");
        try
        {
            await SendAsync(request, cancellationToken);
            var result = await pending.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            byte[]? salt = null;
            if (result.Salt is not null)
            {
                try { salt = Convert.FromBase64String(result.Salt); }
                catch (FormatException) { throw new SignalingProtocolException("The unattended challenge salt is malformed."); }
            }
            return new UnattendedChallenge(
                result.RequestId,
                result.Available,
                result.ChallengeId,
                result.Challenge,
                salt,
                result.Iterations,
                result.ExpiresAt,
                result.ReasonCode,
                ParseUnattendedPermissions(result.AllowedPermissions));
        }
        finally
        {
            _pendingUnattendedChallenges.TryRemove(request.MessageId, out _);
        }
    }

    public Task SendUnattendedChallengeResponseAsync(
        string requestId,
        PeerOnQId requester,
        UnattendedChallenge challenge,
        CancellationToken cancellationToken = default) =>
        SendAsync(new UnattendedChallengeResponseMessage
        {
            RequestId = requestId,
            RequesterId = requester.Value,
            Available = challenge.Available,
            ChallengeId = challenge.ChallengeId,
            Challenge = challenge.Challenge,
            Salt = challenge.Salt is null ? null : Convert.ToBase64String(challenge.Salt),
            Iterations = challenge.Iterations,
            ExpiresAt = challenge.ExpiresAt,
            ReasonCode = challenge.ReasonCode,
            AllowedPermissions = challenge.AllowedPermissions is { } permissions
                ? (int)permissions
                : null,
        }, cancellationToken);

    private static SessionPermission? ParseUnattendedPermissions(int? raw)
    {
        if (raw is null) return null;

        var permissions = (SessionPermission)raw.Value;
        if (!SessionPermissionPolicy.IsValid(SessionMode.ViewOnly, permissions)
            && !SessionPermissionPolicy.IsValid(SessionMode.FullControl, permissions)
            && !SessionPermissionPolicy.IsValid(SessionMode.FileTransferOnly, permissions))
        {
            throw new SignalingProtocolException(
                "The remote unattended permission scope is malformed.");
        }

        return permissions;
    }

    public async Task SendAsync(SignalingMessage message, CancellationToken cancellationToken = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("The signaling client is not connected.");
        var payload = SignalingCodec.Encode(message);

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                throw new PeerOnQException("The signaling connection is not open.");
            }

            await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task SendFileRelayAsync(
        SessionId sessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (!IsFileRelayAvailable)
            throw new InvalidOperationException("The authenticated file relay is not available for this connection.");

        var socket = _socket ?? throw new InvalidOperationException("The signaling client is not connected.");
        var frame = FileRelayFrameCodec.Encode(sessionId, payload.Span);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
                throw new PeerOnQException("The signaling connection is not open.");

            await socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket!;
        var buffer = new byte[Math.Max(SignalingCodec.MaxFrameBytes, FileRelayFrameCodec.MaxFrameBytes)];
        var reconnectAfterLoop = true;

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
                        throw new SignalingProtocolException("Signaling frame exceeded the size limit.");
                    }

                    result = await socket.ReceiveAsync(buffer.AsMemory(offset), cancellationToken);
                    offset += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    reconnectAfterLoop = ShouldReconnectAfterServerClose(
                        socket.CloseStatus,
                        socket.CloseStatusDescription);
                    // Complete the close handshake, otherwise the peer's CloseAsync blocks
                    // until its own timeout.
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                    if (!reconnectAfterLoop && ReferenceEquals(socket, _socket))
                    {
                        Interlocked.Exchange(ref _terminallyReplaced, 1);
                        _lifetime?.Cancel();
                        State = SignalingConnectionState.Disconnected;
                        _log.LogInformation(
                            "Signaling connection was replaced by a newer client instance; automatic reconnect stopped");
                    }
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (!FileRelayFrameCodec.TryDecode(buffer.AsSpan(0, offset), out var sessionId, out var payload))
                    {
                        _log.LogWarning("Discarding malformed authenticated file relay frame.");
                        continue;
                    }

                    // File processing deliberately applies bounded backpressure. Its final
                    // integrity/malware verification can take longer than the heartbeat window,
                    // during which this receive loop cannot read PONGs. Mark that interval so
                    // the heartbeat does not mistake its own queued PONG for a disconnected
                    // screen-sharing session.
                    Interlocked.Increment(ref _fileRelayReceivesInProgress);
                    try
                    {
                        FileRelayReceived?.Invoke(this, new FileRelayFrame(sessionId, payload));
                    }
                    finally
                    {
                        Volatile.Write(
                            ref _lastFileRelayReceiveCompletionTimestamp,
                            Stopwatch.GetTimestamp());
                        Interlocked.Decrement(ref _fileRelayReceivesInProgress);
                    }
                    continue;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                    throw new SignalingProtocolException("The signaling server sent an unsupported WebSocket frame type.");
                if (offset > SignalingCodec.MaxFrameBytes)
                    throw new SignalingProtocolException("Signaling frame exceeded the size limit.");

                Dispatch(buffer.AsSpan(0, offset));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Signaling receive loop stopped");
            FailPending(ex);
        }

        // A loop from a previous connection must never trigger a reconnect: its socket has
        // already been replaced.
        if (reconnectAfterLoop
            && ShouldScheduleReconnect(
                _disposed == 1,
                Volatile.Read(ref _terminallyReplaced) == 1,
                cancellationToken.IsCancellationRequested,
                ReferenceEquals(socket, _socket)))
        {
            // Leave the receive loop before replacing the socket. Reconnect cleanup waits for
            // this task, so awaiting it here would add the cleanup timeout to every recovery.
            _ = Task.Run(HandleDropAsync, CancellationToken.None);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> frame)
    {
        if (!SignalingCodec.TryDecode(frame, out var message, out var error))
        {
            _log.LogWarning("Discarding malformed signaling frame: {Error}", error);
            return;
        }

        switch (message)
        {
            case ChallengeMessage challenge:
                _pendingChallenge?.TrySetResult(challenge);
                break;

            case RegisteredMessage registered:
                _pendingRegistration?.TrySetResult(registered);
                break;

            case SessionIncomingMessage incoming when SessionId.TryParse(incoming.SessionId, out var incomingId):
                var incomingMode = FromWireMode(incoming.Mode);
                var incomingPermissions = (SessionPermission)incoming.Permissions;
                if (!SessionPermissionPolicy.IsValid(incomingMode, incomingPermissions))
                    throw new SignalingProtocolException("The server supplied an invalid session permission set.");
                SessionRequested?.Invoke(this, new IncomingSessionNotification(
                    incomingId,
                    PeerOnQId.Parse(incoming.FromDeviceId),
                    incoming.FromDisplayName,
                    incomingMode,
                    incoming.ExpiresAt,
                    incomingPermissions,
                    FromWireAccessKind(incoming.AccessKind),
                    incoming.FromKeyFingerprint,
                    incoming.UnattendedChallengeId,
                    incoming.UnattendedProof,
                    QualityProfileWire.Parse(incoming.Quality),
                    incoming.Resolution is null
                        ? null
                        : CaptureResolutionWire.Parse(incoming.Resolution),
                    incoming.SupportInvitationToken,
                    incoming.SupportInvitationPassword,
                    incoming.FileRelay,
                    incoming.RemoteScopeSelectionRequired));
                break;

            case UnattendedChallengeIncomingMessage challengeIncoming:
                if (challengeIncoming.FromKeyFingerprint.Length == 64)
                {
                    UnattendedChallengeRequested?.Invoke(this, new UnattendedChallengeRequestNotification(
                        challengeIncoming.RequestId,
                        PeerOnQId.Parse(challengeIncoming.FromDeviceId),
                        challengeIncoming.FromKeyFingerprint,
                        challengeIncoming.ExpiresAt));
                }
                break;

            case UnattendedChallengeResultMessage challengeResult:
                if (_pendingUnattendedChallenges.TryGetValue(challengeResult.RequestId, out var pendingChallenge))
                    pendingChallenge.TrySetResult(challengeResult);
                break;

            case PermissionResultMessage permission when SessionId.TryParse(permission.SessionId, out var permissionId):
                var hasGrantedMode = permission.GrantedMode is not null;
                var hasGrantedPermissions = permission.GrantedPermissions is not null;
                if (hasGrantedMode != hasGrantedPermissions)
                    throw new SignalingProtocolException("The server supplied an incomplete granted session scope.");

                SessionMode? grantedMode = null;
                SessionPermission? grantedPermissions = null;
                if (hasGrantedMode)
                {
                    grantedMode = FromWireMode(permission.GrantedMode!);
                    grantedPermissions = (SessionPermission)permission.GrantedPermissions!.Value;
                    if (!SessionPermissionPolicy.IsValid(grantedMode.Value, grantedPermissions.Value))
                    {
                        throw new SignalingProtocolException(
                            "The server supplied an invalid granted session permission set.");
                    }
                }
                PermissionResolved?.Invoke(this, new PermissionResultNotification(
                    permissionId,
                    FromWireDecision(permission.Decision),
                    permission.PeerDisplayName,
                    permission.PeerKeyFingerprint,
                    permission.FileRelay,
                    grantedMode,
                    grantedPermissions));
                break;

            case SdpMessage sdp when SessionId.TryParse(sdp.SessionId, out var sdpSession):
                SdpReceived?.Invoke(this, new SdpNotification(sdpSession, sdp.SdpType, sdp.Sdp));
                break;

            case IceCandidateMessage ice when SessionId.TryParse(ice.SessionId, out var iceSession):
                IceReceived?.Invoke(this, new IceNotification(iceSession, ice.Candidate, ice.SdpMid, ice.SdpMLineIndex));
                break;

            case IceServersMessage iceServers when SessionId.TryParse(iceServers.SessionId, out var iceServersSession):
                if (_pendingIce.TryGetValue(iceServersSession, out var pendingIce))
                {
                    pendingIce.TrySetResult(new IceConfiguration
                    {
                        Servers = iceServers.Servers.Select(server => new IceServerDefinition
                        {
                            Urls = server.Urls,
                            Username = server.Username,
                            Credential = server.Credential,
                            CredentialExpiresAt = server.ExpiresAt,
                        }).ToArray(),
                        TransportPolicy = iceServers.RelayOnly
                            ? IceTransportPolicy.RelayOnly
                            : IceTransportPolicy.All,
                        RelayServerId = iceServers.RelayServerId,
                        RelayRegion = iceServers.RelayRegion,
                    });
                }
                break;

            case SessionResumeTokenMessage token when SessionId.TryParse(token.SessionId, out var tokenSession):
                _resumeTokens[tokenSession] = new CachedResumeToken(token.ResumeToken, token.ExpiresAt);
                break;

            case SessionResumedMessage resumed when SessionId.TryParse(resumed.SessionId, out var resumedSession):
                if (_pendingResumes.TryGetValue(resumedSession, out var pendingResume))
                {
                    pendingResume.TrySetResult(new SessionResumeResult(
                        resumedSession,
                        resumed.Resumed,
                        resumed.Reason,
                        resumed.ResumeExpiresAt));
                }
                else if (resumed.Resumed && string.Equals(resumed.Reason, "peer_resumed", StringComparison.Ordinal))
                {
                    PeerResumed?.Invoke(this, new SessionPeerResumedNotification(resumedSession));
                }
                break;

            case SessionQualityMessage quality when SessionId.TryParse(quality.SessionId, out var qualitySession):
                QualityReceived?.Invoke(this, new SessionQualityNotification(
                    qualitySession,
                    quality.CaptureFps,
                    quality.EncodeFps,
                    quality.PacketLossPercent,
                    quality.JitterMs,
                    quality.AvailableOutgoingBitrateKbps,
                    quality.FramesDropped,
                    quality.SourceWidth,
                    quality.SourceHeight,
                    quality.RequestedWidth,
                    quality.RequestedHeight,
                    quality.EncodedWidth,
                    quality.EncodedHeight,
                    quality.EncoderName,
                    quality.EncoderHardwareAccelerated,
                    Enum.TryParse<ConnectionHealth>(quality.ConnectionHealth, out var health)
                        ? health
                        : ConnectionHealth.Unknown,
                    Enum.TryParse<QualityProfile>(quality.ActiveQualityProfile, out var profile)
                        ? profile
                        : null,
                    quality.AdaptiveQualityLevel,
                    quality.QualityChangeReason,
                    quality.TargetFps,
                    quality.TargetBitrateKbps,
                    quality.EncoderQueueDepth,
                    quality.DecodeFps,
                    quality.RenderFps,
                    quality.DecodeToRenderLatencyP95Ms,
                    quality.CaptureToPresentLatencyP95Ms,
                    quality.FrameAgeClockUncertaintyMs,
                    quality.InputToInjectionLatencyP95Ms,
                    quality.InputClockUncertaintyMs));
                break;

            case SessionEndMessage end when SessionId.TryParse(end.SessionId, out var endSession):
                _resumeTokens.TryRemove(endSession, out _);
                SessionEnded?.Invoke(this, new SessionEndedNotification(
                    endSession,
                    Enum.TryParse<SessionEndReason>(end.Reason, out var reason) ? reason : SessionEndReason.Unknown));
                break;

            case SessionDisplaysMessage displays when SessionId.TryParse(displays.SessionId, out var displaysSession):
                RemoteDisplaysReceived?.Invoke(this, new RemoteDisplaysNotification(
                    displaysSession,
                    displays.Displays
                        .Select(d => new CaptureTargetInfo(CaptureTargetKind.Display, d.Id, d.Name, d.Width, d.Height))
                        .ToArray(),
                    displays.ActiveDisplayId));
                break;

            case SelectDisplayMessage select when SessionId.TryParse(select.SessionId, out var selectSession):
                DisplaySelectionRequested?.Invoke(this, new SelectDisplayNotification(selectSession, select.DisplayId));
                break;

            case ErrorMessage error1:
                _log.LogWarning("Signaling error {Code}: {Message}", error1.Code, error1.Message);
                var exception = error1.Code is SignalingErrorCodes.InvalidProof
                    or SignalingErrorCodes.InvalidAttestation
                    ? new SignalingAuthenticationException($"{error1.Code}: {error1.Message}")
                    : error1.Code == SignalingErrorCodes.UnsupportedVersion
                        ? new SignalingProtocolException($"{error1.Code}: {error1.Message}")
                    : new PeerOnQException($"{error1.Code}: {error1.Message}");
                if (error1.Code == SignalingErrorCodes.UnsupportedVersion)
                    State = SignalingConnectionState.Faulted;
                FailPending(exception);
                if (ShouldSurfaceSignalingError(error1.Code))
                {
                    ErrorReceived?.Invoke(this, new SignalingErrorNotification(
                        error1.Code,
                        error1.Message,
                        error1.InReplyTo,
                        error1.CapabilitySide,
                        error1.RequiredCapabilities));
                }
                if (error1.Code == SignalingErrorCodes.TokenExpired)
                {
                    _ = HandleDropAsync();
                }
                break;

            case PresenceResultMessage presence:
                if (_pendingPresence.TryGetValue(presence.InReplyTo, out var pendingPresence))
                    pendingPresence.TrySetResult(presence);
                break;

            case PongMessage pong:
                UpdateServerClockOffset(pong.ServerTime);
                Volatile.Write(ref _lastHeartbeatResponseTimestamp, Stopwatch.GetTimestamp());
                break;

            default:
                _log.LogDebug("Ignoring unexpected signaling frame {Type}", message!.GetType().Name);
                break;
        }
    }

    private void FailPending(Exception ex)
    {
        _pendingChallenge?.TrySetException(ex);
        _pendingRegistration?.TrySetException(ex);
        foreach (var pending in _pendingIce.Values) pending.TrySetException(ex);
        foreach (var pending in _pendingResumes.Values) pending.TrySetException(ex);
        foreach (var pending in _pendingPresence.Values) pending.TrySetException(ex);
        foreach (var pending in _pendingUnattendedChallenges.Values) pending.TrySetException(ex);
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var refreshLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? registrationRefresh = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var heartbeatDelay = Task.Delay(options.HeartbeatInterval, cancellationToken);
                if (registrationRefresh is not null
                    && await Task.WhenAny(heartbeatDelay, registrationRefresh) == registrationRefresh)
                {
                    // Observe rejection immediately while retaining the normal heartbeat cadence.
                    await registrationRefresh;
                    registrationRefresh = null;
                }
                await heartbeatDelay;

                var lastResponse = Volatile.Read(ref _lastHeartbeatResponseTimestamp);
                if (ShouldFailHeartbeat(
                        lastResponse,
                        Stopwatch.GetTimestamp(),
                        options.HeartbeatTimeout,
                        IsFileRelayReceiveInProgress,
                        Volatile.Read(ref _lastFileRelayReceiveCompletionTimestamp)))
                {
                    throw new TimeoutException(
                        $"The signaling server did not answer a heartbeat within {options.HeartbeatTimeout.TotalSeconds:0}s.");
                }

                if (registrationRefresh is null && ShouldRefreshRegistration(
                        Volatile.Read(ref _registrationRefreshAtUtcTicks),
                        GetServerUtcNow()))
                {
                    // Cloud attestation may be slow. Keep proving socket liveness while the
                    // existing registration is still valid; only one refresh may run at a time.
                    registrationRefresh = RefreshRegistrationAsync(socket, refreshLifetime.Token);
                }

                using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendTimeout.CancelAfter(options.HeartbeatTimeout);
                await SendAsync(new PingMessage(), sendTimeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) return;

            var timeout = new TimeoutException("The signaling heartbeat send timed out.");
            _log.LogWarning(timeout, "Heartbeat stopped");
            FailPending(timeout);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Heartbeat stopped");
            FailPending(ex);
        }
        finally
        {
            await refreshLifetime.CancelAsync();
            if (registrationRefresh is not null)
            {
                try
                {
                    await registrationRefresh;
                }
                catch (Exception)
                {
                    // The failure was already handled above, or this connection was cancelled.
                }
            }
        }

        if (ShouldScheduleReconnect(
                _disposed == 1,
                Volatile.Read(ref _terminallyReplaced) == 1,
                cancellationToken.IsCancellationRequested,
                ReferenceEquals(socket, _socket)))
        {
            // Queue reconnect after this loop returns so connection replacement never waits on
            // the heartbeat task that detected the dead socket.
            _ = Task.Run(HandleDropAsync, CancellationToken.None);
        }
    }

    internal static bool IsHeartbeatExpired(long lastResponse, long now, TimeSpan timeout) =>
        lastResponse > 0
        && now >= lastResponse
        && Stopwatch.GetElapsedTime(lastResponse, now) >= timeout;

    internal static bool ShouldFailHeartbeat(
        long lastResponse,
        long now,
        TimeSpan timeout,
        bool fileRelayReceiveInProgress,
        long lastFileRelayReceiveCompletion = 0) =>
        !fileRelayReceiveInProgress
        && !IsHeartbeatGraceActive(lastFileRelayReceiveCompletion, now, timeout)
        && IsHeartbeatExpired(lastResponse, now, timeout);

    private static bool IsHeartbeatGraceActive(long started, long now, TimeSpan timeout) =>
        started > 0
        && now >= started
        && Stopwatch.GetElapsedTime(started, now) < timeout;

    private bool IsFileRelayReceiveInProgress =>
        Volatile.Read(ref _fileRelayReceivesInProgress) > 0;

    internal static DateTimeOffset CalculateRegistrationRefreshAt(
        DateTimeOffset registeredAt,
        DateTimeOffset expiresAt,
        TimeSpan heartbeatInterval,
        TimeSpan handshakeTimeout)
    {
        if (expiresAt <= registeredAt) return registeredAt;

        var lifetimeTicks = expiresAt.UtcTicks - registeredAt.UtcTicks;
        var doubledHeartbeatTicks = Math.Min(
            heartbeatInterval.Ticks,
            TimeSpan.MaxValue.Ticks / 2) * 2;
        var preferredLeadTicks = Math.Max(doubledHeartbeatTicks, handshakeTimeout.Ticks);
        var leadTicks = Math.Min(preferredLeadTicks, Math.Max(1, lifetimeTicks / 2));
        return new DateTimeOffset(expiresAt.UtcTicks - leadTicks, TimeSpan.Zero);
    }

    internal static bool ShouldRefreshRegistration(long refreshAtUtcTicks, DateTimeOffset serverNow) =>
        refreshAtUtcTicks > 0 && serverNow.UtcTicks >= refreshAtUtcTicks;

    internal static bool ShouldSurfaceSignalingError(string code) =>
        !string.Equals(code, SignalingErrorCodes.TokenExpired, StringComparison.Ordinal);

    private async Task RefreshRegistrationAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(socket, _socket) || State != SignalingConnectionState.Registered) return;

        var identity = _identity ?? throw new InvalidOperationException("ConnectAsync was not called.");
        var capabilities = _capabilities
                           ?? throw new InvalidOperationException("ConnectAsync did not validate client capabilities.");
        var remainingLifetime = (TokenExpiresAt ?? GetServerUtcNow()) - GetServerUtcNow();
        if (remainingLifetime <= TimeSpan.Zero)
            throw new TimeoutException("The signaling registration expired before refresh completed.");

        using var refreshDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshDeadline.CancelAfter(remainingLifetime);
        RegisteredMessage registered;
        try
        {
            registered = await RegisterCurrentConnectionAsync(identity, capabilities, refreshDeadline.Token);
            refreshDeadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && refreshDeadline.IsCancellationRequested)
        {
            throw new TimeoutException("The signaling registration expired before refresh completed.");
        }
        if (!ReferenceEquals(socket, _socket)) return;

        ApplyRegistration(registered, capabilities);
        _log.LogInformation(
            "Refreshed signaling registration for {Device} without replacing the active connection",
            identity.ToLogString());
    }

    internal static bool ShouldReconnectAfterServerClose(
        WebSocketCloseStatus? closeStatus,
        string? closeReason) =>
        closeStatus != WebSocketCloseStatus.NormalClosure
        || !string.Equals(closeReason, "replaced_by_new_connection", StringComparison.Ordinal);

    internal static bool ShouldScheduleReconnect(
        bool disposed,
        bool terminallyReplaced,
        bool cancellationRequested,
        bool ownsCurrentSocket) =>
        !disposed && !terminallyReplaced && !cancellationRequested && ownsCurrentSocket;

    internal static bool ShouldReconnectForNetworkChange(
        SignalingConnectionState state,
        bool networkAvailable,
        bool addressChanged) =>
        state == SignalingConnectionState.Registered
        || (networkAvailable && state == SignalingConnectionState.Faulted)
        || (addressChanged && state == SignalingConnectionState.Faulted);

    private void StartNetworkMonitoring()
    {
        if (Interlocked.Exchange(ref _networkMonitoringStarted, 1) != 0) return;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        if (!ShouldReconnectForNetworkChange(State, args.IsAvailable, addressChanged: false)) return;
        QueueNetworkReconnect(args.IsAvailable ? "network_available" : "network_unavailable");
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args)
    {
        if (!ShouldReconnectForNetworkChange(
                State,
                NetworkInterface.GetIsNetworkAvailable(),
                addressChanged: true)) return;
        QueueNetworkReconnect("network_address_changed");
    }

    private void QueueNetworkReconnect(string reason)
    {
        if (_disposed != 0 || Volatile.Read(ref _terminallyReplaced) != 0 || _identity is null) return;
        _log.LogInformation("Network change detected ({Reason}); refreshing signaling", reason);
        _ = Task.Run(HandleDropAsync, CancellationToken.None);
    }

    private void UpdateServerClockOffset(DateTimeOffset serverTime)
    {
        var offsetTicks = serverTime.UtcTicks - _time.GetUtcNow().UtcTicks;
        Interlocked.Exchange(ref _serverClockOffsetTicks, offsetTicks);
    }

    private DateTimeOffset GetServerUtcNow()
    {
        var correctedTicks = _time.GetUtcNow().UtcTicks
                             + Volatile.Read(ref _serverClockOffsetTicks);
        correctedTicks = Math.Clamp(
            correctedTicks,
            DateTimeOffset.MinValue.UtcTicks,
            DateTimeOffset.MaxValue.UtcTicks);
        return new DateTimeOffset(correctedTicks, TimeSpan.Zero);
    }

    private async Task HandleDropAsync()
    {
        if (_disposed == 1 || Volatile.Read(ref _terminallyReplaced) == 1) return;

        // Only one reconnect may be in flight; otherwise every dropped loop starts its own.
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1) return;

        try
        {
            await ReconnectAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    private async Task ReconnectAsync()
    {
        if (Volatile.Read(ref _terminallyReplaced) == 1)
        {
            State = SignalingConnectionState.Disconnected;
            return;
        }

        State = SignalingConnectionState.Reconnecting;

        if (!options.AutoReconnect)
        {
            State = SignalingConnectionState.Disconnected;
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;

        for (var attempt = 1;
             attempt <= options.MaxReconnectAttempts
             && _disposed == 0
             && Volatile.Read(ref _terminallyReplaced) == 0;
             attempt++)
        {
            var exponential = options.ReconnectDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
            var capped = Math.Min(exponential, options.MaxReconnectDelay.TotalMilliseconds);
            var jitter = capped * Math.Clamp(options.ReconnectJitterFraction, 0, 1);
            var delayMs = Math.Max(0, capped + ((Random.Shared.NextDouble() * 2 - 1) * jitter));
            var delay = TimeSpan.FromMilliseconds(delayMs);

            if (DateTimeOffset.UtcNow - startedAt + delay > options.MaxReconnectWindow)
            {
                break;
            }

            _log.LogInformation("Signaling reconnect attempt {Attempt} in {Delay}s", attempt, delay.TotalSeconds);
            await Task.Delay(delay);
            if (_disposed != 0 || Volatile.Read(ref _terminallyReplaced) != 0)
            {
                State = SignalingConnectionState.Disconnected;
                return;
            }

            try
            {
                await OpenAndRegisterAsync(CancellationToken.None);
                return;
            }
            catch (SignalingAuthenticationException ex)
            {
                _log.LogError(ex, "Signaling identity no longer matches the server pin; reconnect stopped");
                State = SignalingConnectionState.Faulted;
                return;
            }
            catch (SignalingProtocolException ex)
            {
                _log.LogError(ex, "Signaling protocol is incompatible; reconnect stopped");
                State = SignalingConnectionState.Faulted;
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Reconnect attempt {Attempt} failed", attempt);
            }
        }

        State = Volatile.Read(ref _terminallyReplaced) == 1
            ? SignalingConnectionState.Disconnected
            : SignalingConnectionState.Faulted;
    }

    internal static string ToWire(SessionMode mode) => mode switch
    {
        SessionMode.ViewOnly => "view-only",
        SessionMode.FullControl => "full-control",
        SessionMode.FileTransferOnly => "file-transfer-only",
        SessionMode.Custom => "custom",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static SessionMode FromWireMode(string mode) => mode switch
    {
        "view-only" => SessionMode.ViewOnly,
        "full-control" => SessionMode.FullControl,
        "file-transfer-only" => SessionMode.FileTransferOnly,
        "custom" => SessionMode.Custom,
        _ => throw new SignalingProtocolException($"Unsupported session mode '{mode}'."),
    };

    internal static string ToWire(SessionAccessKind kind) => kind switch
    {
        SessionAccessKind.Attended => "attended",
        SessionAccessKind.Unattended => "unattended",
        SessionAccessKind.SupportInvitation => "support-invitation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static SessionAccessKind FromWireAccessKind(string value) => value switch
    {
        "attended" => SessionAccessKind.Attended,
        "unattended" => SessionAccessKind.Unattended,
        "support-invitation" => SessionAccessKind.SupportInvitation,
        _ => throw new SignalingProtocolException($"Unsupported access kind '{value}'."),
    };

    internal static string ToWire(PermissionDecision decision) => decision switch
    {
        PermissionDecision.Accept => "accept",
        PermissionDecision.Decline => "decline",
        PermissionDecision.Block => "block",
        PermissionDecision.Timeout => "timeout",
        _ => throw new ArgumentOutOfRangeException(nameof(decision)),
    };

    internal static PermissionDecision FromWireDecision(string decision) => decision switch
    {
        "accept" => PermissionDecision.Accept,
        "decline" => PermissionDecision.Decline,
        "block" => PermissionDecision.Block,
        "timeout" => PermissionDecision.Timeout,
        _ => PermissionDecision.Decline,
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        if (Interlocked.Exchange(ref _networkMonitoringStarted, 0) != 0)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        }

        try
        {
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Ignoring error while disconnecting");
        }

        _lifetime?.Cancel();

        foreach (var loop in new[] { _receiveLoop, _heartbeatLoop })
        {
            if (loop is null) continue;
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Loop is wedged on a dead socket; the socket dispose below unblocks it.
            }
        }

        _socket?.Dispose();
        _lifetime?.Dispose();
        _sendGate.Dispose();
    }

    private sealed record CachedResumeToken(string Token, DateTimeOffset ExpiresAt);
}
