using System.Security.Cryptography;
using System.Text;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Signaling.Server;
using PeerOnQ.Transport;
using PeerOnQ.Transport.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PeerOnQ.Signaling.Tests;

/// <summary>
/// A real signaling server on a real loopback socket. Tests talk to it with the production
/// WebSocket client, so the whole control plane is exercised end to end.
/// </summary>
public sealed class SignalingHarness : IAsyncDisposable
{
    private WebApplication? _app;

    public Uri WebSocketUri { get; private set; } = null!;
    public Uri HealthUri { get; private set; } = null!;

    public static async Task<SignalingHarness> StartAsync(
        Action<SignalingOptions>? configure = null,
        IReadOnlyDictionary<string, string>? configuration = null)
    {
        var harness = new SignalingHarness();

        var arguments = new List<string> { "--environment", "Testing" };
        if (configuration is not null)
        {
            arguments.AddRange(configuration.Select(pair => $"--{pair.Key}={pair.Value}"));
        }

        var app = SignalingApp.Create(arguments.ToArray(), options =>
        {
            // Keep the defaults meaningful but fast enough for a test run.
            options.PermissionTimeout = TimeSpan.FromSeconds(30);
            options.NegotiationTimeout = TimeSpan.FromSeconds(30);
            options.SweepInterval = TimeSpan.FromMilliseconds(200);

            // Tests must not share pins or touch the working directory.
            options.PinStorePath = null;
            options.Attestation.Required = false;
            options.Attestation.AllowDevelopmentTofuFallback = true;
            configure?.Invoke(options);
        });

        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        var http = new Uri(address);
        harness._app = app;
        harness.WebSocketUri = new Uri($"ws://127.0.0.1:{http.Port}{SignalingApp.WebSocketPath}");
        harness.HealthUri = new Uri($"http://127.0.0.1:{http.Port}/health");

        return harness;
    }

    public T Service<T>() where T : notnull => _app!.Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        var app = _app;
        if (app is null) return;
        _app = null;

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await app.StopAsync(stopTimeout.Token);
        await app.DisposeAsync();
    }
}

/// <summary>A test device: identity plus the ECDSA key that proves it owns its PeerOnQ ID.</summary>
public sealed class TestDevice : IRegistrationProofProvider, ISignalingAttestationProvider, IDisposable
{
    private readonly ECDsa _key;

    public TestDevice(string displayName, ECDsa? key = null)
    {
        _key = key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Identity = DeviceIdentity.Create(displayName) with
        {
            PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
        };
    }

    public DeviceIdentity Identity { get; private set; }

    public PeerOnQId Id => Identity.PublicId;
    public string? SignalingAttestation { get; set; }
    public int SignalingAttestationRequests { get; private set; }

    /// <summary>Impersonation helper: same PeerOnQ ID, different key pair.</summary>
    public TestDevice WithNewKeyPair()
    {
        var other = new TestDevice(Identity.DisplayName);
        other.Identity = other.Identity with { InternalId = Identity.InternalId, PublicId = Identity.PublicId };
        return other;
    }

    public Task<string> ComputeRegistrationProofAsync(string challenge, CancellationToken cancellationToken = default) =>
        Task.FromResult(Convert.ToBase64String(
            _key.SignData(Encoding.UTF8.GetBytes(challenge), HashAlgorithmName.SHA256)));

    public Task<string?> GetSignalingAttestationAsync(CancellationToken cancellationToken = default)
    {
        SignalingAttestationRequests++;
        return Task.FromResult(SignalingAttestation);
    }

    public WebSocketSignalingClient CreateClient(
        Uri serverUri,
        ClientCapabilityManifest? clientCapabilities = null,
        TimeProvider? timeProvider = null,
        TimeSpan? heartbeatInterval = null,
        bool autoReconnect = false)
    {
        var options = new SignalingClientOptions
        {
            ServerUri = serverUri,
            ClientCapabilities = clientCapabilities ?? TestClientCapabilities.All(),
            HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(5),
            AutoReconnect = autoReconnect,
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
            ReconnectJitterFraction = 0,
        };

        return new WebSocketSignalingClient(
            options,
            this,
            LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning))
                .CreateLogger<WebSocketSignalingClient>(),
            this,
            timeProvider);
    }

    public void Dispose() => _key.Dispose();
}

internal sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
}

public static class TestClientCapabilities
{
    public static ClientCapabilityManifest All(
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? optionalFeatures = null,
        IReadOnlyList<string>? requiredServerCapabilities = null) => new()
        {
            Platform = PeerOnQClientPlatforms.Windows,
            Capabilities = capabilities ??
        [
            EndpointCapabilityNames.ClipboardReceive,
            EndpointCapabilityNames.ClipboardSend,
            EndpointCapabilityNames.DisplaySelectAccept,
            EndpointCapabilityNames.DisplaySelectRequest,
            EndpointCapabilityNames.FileReceive,
            EndpointCapabilityNames.FileSend,
            EndpointCapabilityNames.InputInject,
            EndpointCapabilityNames.InputSend,
            EndpointCapabilityNames.ScreenCapture,
            EndpointCapabilityNames.ScreenRender,
            EndpointCapabilityNames.HybridPostQuantumSecure,
            EndpointCapabilityNames.SessionHost,
            EndpointCapabilityNames.SessionReconnect,
            EndpointCapabilityNames.SessionViewer,
            EndpointCapabilityNames.UnattendedAccept,
            EndpointCapabilityNames.UnattendedRequest,
            EndpointCapabilityNames.SupportInvitationAccept,
            EndpointCapabilityNames.SupportInvitationRequest,
        ],
            OptionalFeatures = optionalFeatures ??
        [
            OptionalProtocolFeatureNames.SafeUnknownMessages,
            OptionalProtocolFeatureNames.SessionCapabilityDetails,
        ],
            RequiredServerCapabilities = requiredServerCapabilities ??
        [
            SignalingServerCapabilityNames.AuthenticatedRegistration,
            SignalingServerCapabilityNames.SessionCapabilityGate,
            SignalingServerCapabilityNames.SessionRouting,
        ],
        };
}

public static class Wait
{
    /// <summary>Waits for an event-driven condition without sleeping for a fixed period.</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }

        return condition();
    }

    public static async Task<T> ForAsync<T>(TaskCompletionSource<T> source, TimeSpan? timeout = null)
    {
        var completed = await Task.WhenAny(source.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(10)));
        if (completed != source.Task)
        {
            throw new TimeoutException("The expected signaling event did not arrive in time.");
        }

        return await source.Task;
    }
}
