using System.Reflection;
using Android.Content;
using Android.OS;
using Android.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Identity;
using PeerOnQ.Application.Security;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media;
using PeerOnQ.Platform.Android;
using PeerOnQ.Transport;

namespace PeerOnQ.App.Android;

public sealed record AndroidPresentedFrame(
    PeerOnQ.Domain.Sessions.SessionId SessionId,
    PresentedVideoFrame Frame);

public sealed class AndroidAppServices : IAsyncDisposable
{
    private const string DevelopmentCertificateResource = "PeerOnQ.DevelopmentRoot.cer";
    private int _disposed;

    private AndroidAppServices(
        DeviceIdentity identity,
        HybridDeviceIdentityService hybridIdentity,
        WebSocketSignalingClient signaling,
        SessionCoordinator coordinator)
    {
        Identity = identity;
        HybridIdentity = hybridIdentity;
        Signaling = signaling;
        Coordinator = coordinator;
    }

    public DeviceIdentity Identity { get; }
    public HybridDeviceIdentityService HybridIdentity { get; }
    public WebSocketSignalingClient Signaling { get; }
    public SessionCoordinator Coordinator { get; }

    public event EventHandler<AndroidPresentedFrame>? FramePresented;

    public static async Task<AndroidAppServices> CreateAsync(
        Context context,
        ISurfaceHolder surfaceHolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(surfaceHolder);

        var secretStore = new AndroidKeyStoreDeviceSecretStore(context);
        var provisioning = new DeviceProvisioningService(
            new AndroidDeviceIdentityRepository(context),
            secretStore,
            NullLogger<DeviceProvisioningService>.Instance);
        var identity = await provisioning.GetOrCreateAsync(CreateDisplayName(), cancellationToken);
        var hybridIdentity = new HybridDeviceIdentityService(secretStore, identity);
        WebSocketSignalingClient? signaling = null;
        SessionCoordinator? coordinator = null;
        AndroidAppServices? services = null;
        try
        {
            var appVersion = typeof(AndroidAppServices).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AndroidAppServices).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            var serverUri = ReadSignalingUri();
            signaling = new WebSocketSignalingClient(
                new SignalingClientOptions
                {
                    ServerUri = serverUri,
                    ClientCapabilities = AndroidClientCapabilityProfile.Create(),
                    ClientVersion = appVersion,
                    AutoReconnect = true,
                    MaxReconnectAttempts = 10,
                    MaxReconnectWindow = TimeSpan.FromSeconds(60),
                    AllowInsecureTransport = false,
                    TrustedDevelopmentRootCertificate = ReadDevelopmentCertificate(),
                },
                provisioning,
                NullLogger<WebSocketSignalingClient>.Instance);

            var mediaEngine = new WebRtcMediaEngine(
                NullLoggerFactory.Instance,
                viewerVideoSinkFactory: sessionId =>
                {
                    var sink = new AndroidMediaCodecVideoSink(surfaceHolder);
                    sink.FramePresented += (_, presentation) =>
                    {
                        coordinator?.ReportFrameRendered(sessionId, presentation.Frame);
                        services?.FramePresented?.Invoke(
                            services,
                            new AndroidPresentedFrame(sessionId, presentation.Frame));
                    };
                    return sink;
                });

            coordinator = new SessionCoordinator(
                signaling,
                mediaEngine,
                DenyIncomingPermissionPrompt.Instance,
                new AndroidBlockedDeviceStore(context),
                new AndroidSessionAuditLog(context),
                static () => new UnsupportedAndroidCaptureSource(),
                SessionOptions.Default,
                NullLogger<SessionCoordinator>.Instance,
                hybridIdentity: hybridIdentity);
            services = new AndroidAppServices(identity, hybridIdentity, signaling, coordinator);
            return services;
        }
        catch
        {
            if (coordinator is not null) await coordinator.DisposeAsync();
            if (signaling is not null) await signaling.DisposeAsync();
            hybridIdentity.Dispose();
            throw;
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        Signaling.ConnectAsync(Identity, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Coordinator.DisposeAsync();
        await Signaling.DisposeAsync();
        HybridIdentity.Dispose();
    }

    private static string CreateDisplayName()
    {
        var manufacturer = Build.Manufacturer?.Trim();
        var model = Build.Model?.Trim();
        var combined = string.Join(' ', new[] { manufacturer, model }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(combined) ? "Android device" : combined;
    }

    private static Uri ReadSignalingUri()
    {
        var metadata = typeof(AndroidAppServices).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "PeerOnQSignalingUrl")?.Value;
        if (!Uri.TryCreate(metadata, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeWss
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.AbsolutePath.TrimEnd('/').Equals("/ws", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This Android preview is not configured. Build it with a wss:// signaling URL ending in /ws.");
        }
        return uri;
    }

    private static byte[]? ReadDevelopmentCertificate()
    {
        using var stream = typeof(AndroidAppServices).Assembly
            .GetManifestResourceStream(DevelopmentCertificateResource);
        if (stream is null) return null;
        if (stream.Length is <= 0 or > 128 * 1024)
            throw new InvalidOperationException("The embedded development root certificate is invalid.");
        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private sealed class DenyIncomingPermissionPrompt : IPermissionPrompt
    {
        public static DenyIncomingPermissionPrompt Instance { get; } = new();

        public Task<PermissionDecision> AskAsync(
            PermissionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PermissionDecision.Decline);
    }
}
