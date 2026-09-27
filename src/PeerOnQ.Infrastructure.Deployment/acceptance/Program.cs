using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PeerOnQ.Shared.Contracts.Security;
using PeerOnQ.Transport.Protocol;

return await Phase6Acceptance.RunAsync();

internal static class Phase6Acceptance
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync()
    {
        try
        {
            var settings = AcceptanceSettings.Load();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var gateway = new GatewayClient(settings);

            var installationId = Guid.NewGuid();
            var publicKey = Convert.ToBase64String(deviceKey.ExportSubjectPublicKeyInfo());
            var enrollment = await EnrollAsync(
                gateway, settings, installationId, deviceKey, publicKey, deadline.Token);
            if (!Regex.IsMatch(enrollment.PublicDeviceId, "^[0-9]{3}(?:-[0-9]{3}){3}$",
                    RegexOptions.CultureInvariant))
            {
                throw new AcceptanceException("Cloud returned an invalid routing-alias format.");
            }

            await gateway.PostJsonAsync<object, InstallationResult>(
                settings.ApiHost,
                "/v1/installations/register",
                new
                {
                    installationId,
                    platform = "Windows",
                    architecture = "X64",
                    appVersion = settings.AppVersion,
                    osVersion = "Windows 11 acceptance",
                    installChannel = "Development",
                    protocolVersion = "1",
                    region = settings.Region,
                },
                enrollment.AccessToken,
                deadline.Token);

            await VerifyPresenceAsync(gateway, settings, enrollment.AccessToken, deadline.Token);
            await VerifySignalingAcceptedAsync(
                gateway, settings, enrollment.PublicDeviceId, publicKey, deviceKey,
                enrollment.SignalingAttestation, deadline.Token);

            var freshForMismatch = await EnrollAsync(
                gateway, settings, installationId, deviceKey, publicKey, deadline.Token);
            using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var otherPublicKey = Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo());
            await VerifySignalingRejectedAsync(
                gateway, settings, enrollment.PublicDeviceId, otherPublicKey, otherKey,
                freshForMismatch.SignalingAttestation, includeAttestation: true,
                "different-device-key", deadline.Token);

            await VerifySignalingRejectedAsync(
                gateway, settings, enrollment.PublicDeviceId, publicKey, deviceKey,
                attestation: null, includeAttestation: false, "missing-attestation", deadline.Token);

            using var signer = LoadPrivateKey(settings.AttestationPrivateKeyFile);
            var now = DateTimeOffset.UtcNow;
            var wrongAudience = IssueAttestation(
                signer, settings, enrollment, publicKey, "peeronq-wrong-audience",
                now, now.AddMinutes(5));
            await VerifySignalingRejectedAsync(
                gateway, settings, enrollment.PublicDeviceId, publicKey, deviceKey,
                wrongAudience, includeAttestation: true, "wrong-audience", deadline.Token);

            var expired = IssueAttestation(
                signer, settings, enrollment, publicKey, settings.AttestationAudience,
                now.AddMinutes(-10), now.AddMinutes(-5));
            await VerifySignalingRejectedAsync(
                gateway, settings, enrollment.PublicDeviceId, publicKey, deviceKey,
                expired, includeAttestation: true, "expired-attestation", deadline.Token);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                cloudEnrollment = "pass",
                boundedRegistrationChallenge = "pass",
                installationConfirmation = "pass",
                presenceRegisterAndHeartbeat = "pass",
                signalingAttestation = "pass",
                missingAttestationRejected = "pass",
                differentDeviceKeyRejected = "pass",
                wrongAudienceRejected = "pass",
                expiredAttestationRejected = "pass",
                routingAliasFormat = "pass",
            }, JsonOptions));
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Phase 6 acceptance failed: {Sanitize(exception.Message)}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Phase 6 acceptance failed: bounded acceptance deadline exceeded.");
            return 1;
        }
    }

    private static async Task<EnrollmentResult> EnrollAsync(
        GatewayClient gateway,
        AcceptanceSettings settings,
        Guid installationId,
        ECDsa deviceKey,
        string publicKey,
        CancellationToken cancellationToken)
    {
        var challengeRequestedAt = DateTimeOffset.UtcNow;
        var challenge = await gateway.PostJsonAsync<object, DeviceChallenge>(
            settings.ApiHost,
            "/v1/devices/register",
            new
            {
                installationId,
                displayName = "Phase 6 Acceptance Device",
                publicKeySpkiBase64 = publicKey,
                platform = "Windows",
                architecture = "X64",
                appVersion = settings.AppVersion,
                osVersion = "Windows 11 acceptance",
                installChannel = "Development",
                region = settings.Region,
                protocolVersion = "1",
            },
            bearerToken: null,
            cancellationToken);
        if (challenge.ExpiresAtUtc <= challengeRequestedAt
            || challenge.ExpiresAtUtc > challengeRequestedAt.AddSeconds(105))
        {
            throw new AcceptanceException("Cloud returned an out-of-policy registration challenge lifetime.");
        }

        var proof = Convert.ToBase64String(deviceKey.SignData(
            Encoding.UTF8.GetBytes(challenge.CanonicalPayload), HashAlgorithmName.SHA256));
        var result = await gateway.PostJsonAsync<object, EnrollmentResult>(
            settings.ApiHost,
            "/v1/devices/authenticate",
            new
            {
                challengeId = challenge.ChallengeId,
                signatureBase64 = proof,
                publicKeySpkiBase64 = publicKey,
                installationId,
            },
            bearerToken: null,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken)
            || string.IsNullOrWhiteSpace(result.SignalingAttestation)
            || result.DeviceId == Guid.Empty
            || result.InstallationId != installationId)
        {
            throw new AcceptanceException("Cloud authentication response is incomplete.");
        }
        return result;
    }

    private static async Task VerifyPresenceAsync(
        GatewayClient gateway,
        AcceptanceSettings settings,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var negotiated = await gateway.PostEmptyAsync<NegotiateResponse>(
            settings.PresenceHost,
            "/presence/v1/hub/negotiate?negotiateVersion=1",
            accessToken,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(negotiated.ConnectionToken))
            throw new AcceptanceException("Presence negotiation returned no connection token.");

        var path = $"/presence/v1/hub?id={Uri.EscapeDataString(negotiated.ConnectionToken)}" +
                   $"&access_token={Uri.EscapeDataString(accessToken)}";
        await using var socket = await gateway.ConnectWebSocketAsync(
            settings.PresenceHost, path, cancellationToken);
        await socket.SendTextAsync("{\"protocol\":\"json\",\"version\":1}\u001e", cancellationToken);
        var handshake = await socket.ReceiveSignalRJsonAsync(cancellationToken);
        if (handshake.RootElement.TryGetProperty("error", out _))
            throw new AcceptanceException("Presence SignalR handshake was rejected.");

        await InvokePresenceAsync(socket, "1", "Register", "Connecting", settings, cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(3.1), cancellationToken);
        await InvokePresenceAsync(socket, "2", "Heartbeat", "Online", settings, cancellationToken);
        await socket.CloseAsync(cancellationToken);
    }

    private static async Task InvokePresenceAsync(
        WebSocketLease socket,
        string invocationId,
        string target,
        string state,
        AcceptanceSettings settings,
        CancellationToken cancellationToken)
    {
        var frame = JsonSerializer.Serialize(new
        {
            type = 1,
            invocationId,
            target,
            arguments = new[]
            {
                new
                {
                    state,
                    appVersion = settings.AppVersion,
                    region = settings.Region,
                    sentAtUtc = DateTimeOffset.UtcNow,
                },
            },
        }, JsonOptions) + '\u001e';
        await socket.SendTextAsync(frame, cancellationToken);
        while (true)
        {
            using var response = await socket.ReceiveSignalRJsonAsync(cancellationToken);
            var root = response.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetInt32() != 3
                || !root.TryGetProperty("invocationId", out var id)
                || id.GetString() != invocationId)
            {
                continue;
            }
            if (root.TryGetProperty("error", out _))
                throw new AcceptanceException($"Presence {target} invocation was rejected.");
            return;
        }
    }

    private static async Task VerifySignalingAcceptedAsync(
        GatewayClient gateway,
        AcceptanceSettings settings,
        string deviceId,
        string publicKey,
        ECDsa deviceKey,
        string attestation,
        CancellationToken cancellationToken)
    {
        await using var socket = await gateway.ConnectWebSocketAsync(
            settings.SignalingHost, "/ws", cancellationToken);
        var response = await RegisterWithSignalingAsync(
            socket, settings, deviceId, publicKey, deviceKey, attestation,
            includeAttestation: true, cancellationToken);
        if (!response.RootElement.TryGetProperty("type", out var type)
            || type.GetString() != "registered")
        {
            throw new AcceptanceException("Valid Cloud attestation was rejected by Signaling.");
        }
        await socket.CloseAsync(cancellationToken);
    }

    private static async Task VerifySignalingRejectedAsync(
        GatewayClient gateway,
        AcceptanceSettings settings,
        string deviceId,
        string publicKey,
        ECDsa proofKey,
        string? attestation,
        bool includeAttestation,
        string scenario,
        CancellationToken cancellationToken)
    {
        await using var socket = await gateway.ConnectWebSocketAsync(
            settings.SignalingHost, "/ws", cancellationToken);
        try
        {
            using var response = await RegisterWithSignalingAsync(
                socket, settings, deviceId, publicKey, proofKey, attestation,
                includeAttestation, cancellationToken);
            if (response.RootElement.TryGetProperty("type", out var type)
                && type.GetString() == "registered")
            {
                throw new AcceptanceException($"Signaling accepted the {scenario} scenario.");
            }
        }
        catch (WebSocketException)
        {
            // A policy close is an expected fail-closed response.
        }
    }

    private static async Task<JsonDocument> RegisterWithSignalingAsync(
        WebSocketLease socket,
        AcceptanceSettings settings,
        string deviceId,
        string publicKey,
        ECDsa proofKey,
        string? attestation,
        bool includeAttestation,
        CancellationToken cancellationToken)
    {
        await socket.SendJsonAsync(new
        {
            type = "hello",
            mid = Guid.NewGuid().ToString("N"),
            deviceId,
            displayName = "Phase 6 Acceptance Device",
            clientVersion = settings.AppVersion,
            protocolVersion = SignalingProtocol.CurrentVersion,
            clientCapabilities = new
            {
                platform = PeerOnQClientPlatforms.Windows,
                capabilities = Array.Empty<string>(),
                optionalFeatures = new[] { OptionalProtocolFeatureNames.SafeUnknownMessages },
                requiredServerCapabilities = new[]
                {
                    SignalingServerCapabilityNames.AuthenticatedRegistration,
                },
            },
        }, cancellationToken);
        using var challenge = await socket.ReceiveJsonAsync(cancellationToken);
        if (!challenge.RootElement.TryGetProperty("type", out var challengeType)
            || challengeType.GetString() != "challenge"
            || !challenge.RootElement.TryGetProperty("nonce", out var nonceElement)
            || string.IsNullOrEmpty(nonceElement.GetString()))
        {
            throw new AcceptanceException("Signaling did not issue a registration challenge.");
        }
        var proof = Convert.ToBase64String(proofKey.SignData(
            Encoding.UTF8.GetBytes(nonceElement.GetString()!), HashAlgorithmName.SHA256));
        var register = new Dictionary<string, object?>
        {
            ["type"] = "register",
            ["mid"] = Guid.NewGuid().ToString("N"),
            ["deviceId"] = deviceId,
            ["displayName"] = "Phase 6 Acceptance Device",
            ["proof"] = proof,
            ["publicKey"] = publicKey,
        };
        if (includeAttestation) register["attestation"] = attestation;
        await socket.SendJsonAsync(register, cancellationToken);
        return await socket.ReceiveJsonAsync(cancellationToken);
    }

    private static ECDsa LoadPrivateKey(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 or > 16 * 1024)
            throw new AcceptanceException("Acceptance attestation key file is unavailable or invalid.");
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(fullPath));
            if (key.KeySize != 256)
                throw new AcceptanceException("Acceptance attestation key is not ECDSA P-256.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static string IssueAttestation(
        ECDsa signer,
        AcceptanceSettings settings,
        EnrollmentResult enrollment,
        string publicKey,
        string audience,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var spki = Convert.FromBase64String(publicKey);
        return SignalingAttestationTokenV1.Issue(
            new SignalingAttestationClaimsV1(
                settings.AttestationIssuer,
                audience,
                enrollment.PublicDeviceId,
                enrollment.DeviceId,
                enrollment.InstallationId,
                Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                SignalingAttestationTokenV1.ComputeKeyId(signer.ExportSubjectPublicKeyInfo()),
                issuedAt.ToUnixTimeSeconds(),
                expiresAt.ToUnixTimeSeconds(),
                SignalingAttestationTokenV1.Base64UrlEncode(RandomNumberGenerator.GetBytes(16))),
            signer);
    }

    private static string Sanitize(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 300 ? singleLine : singleLine[..300];
    }

    private sealed record DeviceChallenge(string ChallengeId, string CanonicalPayload, DateTimeOffset ExpiresAtUtc);
    private sealed record EnrollmentResult(
        string AccessToken,
        DateTimeOffset ExpiresAtUtc,
        Guid DeviceId,
        Guid InstallationId,
        string PublicDeviceId,
        string SignalingAttestation,
        DateTimeOffset SignalingAttestationExpiresAtUtc);
    private sealed record InstallationResult(
        Guid InstallationId,
        DateTimeOffset RegisteredAtUtc,
        bool IsNew,
        bool IsBlocked,
        string? MinimumSupportedVersion);
    private sealed record NegotiateResponse(string ConnectionToken);
}

internal sealed class GatewayClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AcceptanceSettings _settings;
    private readonly HttpClient _http;

    public GatewayClient(AcceptanceSettings settings)
    {
        _settings = settings;
        _http = new HttpClient(CreateHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
        string host,
        string path,
        TRequest payload,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildHttpsUri(host, path))
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        SetBearer(request, bearerToken);
        return await SendAsync<TResponse>(request, cancellationToken);
    }

    public async Task<TResponse> PostEmptyAsync<TResponse>(
        string host,
        string path,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildHttpsUri(host, path));
        SetBearer(request, bearerToken);
        return await SendAsync<TResponse>(request, cancellationToken);
    }

    public async Task<WebSocketLease> ConnectWebSocketAsync(
        string host,
        string path,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        var invoker = new HttpMessageInvoker(CreateHandler(), disposeHandler: true);
        try
        {
            await socket.ConnectAsync(BuildWebSocketUri(host, path), invoker, cancellationToken);
            return new WebSocketLease(socket, invoker);
        }
        catch
        {
            socket.Dispose();
            invoker.Dispose();
            throw;
        }
    }

    private async Task<TResponse> SendAsync<TResponse>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var safeBody = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (safeBody.Length > 240) safeBody = safeBody[..240];
            throw new AcceptanceException($"HTTP {(int)response.StatusCode} from {request.RequestUri!.Host}: {safeBody}");
        }
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
               ?? throw new AcceptanceException($"Empty JSON response from {request.RequestUri!.Host}.");
    }

    private SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(_settings.GatewayAddress,
                        context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        if (_settings.AllowUntrustedDevelopmentCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback =
                static (_, _, _, _) => true;
        }
        return handler;
    }

    private Uri BuildHttpsUri(string host, string path) =>
        new($"https://{host}:{_settings.GatewayPort}{path}");

    private Uri BuildWebSocketUri(string host, string path) =>
        new($"wss://{host}:{_settings.GatewayPort}{path}");

    private static void SetBearer(HttpRequestMessage request, string? bearerToken)
    {
        if (!string.IsNullOrEmpty(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class WebSocketLease(ClientWebSocket socket, HttpMessageInvoker invoker) : IAsyncDisposable
{
    private const char RecordSeparator = '\u001e';
    private readonly Queue<string> _signalRFrames = new();

    public Task SendJsonAsync(object value, CancellationToken cancellationToken) =>
        SendTextAsync(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken);

    public async Task SendTextAsync(string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public async Task<JsonDocument> ReceiveJsonAsync(CancellationToken cancellationToken)
    {
        var text = await ReceiveTextAsync(cancellationToken)
                   ?? throw new WebSocketException("The WebSocket closed before a response was received.");
        return JsonDocument.Parse(text);
    }

    public async Task<JsonDocument> ReceiveSignalRJsonAsync(CancellationToken cancellationToken)
    {
        while (_signalRFrames.Count == 0)
        {
            var text = await ReceiveTextAsync(cancellationToken)
                       ?? throw new WebSocketException("Presence closed before a SignalR response was received.");
            foreach (var frame in text.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
                _signalRFrames.Enqueue(frame);
        }
        return JsonDocument.Parse(_signalRFrames.Dequeue());
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "acceptance_complete", cancellationToken);
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket target, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await target.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new WebSocketException("Unexpected binary WebSocket frame.");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 64 * 1024)
                throw new WebSocketException("WebSocket response exceeded the acceptance bound.");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private Task<string?> ReceiveTextAsync(CancellationToken cancellationToken) =>
        ReceiveTextAsync(socket, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "acceptance_dispose", timeout.Token);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
        }
        socket.Dispose();
        invoker.Dispose();
    }
}

internal sealed record AcceptanceSettings(
    IPAddress GatewayAddress,
    int GatewayPort,
    string ApiHost,
    string PresenceHost,
    string SignalingHost,
    string Region,
    string AppVersion,
    string AttestationIssuer,
    string AttestationAudience,
    string AttestationPrivateKeyFile,
    bool AllowUntrustedDevelopmentCertificate)
{
    public static AcceptanceSettings Load()
    {
        var address = IPAddress.Parse(Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_GATEWAY_ADDRESS") ?? "127.0.0.1");
        var port = int.Parse(Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_GATEWAY_PORT") ?? "8443");
        var apiHost = Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_API_HOST") ?? "api.dev.localhost";
        var presenceHost = Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_PRESENCE_HOST") ?? "presence.dev.localhost";
        var signalingHost = Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_SIGNALING_HOST") ?? "signal.dev.localhost";
        var allowUntrusted = string.Equals(
            Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_ALLOW_UNTRUSTED_DEVELOPMENT_CERTIFICATE"),
            "true", StringComparison.OrdinalIgnoreCase);
        if (port is <= 0 or > 65535)
            throw new AcceptanceException("Acceptance gateway port is invalid.");
        if (allowUntrusted && (!IPAddress.IsLoopback(address)
                               || new[] { apiHost, presenceHost, signalingHost }
                                   .Any(host => !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))))
        {
            throw new AcceptanceException("Untrusted-certificate acceptance is restricted to loopback development hosts.");
        }
        var privateKeyFile = Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_ATTESTATION_PRIVATE_KEY_FILE");
        if (string.IsNullOrWhiteSpace(privateKeyFile))
            throw new AcceptanceException("PEERONQ_ACCEPTANCE_ATTESTATION_PRIVATE_KEY_FILE is required.");
        return new AcceptanceSettings(
            address,
            port,
            apiHost,
            presenceHost,
            signalingHost,
            Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_REGION") ?? "local-dev",
            Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_APP_VERSION") ?? "6.0.0",
            Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_ATTESTATION_ISSUER") ?? "https://api.dev.localhost",
            Environment.GetEnvironmentVariable("PEERONQ_ACCEPTANCE_ATTESTATION_AUDIENCE") ?? "peeronq-signaling",
            privateKeyFile,
            allowUntrusted);
    }
}

internal sealed class AcceptanceException(string message) : Exception(message);
