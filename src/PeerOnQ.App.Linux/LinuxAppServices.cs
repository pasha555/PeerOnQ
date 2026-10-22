using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Identity;
using PeerOnQ.Application.Security;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Infrastructure;
using PeerOnQ.Infrastructure.Cloud;
using PeerOnQ.Infrastructure.Configuration;
using PeerOnQ.Infrastructure.Diagnostics;
using PeerOnQ.Infrastructure.Persistence;
using PeerOnQ.Media;
using PeerOnQ.Platform.Linux;
using PeerOnQ.Platform.Linux.Security;
using PeerOnQ.Shared.Contracts.V1;
using PeerOnQ.Transport;
using PeerOnQ.Transport.DataPlane;

namespace PeerOnQ.App.Linux;

public sealed class LinuxAppServices : IAsyncDisposable
{
    private const string DefaultSignalingUrl = "wss://signal.127.0.0.1.sslip.io:5443/ws";
    private const int MaximumCertificateBytes = 128 * 1024;
    private readonly Lock _disposalGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DeviceIdentity _identity;
    private readonly CloudPlatformClient? _cloud;
    private readonly HttpClient? _cloudHttp;
    private Task? _disposeTask;

    private LinuxAppServices(
        ILoggerFactory loggerFactory,
        DeviceIdentity identity,
        HybridDeviceIdentityService hybridIdentity,
        WebSocketSignalingClient signaling,
        SessionCoordinator coordinator,
        CloudPlatformClient? cloud,
        HttpClient? cloudHttp)
    {
        LoggerFactory = loggerFactory;
        _identity = identity;
        HybridIdentity = hybridIdentity;
        Signaling = signaling;
        Coordinator = coordinator;
        _cloud = cloud;
        _cloudHttp = cloudHttp;
    }

    public ILoggerFactory LoggerFactory { get; }

    public DeviceIdentity Identity => _cloud?.Identity ?? _identity;

    public HybridDeviceIdentityService HybridIdentity { get; }

    public WebSocketSignalingClient Signaling { get; }

    public SessionCoordinator Coordinator { get; }

    public static async Task<LinuxAppServices> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("PeerOnQ Linux must be started on a supported Linux desktop.");

        LinuxNativeLibraryBootstrap.Initialize();

        var paths = new PeerOnQPaths(ReadEnvironmentVariable("PEERONQ_DATA_DIR")).EnsureCreated();
        var appVersion = typeof(LinuxAppServices).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(LinuxAppServices).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        var protocolClientVersion = typeof(LinuxAppServices).Assembly.GetName().Version!.ToString(3);
        var loggerFactory = PeerOnQLogging.CreateLoggerFactory(
            paths.LogDirectory,
            appVersion,
            "Production",
            "linux-desktop");

        HybridDeviceIdentityService? hybridIdentity = null;
        WebSocketSignalingClient? signaling = null;
        SessionCoordinator? coordinator = null;
        CloudPlatformClient? cloud = null;
        HttpClient? cloudHttp = null;
        try
        {
            var database = new PeerOnQDatabase(paths.DatabaseFile);
            database.Migrate();

            var secretStore = new SecretToolDeviceSecretStore(CreateKeyringProfileId(paths.Root));
            var provisioning = new DeviceProvisioningService(
                new SqliteDeviceIdentityRepository(database),
                secretStore,
                loggerFactory.CreateLogger<DeviceProvisioningService>());
            var identity = await provisioning.GetOrCreateAsync(Environment.MachineName, cancellationToken);
            hybridIdentity = new HybridDeviceIdentityService(secretStore, identity);

            // This unsigned preview accepts an operator's complete public endpoint manifest.
            // Compiled metadata takes precedence; the shared parser keeps TLS and completeness checks.
            var cloudEndpoints = CloudEndpointConfiguration.TryCreate(
                typeof(LinuxAppServices).Assembly, allowDevelopmentEnvironmentOverrides: true);
            if (cloudEndpoints is not null)
            {
                cloudHttp = new HttpClient(new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                });
                cloud = new CloudPlatformClient(
                    cloudHttp,
                    new CloudPlatformClientOptions
                    {
                        Endpoints = cloudEndpoints,
                        InstallationId = identity.InternalId,
                        AppVersion = protocolClientVersion,
                        OsVersion = Environment.OSVersion.VersionString,
                        Architecture = CloudPlatformClient.CurrentArchitecture,
                        InstallChannel = InstallChannelV1.Development,
                        Platform = PlatformKindV1.Linux,
                    },
                    identity,
                    provisioning,
                    provisioning.AssignServerPublicIdAsync,
                    new SqliteClientTelemetryOutbox(database),
                    loggerFactory.CreateLogger<CloudPlatformClient>());
            }

            var serverUri = cloudEndpoints?.SignalingUri ?? ReadSignalingUri();
            signaling = new WebSocketSignalingClient(
                new SignalingClientOptions
                {
                    ServerUri = serverUri,
                    ClientCapabilities = LinuxClientCapabilityProfile.Create(),
                    ClientVersion = protocolClientVersion,
                    AutoReconnect = true,
                    MaxReconnectAttempts = 10,
                    MaxReconnectWindow = TimeSpan.FromSeconds(60),
                    AllowInsecureTransport = serverUri.Scheme == Uri.UriSchemeWs,
                    TrustedDevelopmentRootCertificate = ReadDevelopmentCertificate(),
                },
                provisioning,
                loggerFactory.CreateLogger<WebSocketSignalingClient>(),
                cloud is null ? null : new CloudAttestationProvider(cloud));

            var auditKey = await secretStore.TryGetAsync("audit-integrity-key-v1", cancellationToken);
            if (auditKey is null)
            {
                auditKey = RandomNumberGenerator.GetBytes(32);
                await secretStore.SetAsync("audit-integrity-key-v1", auditKey, cancellationToken);
            }

            var securityAudit = new SqliteSecurityAuditLog(
                database,
                auditKey,
                identity.PublicId.Masked,
                appVersion);
            CryptographicOperations.ZeroMemory(auditKey);

            coordinator = new SessionCoordinator(
                signaling,
                new WebRtcMediaEngine(loggerFactory),
                DenyIncomingPermissionPrompt.Instance,
                new SqliteBlockedDeviceStore(database),
                new SqliteSessionAuditLog(database),
                static () => new UnsupportedLinuxCaptureSource(),
                SessionOptions.Default,
                loggerFactory.CreateLogger<SessionCoordinator>(),
                securityAudit: securityAudit,
                hybridIdentity: hybridIdentity,
                nativeBulkTransportFactory: new ManagedQuicBulkTransportFactory());

            cancellationToken.ThrowIfCancellationRequested();
            return new LinuxAppServices(loggerFactory, identity, hybridIdentity, signaling, coordinator, cloud, cloudHttp);
        }
        catch
        {
            if (coordinator is not null)
                await coordinator.DisposeAsync();
            if (signaling is not null)
                await signaling.DisposeAsync();
            if (cloud is not null)
                await cloud.DisposeAsync();
            cloudHttp?.Dispose();
            hybridIdentity?.Dispose();
            loggerFactory.Dispose();
            throw;
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (_cloud is not null) await _cloud.RegisterInstallationAsync(linked.Token);
        await Signaling.ConnectAsync(Identity, linked.Token);
        _cloud?.Start();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync();
        try { await Coordinator.DisposeAsync(); }
        finally
        {
            try { await Signaling.DisposeAsync(); }
            finally
            {
                try
                {
                    if (_cloud is not null) await _cloud.DisposeAsync();
                }
                finally
                {
                    _cloudHttp?.Dispose();
                    HybridIdentity.Dispose();
                    LoggerFactory.Dispose();
                    _lifetime.Dispose();
                }
            }
        }
    }

    private static Uri ReadSignalingUri()
    {
        var compiled = typeof(LinuxAppServices).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "PeerOnQSignalingUrl")?.Value;
        var configured = !string.IsNullOrWhiteSpace(compiled) ? compiled
            : ReadEnvironmentVariable("PEERONQ_SIGNALING_URL") ?? DefaultSignalingUrl;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeWss && uri.Scheme != Uri.UriSchemeWs))
        {
            throw new InvalidOperationException("PEERONQ_SIGNALING_URL must be an absolute ws:// or wss:// URL.");
        }

        if (uri.Scheme == Uri.UriSchemeWs && !uri.IsLoopback)
            throw new InvalidOperationException("Unencrypted signaling is allowed only on a loopback address.");
        return uri;
    }

    private static byte[]? ReadDevelopmentCertificate()
    {
        var path = ReadEnvironmentVariable("PEERONQ_DEVELOPMENT_ROOT_CERTIFICATE");
        if (path is null) return null;

        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || file.Length is <= 0 or > MaximumCertificateBytes)
            throw new InvalidOperationException("The configured development root certificate is missing or invalid.");
        return File.ReadAllBytes(file.FullName);
    }

    private static string CreateKeyringProfileId(string dataDirectory)
    {
        var normalized = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string? ReadEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private sealed class CloudAttestationProvider(CloudPlatformClient cloud) : ISignalingAttestationProvider
    {
        public async Task<string?> GetSignalingAttestationAsync(CancellationToken cancellationToken = default)
        {
            try { return await cloud.GetSignalingAttestationAsync(cancellationToken); }
            catch (CloudPlatformException exception) when (exception.Permanent)
            {
                throw new PeerOnQ.Domain.Errors.SignalingAuthenticationException(
                    "Cloud device authentication is no longer valid for signaling.", exception);
            }
        }
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
