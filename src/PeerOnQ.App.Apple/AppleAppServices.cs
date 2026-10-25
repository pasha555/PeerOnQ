using System.Reflection;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Identity;
using PeerOnQ.Application.Security;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media;
using PeerOnQ.Platform.Apple;
using PeerOnQ.Transport;
using UIKit;

namespace PeerOnQ.App.Apple;

public sealed class AppleAppServices : IAsyncDisposable
{
    private const string DevelopmentCertificateResource = "PeerOnQ.DevelopmentRoot.cer";
    private int _disposed;

    private AppleAppServices(
        AppleClientPlatform platform,
        DeviceIdentity identity,
        HybridDeviceIdentityService hybridIdentity,
        WebSocketSignalingClient signaling,
        SessionCoordinator coordinator)
    {
        Platform = platform;
        Identity = identity;
        HybridIdentity = hybridIdentity;
        Signaling = signaling;
        Coordinator = coordinator;
    }

    public AppleClientPlatform Platform { get; }
    public DeviceIdentity Identity { get; }
    public HybridDeviceIdentityService HybridIdentity { get; }
    public WebSocketSignalingClient Signaling { get; }
    public SessionCoordinator Coordinator { get; }

    public static async Task<AppleAppServices> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst())
            throw new PlatformNotSupportedException("PeerOnQ Apple requires iOS, iPadOS or Mac Catalyst.");

        var platform = DetectPlatform();
        var defaults = NSUserDefaults.StandardUserDefaults;
        var secretStore = new AppleKeychainDeviceSecretStore();
        var provisioning = new DeviceProvisioningService(
            new AppleDeviceIdentityRepository(defaults),
            secretStore,
            NullLogger<DeviceProvisioningService>.Instance);
        var identity = await provisioning.GetOrCreateAsync(CreateDisplayName(platform), cancellationToken);
        var hybridIdentity = new HybridDeviceIdentityService(secretStore, identity);
        WebSocketSignalingClient? signaling = null;
        SessionCoordinator? coordinator = null;
        try
        {
            var appVersion = typeof(AppleAppServices).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AppleAppServices).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            var serverUri = ReadSignalingUri();
            signaling = new WebSocketSignalingClient(
                new SignalingClientOptions
                {
                    ServerUri = serverUri,
                    ClientCapabilities = AppleClientCapabilityProfile.Create(platform),
                    ClientVersion = appVersion,
                    AutoReconnect = true,
                    MaxReconnectAttempts = 10,
                    MaxReconnectWindow = TimeSpan.FromSeconds(60),
                    AllowInsecureTransport = false,
                    TrustedDevelopmentRootCertificate = ReadDevelopmentCertificate(),
                },
                provisioning,
                NullLogger<WebSocketSignalingClient>.Instance);

            coordinator = new SessionCoordinator(
                signaling,
                new WebRtcMediaEngine(NullLoggerFactory.Instance),
                DenyIncomingPermissionPrompt.Instance,
                new AppleBlockedDeviceStore(defaults),
                new AppleSessionAuditLog(defaults),
                () => new UnsupportedAppleCaptureSource(platform),
                SessionOptions.Default,
                NullLogger<SessionCoordinator>.Instance,
                hybridIdentity: hybridIdentity);

            return new AppleAppServices(platform, identity, hybridIdentity, signaling, coordinator);
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

    private static AppleClientPlatform DetectPlatform()
    {
        if (OperatingSystem.IsMacCatalyst()) return AppleClientPlatform.MacOS;
        return UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad
            ? AppleClientPlatform.IPadOS
            : AppleClientPlatform.IOS;
    }

    private static string CreateDisplayName(AppleClientPlatform platform)
    {
        var name = UIDevice.CurrentDevice.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return platform switch
        {
            AppleClientPlatform.MacOS => "Mac",
            AppleClientPlatform.IPadOS => "iPad",
            _ => "iPhone",
        };
    }

    private static Uri ReadSignalingUri()
    {
        var metadata = typeof(AppleAppServices).Assembly
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
                "This Apple preview is not configured. Build it with a wss:// signaling URL ending in /ws.");
        }
        return uri;
    }

    private static byte[]? ReadDevelopmentCertificate()
    {
        using var stream = typeof(AppleAppServices).Assembly
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
