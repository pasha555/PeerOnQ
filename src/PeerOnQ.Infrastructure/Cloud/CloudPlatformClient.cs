using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Infrastructure.Configuration;
using PeerOnQ.Infrastructure.Diagnostics;
using PeerOnQ.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Infrastructure.Cloud;

public enum CloudPlatformState
{
    Disabled = 0,
    Connecting = 1,
    Authenticated = 2,
    Online = 3,
    Reconnecting = 4,
    Blocked = 5,
    UnsupportedVersion = 6,
    AuthenticationFailed = 7,
    Stopped = 8,
}

public sealed record CloudPlatformClientOptions
{
    public required CloudServiceEndpoints Endpoints { get; init; }
    public required Guid InstallationId { get; init; }
    public required string AppVersion { get; init; }
    public required string OsVersion { get; init; }
    public required ArchitectureKindV1 Architecture { get; init; }
    public required InstallChannelV1 InstallChannel { get; init; }
    public PlatformKindV1 Platform { get; init; } = PlatformKindV1.Windows;
    public string ProtocolVersion { get; init; } = "1";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaximumReconnectWindow { get; init; } = TimeSpan.FromMinutes(2);
    public int MaximumReconnectAttempts { get; init; } = 6;
    public TimeSpan ReconnectCooldown { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class CloudPlatformException(
    string code,
    string message,
    bool permanent,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Permanent { get; } = permanent;
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// Opt-in composition for official cloud-enabled releases. Tokens stay in memory, device proof
/// uses the existing OS-protected ECDSA key, each retry burst/backoff is bounded, and cloud failure
/// never changes local permission, media, input, file-transfer, or update-security decisions.
/// </summary>
public sealed class CloudPlatformClient : IAsyncDisposable, ISignalingAttestationProvider
{
    private static readonly TimeSpan MaximumRegistrationChallengeLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(25);
    // Register already refreshes the lease, and 0.9.81 presence servers reject a heartbeat less
    // than 3 s after it; the first hub heartbeat on a connection waits for the next interval.
    private static readonly TimeSpan PresenceHeartbeatSpacing = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly CloudPlatformClientOptions _options;
    private DeviceIdentity _identity;
    private readonly IRegistrationProofProvider _proofProvider;
    private readonly Func<DeviceIdentity, string, CancellationToken, Task<DeviceIdentity>> _persistServerAlias;
    private readonly SqliteClientTelemetryOutbox _outbox;
    private readonly SqliteClientUpdateTelemetryOutbox? _updateOutbox;
    private readonly ILogger<CloudPlatformClient> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);
    private readonly TaskCompletionSource _firstAuthentication = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HubConnection? _presence;
    private TaskCompletionSource _presenceLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _presenceRegisteredAtUtc;
    private Task? _runTask;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAtUtc;
    private string? _signalingAttestation;
    private DateTimeOffset _signalingAttestationExpiresAtUtc;
    private Guid _cloudDeviceId;
    private int _desiredPresenceState = (int)PresenceStateV1.Online;
    private int _started;
    private readonly ConcurrentDictionary<Guid, byte> _droppedUnknownSessions = new();
    private readonly ConcurrentDictionary<Guid, byte> _replayedLiveSessions = new();

    public CloudPlatformClient(
        HttpClient http,
        CloudPlatformClientOptions options,
        DeviceIdentity identity,
        IRegistrationProofProvider proofProvider,
        Func<DeviceIdentity, string, CancellationToken, Task<DeviceIdentity>> persistServerAlias,
        SqliteClientTelemetryOutbox outbox,
        ILogger<CloudPlatformClient> logger,
        SqliteClientUpdateTelemetryOutbox? updateOutbox = null,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _http.BaseAddress = options.Endpoints.ApiBaseUri;
        _http.Timeout = options.RequestTimeout;
        _options = options;
        _identity = identity;
        _proofProvider = proofProvider;
        _persistServerAlias = persistServerAlias;
        _outbox = outbox;
        _updateOutbox = updateOutbox;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CloudPlatformState State { get; private set; } = CloudPlatformState.Disabled;
    public string? LastErrorCode { get; private set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; private set; }
    public DeviceIdentity Identity => _identity;
    public bool RoutingIdentityReady => _identity.PublicIdServerAssigned;
    public event EventHandler<CloudPlatformState>? StateChanged;
    public event EventHandler<DeviceIdentity>? EnrollmentCompleted;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        SetState(CloudPlatformState.Connecting);
        _runTask = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    }

    public Task WaitForFirstAuthenticationAsync(CancellationToken cancellationToken = default) =>
        _firstAuthentication.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Performs one challenge/authentication/installation registration transaction. The normal
    /// app calls <see cref="Start"/>; this explicit operation is useful for readiness checks and
    /// deterministic integration tests without starting the presence loop.
    /// </summary>
    public Task RegisterInstallationAsync(CancellationToken cancellationToken = default) =>
        EnsureAuthenticatedAsync(cancellationToken);

    public async Task<CreateDeviceOwnershipLinkResponse> CreateDeviceOwnershipLinkAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        using var request = CreateRequest(HttpMethod.Post, "v1/devices/ownership-links", authenticated: true);
        var link = await SendAsync<CreateDeviceOwnershipLinkResponse>(request, cancellationToken);
        if (link.Code is not { Length: 43 }
            || link.Code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            || link.ExpiresAtUtc == default)
            throw new CloudPlatformException("invalid_ownership_link", "The server returned an invalid device linking code.", permanent: false);
        return link;
    }

    public async Task<DeviceOwnershipLinkStatusResponse> GetDeviceOwnershipLinkStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        using var request = CreateRequest(HttpMethod.Get, "v1/devices/ownership-links/status", authenticated: true);
        return await SendAsync<DeviceOwnershipLinkStatusResponse>(request, cancellationToken);
    }

    public async Task RefreshOwnershipPolicyAsync(CancellationToken cancellationToken = default)
    {
        await _authenticationGate.WaitAsync(cancellationToken);
        try { await AuthenticateAndRegisterAsync(cancellationToken); }
        finally { _authenticationGate.Release(); }
    }

    public async Task<string?> GetSignalingAttestationAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(_signalingAttestation)
            || _signalingAttestationExpiresAtUtc <= _timeProvider.GetUtcNow().AddSeconds(30))
        {
            throw new CloudPlatformException(
                "signaling_attestation_unavailable",
                "A current signaling attestation is unavailable.",
                permanent: false);
        }

        return _signalingAttestation;
    }

    public async Task SetPresenceStateAsync(
        PresenceStateV1 state,
        CancellationToken cancellationToken = default)
    {
        if (state is PresenceStateV1.Connecting
            or PresenceStateV1.Reconnecting
            or PresenceStateV1.Blocked
            or PresenceStateV1.UnsupportedVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "This presence state is controlled by the cloud client.");
        }

        Volatile.Write(ref _desiredPresenceState, (int)state);
        if (_presence is not { State: HubConnectionState.Connected }) return;

        try
        {
            await _presence.InvokeAsync(
                "SetSessionState",
                new PresenceSessionStateRequestV1(state),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Presence state update was deferred until the next heartbeat");
        }
    }

    public async Task<DiagnosticStatusResponseV1> UploadDiagnosticsAsync(
        DiagnosticBundleResult bundle,
        DiagnosticCreateRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        if (!request.ConsentGranted)
            throw new InvalidOperationException("Diagnostics upload requires explicit consent for this bundle.");
        if (!File.Exists(bundle.FilePath))
            throw new FileNotFoundException("The sanitized diagnostic bundle no longer exists.", bundle.FilePath);
        if (bundle.SizeBytes is <= 0 or > DiagnosticBundleService.MaximumBundleBytes)
            throw new InvalidOperationException("The diagnostic bundle size is outside the accepted limit.");

        await EnsureAuthenticatedAsync(cancellationToken);
        var created = await SendJsonAsync<DiagnosticCreateRequestV1, DiagnosticCreateResultV1>(
            HttpMethod.Post,
            "v1/diagnostics/requests",
            request,
            authenticated: true,
            cancellationToken);

        var archiveBytes = await File.ReadAllBytesAsync(bundle.FilePath, cancellationToken);
        var digest = Convert.ToBase64String(SHA256.HashData(archiveBytes));
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(created.DiagnosticId.ToString("D")), "diagnosticId");
        content.Add(new StringContent(created.UploadToken), "uploadToken");
        content.Add(new StringContent(digest), "sha256Base64");
        var archive = new ByteArrayContent(archiveBytes);
        archive.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(archive, "archive", "peeronq-diagnostics.zip");

        using var uploadRequest = CreateRequest(HttpMethod.Post, "v1/diagnostics/uploads", authenticated: true);
        uploadRequest.Content = content;
        using var response = await _http.SendAsync(uploadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await SendAsync<DiagnosticStatusResponseV1>(
            CreateRequest(HttpMethod.Get, $"v1/diagnostics/{created.DiagnosticId:D}/status", authenticated: true),
            cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var reconnectWindowStartedAt = _timeProvider.GetUtcNow();
        var failureCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureAuthenticatedAsync(cancellationToken);
                await EnsurePresenceConnectedAsync(cancellationToken);
                SetState(CloudPlatformState.Online);
                _firstAuthentication.TrySetResult();

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (AuthenticationNeedsRefresh())
                    {
                        await AuthenticateAndRegisterAsync(cancellationToken);
                    }

                    await SendHeartbeatAsync(cancellationToken);
                    SetState(CloudPlatformState.Online);
                    await SendBestEffortTelemetryAsync("session", FlushTelemetryAsync, cancellationToken);
                    await SendBestEffortTelemetryAsync("session heartbeat", SendSessionHeartbeatsAsync, cancellationToken);
                    await SendBestEffortTelemetryAsync("update", FlushUpdateTelemetryAsync, cancellationToken);
                    await WaitForNextHeartbeatAsync(cancellationToken);
                    // Only a connection that stayed up for a whole interval ends the retry burst;
                    // one that is accepted and then dropped again keeps backing off.
                    failureCount = 0;
                    reconnectWindowStartedAt = _timeProvider.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (CloudPlatformException ex) when (ex.Permanent)
            {
                LastErrorCode = ex.Code;
                SetState(ex.Code is "installation_blocked" or "device_revoked"
                    ? CloudPlatformState.Blocked
                    : ex.Code == "unsupported_version"
                        ? CloudPlatformState.UnsupportedVersion
                        : CloudPlatformState.AuthenticationFailed);
                _firstAuthentication.TrySetException(ex);
                _logger.LogError("Cloud authentication stopped with permanent error {ErrorCode}", ex.Code);
                return;
            }
            catch (Exception ex)
            {
                failureCount++;
                LastErrorCode = ErrorCodeOf(ex);
                SetState(CloudPlatformState.Reconnecting);
                if (_presence is not null)
                {
                    try { await _presence.StopAsync(cancellationToken); }
                    catch (Exception stopError) { _logger.LogDebug(stopError, "Presence stop after failure was incomplete"); }
                }

                var elapsed = _timeProvider.GetUtcNow() - reconnectWindowStartedAt;
                if (failureCount > _options.MaximumReconnectAttempts
                    || elapsed >= _options.MaximumReconnectWindow)
                {
                    _logger.LogWarning(
                        "Cloud reconnect window ended after {Attempts} attempts; retrying after {DelayMs} ms",
                        failureCount,
                        _options.ReconnectCooldown.TotalMilliseconds);
                    await Task.Delay(_options.ReconnectCooldown, cancellationToken);
                    failureCount = 0;
                    reconnectWindowStartedAt = _timeProvider.GetUtcNow();
                    continue;
                }

                var delay = RetryDelay(failureCount, LastErrorCode, ex, _options.ReconnectCooldown);
                _logger.LogWarning("Cloud connection attempt {Attempt} failed with {ErrorCode}; retrying in {DelayMs} ms",
                    failureCount,
                    LastErrorCode,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        SetState(CloudPlatformState.Stopped);
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!AuthenticationNeedsRefresh()) return;

        await _authenticationGate.WaitAsync(cancellationToken);
        try
        {
            if (!AuthenticationNeedsRefresh()) return;
            await AuthenticateAndRegisterAsync(cancellationToken);
        }
        finally
        {
            _authenticationGate.Release();
        }
    }

    private async Task AuthenticateAndRegisterAsync(CancellationToken cancellationToken)
    {
        // Renewing the access token of an online client is not a reconnect: the public state
        // blipped Connecting -> Online for ~0.3 s on every refresh (0.9.86), with no presence change.
        var refreshingOnline = State == CloudPlatformState.Online;
        if (!refreshingOnline) SetState(CloudPlatformState.Connecting);
        var publicKey = _identity.PublicKey ?? throw new CloudPlatformException(
            "identity_key_missing", "The local device signing key is unavailable.", permanent: true);
        var challenge = await SendJsonAsync<DeviceRegistrationRequestV1, DeviceRegistrationChallengeV1>(
            HttpMethod.Post,
            "v1/devices/register",
            new DeviceRegistrationRequestV1(
                _options.InstallationId,
                _identity.DisplayName,
                publicKey,
                _options.Platform,
                _options.Architecture,
                _options.AppVersion,
                _options.OsVersion,
                _options.InstallChannel,
                _options.Endpoints.Region,
                _options.ProtocolVersion),
            authenticated: false,
            cancellationToken);
        if (!TryValidateRegistrationChallenge(challenge, out var challengeRejectionReason))
        {
            _logger.LogWarning(
                "Cloud registration challenge was rejected by local validation ({Reason})",
                challengeRejectionReason);
            throw new CloudPlatformException("invalid_challenge", "The cloud authentication challenge is invalid.", permanent: true);
        }

        var proof = await _proofProvider.ComputeRegistrationProofAsync(challenge.CanonicalPayload, cancellationToken);
        var authenticated = await SendJsonAsync<DeviceAuthenticationRequestV1, DeviceAuthenticationResultV1>(
            HttpMethod.Post,
            "v1/devices/authenticate",
            new DeviceAuthenticationRequestV1(
                challenge.ChallengeId,
                proof,
                publicKey,
                _options.InstallationId),
            authenticated: false,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        if (authenticated.InstallationId != _options.InstallationId
            || authenticated.DeviceId == Guid.Empty
            || authenticated.ExpiresAtUtc <= now.AddSeconds(30)
            || string.IsNullOrWhiteSpace(authenticated.AccessToken)
            || string.IsNullOrWhiteSpace(authenticated.SignalingAttestation)
            || authenticated.SignalingAttestation.Length > PeerOnQ.Shared.Contracts.Security.SignalingAttestationTokenV1.MaximumTokenCharacters
            || authenticated.SignalingAttestationExpiresAtUtc <= now.AddSeconds(30)
            || authenticated.SignalingAttestationExpiresAtUtc > now.AddMinutes(15)
            || !PeerOnQId.TryParse(authenticated.PublicDeviceId, out _))
        {
            throw new CloudPlatformException("invalid_authentication_result", "The cloud authentication response is invalid.", permanent: true);
        }

        _identity = await _persistServerAlias(
            _identity, authenticated.PublicDeviceId, cancellationToken);
        EnrollmentCompleted?.Invoke(this, _identity);

        _accessToken = authenticated.AccessToken;
        _tokenExpiresAtUtc = authenticated.ExpiresAtUtc;
        _signalingAttestation = authenticated.SignalingAttestation;
        _signalingAttestationExpiresAtUtc = authenticated.SignalingAttestationExpiresAtUtc;
        _cloudDeviceId = authenticated.DeviceId;
        try
        {
            var registration = await SendJsonAsync<InstallationRegistrationRequestV1, InstallationRegistrationResultV1>(
                HttpMethod.Post,
                "v1/installations/register",
                new InstallationRegistrationRequestV1(
                    _options.InstallationId,
                    _options.Platform,
                    _options.Architecture,
                    _options.AppVersion,
                    _options.OsVersion,
                    _options.InstallChannel,
                    _options.ProtocolVersion,
                    _options.Endpoints.Region),
                authenticated: true,
                cancellationToken);
            if (registration.InstallationId != _options.InstallationId)
                throw new CloudPlatformException("installation_mismatch",
                    "The cloud returned another installation identity.", permanent: true);
            if (registration.IsBlocked)
                throw new CloudPlatformException("installation_blocked",
                    "This installation is blocked.", permanent: true);
        }
        catch
        {
            _accessToken = null;
            _tokenExpiresAtUtc = default;
            _signalingAttestation = null;
            _signalingAttestationExpiresAtUtc = default;
            _cloudDeviceId = Guid.Empty;
            throw;
        }

        if (!refreshingOnline) SetState(CloudPlatformState.Authenticated);
    }

    private static bool TryValidateRegistrationChallenge(
        DeviceRegistrationChallengeV1 challenge,
        out string rejectionReason)
    {
        if (challenge.CanonicalPayload is null
            || challenge.CanonicalPayload.Length is < 32 or > 4096)
        {
            rejectionReason = "payload_size";
            return false;
        }

        if (!TryReadCanonicalUnixTime(challenge.CanonicalPayload, "issued_at", out var issuedAtUnix)
            || !TryReadCanonicalUnixTime(challenge.CanonicalPayload, "expires_at", out var expiresAtUnix))
        {
            rejectionReason = "canonical_timestamps";
            return false;
        }

        if (challenge.ExpiresAtUtc.ToUnixTimeSeconds() != expiresAtUnix)
        {
            rejectionReason = "expiry_mismatch";
            return false;
        }

        var lifetimeSeconds = (decimal)expiresAtUnix - issuedAtUnix;
        if (lifetimeSeconds <= 0
            || lifetimeSeconds > (decimal)MaximumRegistrationChallengeLifetime.TotalSeconds)
        {
            rejectionReason = "lifetime";
            return false;
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static bool TryReadCanonicalUnixTime(
        string canonicalPayload,
        string fieldName,
        out long value)
    {
        value = default;
        var prefix = $"{fieldName}=";
        var found = false;
        foreach (var rawLine in canonicalPayload.Split('\n'))
        {
            var line = rawLine.AsSpan().TrimEnd('\r');
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (found
                || !long.TryParse(
                    line[prefix.Length..],
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return false;
            }

            found = true;
        }

        return found;
    }

    private bool AuthenticationNeedsRefresh()
    {
        var refreshAfter = _timeProvider.GetUtcNow().AddMinutes(2);
        return string.IsNullOrEmpty(_accessToken)
               || _tokenExpiresAtUtc <= refreshAfter
               || string.IsNullOrEmpty(_signalingAttestation)
               || _signalingAttestationExpiresAtUtc <= refreshAfter;
    }

    private async Task EnsurePresenceConnectedAsync(CancellationToken cancellationToken)
    {
        if (_presence is { State: HubConnectionState.Connected }) return;
        if (_presence is not null) await _presence.DisposeAsync();

        var hubUri = new Uri(_options.Endpoints.PresenceUri, "presence/v1/hub");
        // No SignalR automatic reconnect: a reconnected connection has a new id the hub never saw
        // register, so heartbeats failed with presence_not_registered. RunAsync owns reconnection
        // and every connection registers exactly once.
        var presence = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(_accessToken);
                options.Headers["X-PeerOnQ-Installation"] = _options.InstallationId.ToString("D");
            })
            .Build();
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presence.Closed += _ =>
        {
            lost.TrySetResult();
            return Task.CompletedTask;
        };
        _presence = presence;
        _presenceLost = lost;
        try
        {
            await presence.StartAsync(cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new CloudPlatformException(
                "rate_limited", "Presence connection attempts are rate limited.", permanent: false, ex);
        }

        // Every presence server accepts Online on Register (0.9.81 rejects Busy/InSession there),
        // so a device that reconnects during a session restores its session state afterwards.
        await presence.InvokeAsync<PresenceConnectionAcceptedV1>(
            "Register",
            CreatePresencePayload(PresenceStateV1.Online),
            cancellationToken);
        _presenceRegisteredAtUtc = _timeProvider.GetUtcNow();
        var restored = PresenceStateV1.Online;
        while (true)
        {
            var desired = (PresenceStateV1)Volatile.Read(ref _desiredPresenceState);
            if (desired == restored
                || desired is not (PresenceStateV1.Online or PresenceStateV1.Busy or PresenceStateV1.InSession))
                break;
            await presence.InvokeAsync<PresenceHeartbeatResultV1>(
                "SetSessionState",
                new PresenceSessionStateRequestV1(desired),
                cancellationToken);
            restored = desired;
        }
    }

    private async Task WaitForNextHeartbeatAsync(CancellationToken cancellationToken)
    {
        var lost = _presenceLost.Task;
        using var interval = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(HeartbeatInterval, interval.Token);
        if (await Task.WhenAny(delay, lost) == lost)
        {
            await interval.CancelAsync();
            // Reconnect through the bounded retry path rather than waiting out the interval.
            throw new CloudPlatformException(
                "presence_disconnected", "The presence connection closed.", permanent: false);
        }

        await delay;
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var heartbeatPayload = CreatePresenceHeartbeat();
        var heartbeat = await SendJsonAsync<InstallationHeartbeatRequestV1, InstallationHeartbeatResultV1>(
            HttpMethod.Post,
            "v1/installations/heartbeat",
            new InstallationHeartbeatRequestV1(
                _options.InstallationId,
                heartbeatPayload.State,
                _options.AppVersion,
                _options.Endpoints.Region,
                heartbeatPayload.SentAtUtc),
            authenticated: true,
            cancellationToken);
        if (heartbeat.IsBlocked)
            throw new CloudPlatformException("installation_blocked", "This installation was blocked.", permanent: true);

        if (_presence is not { State: HubConnectionState.Connected })
            await EnsurePresenceConnectedAsync(cancellationToken);
        if (_timeProvider.GetUtcNow() - _presenceRegisteredAtUtc >= PresenceHeartbeatSpacing)
        {
            await _presence!.InvokeAsync<PresenceHeartbeatResultV1>(
                "Heartbeat",
                heartbeatPayload,
                cancellationToken);
        }
        LastHeartbeatAtUtc = heartbeatPayload.SentAtUtc;
    }

    private PresenceHeartbeatV1 CreatePresenceHeartbeat() =>
        CreatePresencePayload((PresenceStateV1)Volatile.Read(ref _desiredPresenceState));

    private PresenceHeartbeatV1 CreatePresencePayload(PresenceStateV1 state) => new(
        state,
        _options.AppVersion,
        _options.Endpoints.Region,
        _timeProvider.GetUtcNow());

    private async Task FlushTelemetryAsync(CancellationToken cancellationToken)
    {
        // Session event DTOs are mapped at the authenticated API boundary. Until queued items are
        // accepted, they remain local and bounded; deleting on a network error would corrupt counts.
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pending in await _outbox.ReadPendingAsync(cancellationToken: cancellationToken))
        {
            if (dropped.Contains(pending.SessionId)) continue;
            try
            {
                var local = DeserializeSessionEvent(pending.PayloadJson);
                if (!await TrySendSessionEventAsync(local, cancellationToken))
                {
                    // The cloud never recorded this session's start and nothing here can start it,
                    // so none of its queued events can ever be accepted. 0.9.85 retried them and
                    // logged "deferred after session_not_found" on every heartbeat.
                    dropped.Add(pending.SessionId);
                    await _outbox.MarkSessionTerminallyRejectedAsync(pending.SessionId, cancellationToken);
                    if (_droppedUnknownSessions.TryAdd(local.SessionId.Value, 0))
                        _logger.LogWarning(
                            "Cloud has no record of session {SessionId}; its queued telemetry was dropped at {Kind}",
                            local.SessionId,
                            local.Kind);
                    continue;
                }

                await _outbox.MarkDeliveredAsync(pending.EventId, cancellationToken);
                if (local.Kind == ClientSessionEventKind.Connected)
                    _outbox.MarkSessionConnectedAccepted(local.SessionId.Value);
                _logger.LogInformation("Cloud recorded session event {Kind} for {SessionId}", local.Kind, local.SessionId);
            }
            catch (CloudPlatformException ex) when (ex.Permanent)
            {
                // Validation/auth failures need operator attention; retrying them would flood the API.
                _logger.LogWarning("Client telemetry {EventId} was rejected with {ErrorCode}", pending.EventId, ex.Code);
                await _outbox.MarkTerminallyRejectedAsync(pending.EventId, cancellationToken);
            }
            catch
            {
                await _outbox.MarkFailedAsync(pending.EventId, cancellationToken);
                throw;
            }
        }
    }

    private static ClientSessionTelemetryEvent DeserializeSessionEvent(string payloadJson) =>
        JsonSerializer.Deserialize<ClientSessionTelemetryEvent>(payloadJson, JsonOptions)
        ?? throw new CloudPlatformException(
            "invalid_local_telemetry", "A queued telemetry event is malformed.", permanent: true);

    /// <summary>
    /// Sends one lifecycle event. A later event the cloud cannot place (404 session_not_found)
    /// first gets its session started again; false means the session can never be started.
    /// </summary>
    private async Task<bool> TrySendSessionEventAsync(
        ClientSessionTelemetryEvent local,
        CancellationToken cancellationToken)
    {
        try
        {
            await PostSessionEventAsync(local, cancellationToken);
            return true;
        }
        catch (CloudPlatformException ex) when (local.Kind != ClientSessionEventKind.Started
                                                && IsSessionNotFound(ex.Code))
        {
            // The start has to reach the cloud before any later lifecycle event.
        }

        if (!await TryStartSessionAgainAsync(local.SessionId, cancellationToken)) return false;
        try
        {
            await PostSessionEventAsync(local, cancellationToken);
            return true;
        }
        catch (CloudPlatformException ex) when (IsSessionNotFound(ex.Code))
        {
            return false;
        }
    }

    private async Task<bool> TryStartSessionAgainAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        // A start still queued (in backoff after an outage, so a later event overtook it) goes first.
        if (await _outbox.FindUndeliveredStartAsync(sessionId.ToString(), cancellationToken) is { } queued)
        {
            var start = DeserializeSessionEvent(queued.PayloadJson);
            await PostSessionEventAsync(start, cancellationToken);
            await _outbox.MarkDeliveredAsync(queued.EventId, cancellationToken);
            _logger.LogInformation(
                "Cloud recorded session event {Kind} for {SessionId} ahead of its later events",
                start.Kind,
                start.SessionId);
            return true;
        }

        return await TryReplayLiveSessionAsync(sessionId.Value, cancellationToken);
    }

    /// <summary>
    /// A session still running here that the cloud lost (a redeploy, a restore) is started again
    /// from its local record, once per session, under fresh event IDs.
    /// </summary>
    private async Task<bool> TryReplayLiveSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var replay = _outbox.GetLiveSessionReplay(sessionId);
        if (replay.Count == 0 || _replayedLiveSessions.ContainsKey(sessionId)) return false;
        foreach (var lifecycleEvent in replay)
        {
            await PostSessionEventAsync(lifecycleEvent with { EventId = Guid.NewGuid() }, cancellationToken);
            if (lifecycleEvent.Kind == ClientSessionEventKind.Connected) _outbox.MarkSessionConnectedAccepted(sessionId);
        }
        _replayedLiveSessions.TryAdd(sessionId, 0);
        _logger.LogInformation("Cloud had no record of live session {SessionId}; re-sent its start", sessionId);
        return true;
    }

    private async Task PostSessionEventAsync(ClientSessionTelemetryEvent local, CancellationToken cancellationToken)
    {
        var payload = MapSessionEvent(local);
        using var request = CreateRequest(HttpMethod.Post, $"v1/sessions/{RouteFor(local.Kind)}", authenticated: true);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", local.EventId.ToString("N"));
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static bool IsSessionNotFound(string code) =>
        string.Equals(code, "session_not_found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Session and update telemetry is best effort. The outboxes keep and retry what failed, and a
    /// telemetry failure never tears down presence (a 404 session heartbeat used to put the
    /// device offline into the reconnect cooldown).
    /// </summary>
    private async Task SendBestEffortTelemetryAsync(
        string telemetry,
        Func<CancellationToken, Task> send,
        CancellationToken cancellationToken)
    {
        try
        {
            await send(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Client {Telemetry} telemetry was deferred after {ErrorCode}", telemetry, ErrorCodeOf(ex));
        }
    }

    private async Task SendSessionHeartbeatsAsync(CancellationToken cancellationToken)
    {
        foreach (var sessionId in _outbox.GetActiveConnectedSessionIds())
        {
            var eventId = Guid.NewGuid();
            var payload = new ClientSessionHeartbeatV1(eventId, sessionId, _timeProvider.GetUtcNow());
            using var request = CreateRequest(HttpMethod.Post, "v1/sessions/heartbeat", authenticated: true);
            request.Headers.TryAddWithoutValidation("Idempotency-Key", eventId.ToString("N"));
            request.Content = JsonContent.Create(payload, options: JsonOptions);
            try
            {
                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                await EnsureSuccessAsync(response, cancellationToken);
            }
            catch (CloudPlatformException exception) when (IsRejectedSessionHeartbeat(exception.Code))
            {
                // A live session the cloud lost after a reconnect is started again; the next
                // heartbeat round tells whether that worked.
                if (IsSessionNotFound(exception.Code)
                    && await TryReplayLiveSessionAsync(sessionId, cancellationToken))
                    continue;

                // The API returns lowercase codes and session_not_found is a 404 (not "permanent"),
                // so the code alone decides: the cloud will not track this session.
                _outbox.StopSessionHeartbeat(sessionId);
                _logger.LogWarning(
                    "Session heartbeat for {SessionId} was rejected with {ErrorCode}",
                    sessionId,
                    exception.Code);
            }
        }
    }

    private static bool IsRejectedSessionHeartbeat(string code) =>
        IsSessionNotFound(code)
        || string.Equals(code, "invalid_request", StringComparison.OrdinalIgnoreCase);

    private static string RouteFor(ClientSessionEventKind kind) => kind switch
    {
        ClientSessionEventKind.Connected => "connected",
        ClientSessionEventKind.Ended or ClientSessionEventKind.Failed => "end",
        _ => "start",
    };

    private async Task FlushUpdateTelemetryAsync(CancellationToken cancellationToken)
    {
        if (_updateOutbox is null) return;
        foreach (var pending in await _updateOutbox.ReadPendingAsync(cancellationToken: cancellationToken))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<ClientUpdateEventV1>(pending.PayloadJson, JsonOptions)
                              ?? throw new CloudPlatformException(
                                  "invalid_local_update_telemetry",
                                  "A queued update telemetry event is malformed.",
                                  permanent: true);
                using var request = CreateRequest(HttpMethod.Post, "v1/updates/events", authenticated: true);
                request.Headers.TryAddWithoutValidation("Idempotency-Key", pending.EventId.ToString("N"));
                request.Content = JsonContent.Create(payload, options: JsonOptions);
                using var response = await _http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                await EnsureSuccessAsync(response, cancellationToken);
                await _updateOutbox.MarkDeliveredAsync(pending.EventId, cancellationToken);
            }
            catch (CloudPlatformException ex) when (ex.Permanent)
            {
                _logger.LogWarning(
                    "Client update telemetry {EventId} was rejected with {ErrorCode}",
                    pending.EventId,
                    ex.Code);
                await _updateOutbox.MarkTerminallyRejectedAsync(pending.EventId, cancellationToken);
            }
            catch
            {
                await _updateOutbox.MarkFailedAsync(pending.EventId, cancellationToken);
                throw;
            }
        }
    }

    private ClientSessionLifecycleEventV1 MapSessionEvent(ClientSessionTelemetryEvent value) => new(
        value.EventId,
        value.SessionId.Value,
        value.Kind switch
        {
            ClientSessionEventKind.Started => SessionEventKindV1.Started,
            ClientSessionEventKind.Connected => SessionEventKindV1.Connected,
            _ => SessionEventKindV1.Ended,
        },
        value.Role == PeerOnQ.Domain.Sessions.SessionRole.Viewer
            ? SessionParticipantRoleV1.Viewer
            : SessionParticipantRoleV1.Host,
        value.PeerPublicDeviceId,
        value.PermissionMode switch
        {
            PeerOnQ.Domain.Sessions.SessionMode.ViewOnly => PermissionModeV1.ViewOnly,
            PeerOnQ.Domain.Sessions.SessionMode.FullControl => PermissionModeV1.FullControl,
            PeerOnQ.Domain.Sessions.SessionMode.FileTransferOnly => PermissionModeV1.FileTransfer,
            _ => PermissionModeV1.Custom,
        },
        value.ConnectionPath switch
        {
            "DirectLan" => ConnectionPathV1.LanDirect,
            "DirectInternet" => ConnectionPathV1.InternetDirect,
            // The current media API confirms relay nomination but cannot safely distinguish
            // UDP, TCP, or TURN-over-TLS. Preserve that truth instead of inventing a transport.
            "Relayed" => ConnectionPathV1.Unknown,
            _ => ConnectionPathV1.Unknown,
        },
        value.ServerRegion ?? _options.Endpoints.Region,
        value.StartedAtUtc,
        value.OccurredAtUtc,
        value.Kind is ClientSessionEventKind.Ended or ClientSessionEventKind.Failed
            ? MapEndReason(value.EndReason)
            : null,
        value.FailureStage,
        value.FailureCode,
        value.UsedTurn,
        value.ReconnectCount,
        _options.AppVersion);

    private static SessionEndReasonV1 MapEndReason(string? value) => value switch
    {
        "EndedByViewer" or "EndedBySharer" => SessionEndReasonV1.Completed,
        "PermissionDeclined" or "PermissionBlocked" or "RemoteBusy" => SessionEndReasonV1.PermissionDenied,
        "PermissionTimeout" or "RequestTimeout" or "NegotiationTimeout" or "ConnectTimeout" => SessionEndReasonV1.TimedOut,
        "SignalingLost" or "MediaLost" or "DeviceOffline" or "ReconnectFailed" => SessionEndReasonV1.NetworkLost,
        "AuthenticationMismatch" => SessionEndReasonV1.AuthenticationFailed,
        "ApplicationShutdown" or "ResumeConfirmationRequired" => SessionEndReasonV1.Cancelled,
        _ => SessionEndReasonV1.Error,
    };

    private async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest payload,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, authenticated);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        return await SendAsync<TResponse>(request, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
               ?? throw new CloudPlatformException("invalid_response", "The cloud returned an empty response.", permanent: false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, bool authenticated)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-PeerOnQ-Protocol", _options.ProtocolVersion);
        if (authenticated)
        {
            if (string.IsNullOrWhiteSpace(_accessToken))
                throw new CloudPlatformException("not_authenticated", "Cloud authentication is required.", permanent: false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "authentication_failed",
            HttpStatusCode.Forbidden => "access_denied",
            HttpStatusCode.Conflict => "identity_conflict",
            HttpStatusCode.Gone => "expired",
            HttpStatusCode.TooManyRequests => "rate_limited",
            HttpStatusCode.UpgradeRequired => "unsupported_version",
            _ => $"http_{(int)response.StatusCode}",
        };
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length <= 64 * 1024)
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.TryGetProperty("code", out var property)
                    && property.GetString() is { Length: > 0 and <= 64 } typedCode)
                {
                    code = typedCode;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Status-derived error code remains safe and sufficient.
        }

        var permanent = response.StatusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.Conflict
            or HttpStatusCode.Gone
            or HttpStatusCode.UpgradeRequired;
        var retryAfter = response.StatusCode != HttpStatusCode.TooManyRequests
            ? null
            : response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
        throw new CloudPlatformException(code, $"PeerOnQ cloud request failed ({(int)response.StatusCode}).", permanent)
        {
            RetryAfter = retryAfter is { } wait
                ? TimeSpan.FromTicks(Math.Clamp(wait.Ticks, TimeSpan.TicksPerSecond, MaximumRetryAfter.Ticks))
                : null,
        };
    }

    private static string ErrorCodeOf(Exception exception) => exception switch
    {
        CloudPlatformException cloud => cloud.Code,
        HubException hub => PresenceErrorCode(hub.Message),
        _ => "cloud_unavailable",
    };

    // Detailed hub errors are off, so the client sees "An unexpected error occurred invoking
    // 'Register' on the server. HubException: <code>"; keep only a well-formed code.
    private static string PresenceErrorCode(string message)
    {
        const string marker = "HubException: ";
        var start = message.LastIndexOf(marker, StringComparison.Ordinal);
        var code = (start < 0 ? message : message[(start + marker.Length)..]).Trim();
        return code.Length is > 0 and <= 64 && code.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            ? code
            : "presence_error";
    }

    // A rate limit, or a request the server rejects as invalid, fails the same way again within
    // the fast burst, so both wait at least the cooldown (or the server's Retry-After) instead.
    private static TimeSpan RetryDelay(int failureCount, string errorCode, Exception exception, TimeSpan cooldown)
    {
        var delay = BoundedDelay(failureCount - 1);
        var floor = errorCode switch
        {
            "rate_limited" => (exception as CloudPlatformException)?.RetryAfter ?? cooldown,
            _ when errorCode.StartsWith("invalid_", StringComparison.Ordinal) => cooldown,
            _ => TimeSpan.Zero,
        };
        return delay > floor ? delay : floor;
    }

    private void SetState(CloudPlatformState state)
    {
        if (State == state) return;
        if (state == CloudPlatformState.Online || State == CloudPlatformState.Online)
            _logger.LogInformation("Cloud platform {State}", state);
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private static TimeSpan BoundedDelay(int attempt)
    {
        var capped = Math.Min(30_000, 500 * Math.Pow(2, Math.Min(attempt, 6)));
        var jitter = capped * 0.2 * ((Random.Shared.NextDouble() * 2) - 1);
        return TimeSpan.FromMilliseconds(Math.Max(250, capped + jitter));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (_runTask is not null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogDebug("Cloud platform background loop did not finish before shutdown deadline");
            }
        }

        if (_presence is not null) await _presence.DisposeAsync();
        _accessToken = null;
        _tokenExpiresAtUtc = default;
        _signalingAttestation = null;
        _signalingAttestationExpiresAtUtc = default;
        _authenticationGate.Dispose();
        _lifetime.Dispose();
        SetState(CloudPlatformState.Stopped);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32,
    };

    public static ArchitectureKindV1 CurrentArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => ArchitectureKindV1.X64,
        Architecture.Arm64 => ArchitectureKindV1.Arm64,
        Architecture.X86 => ArchitectureKindV1.X86,
        Architecture.Arm => ArchitectureKindV1.Arm,
        _ => throw new PlatformNotSupportedException("PeerOnQ cloud registration does not support this architecture."),
    };
}
