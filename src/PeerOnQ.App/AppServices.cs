using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Identity;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Infrastructure;
using PeerOnQ.Infrastructure.Diagnostics;
using PeerOnQ.Infrastructure.Configuration;
using PeerOnQ.Infrastructure.Cloud;
using PeerOnQ.Infrastructure.Persistence;
using PeerOnQ.Infrastructure.Security;
using PeerOnQ.Infrastructure.Updates;
using PeerOnQ.Media;
using PeerOnQ.Platform.Windows;
using PeerOnQ.Platform.Windows.Capture;
using PeerOnQ.Platform.Windows.Media;
using PeerOnQ.Platform.Windows.Security;
using PeerOnQ.Platform.Windows.Input;
using PeerOnQ.Transport;
using PeerOnQ.Transport.DataPlane;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Security.Cryptography;

namespace PeerOnQ.App;

/// <summary>
/// Composition root. Everything the UI touches is created once, here, from the real
/// implementations - there is no mock path in the shipping app.
/// </summary>
public sealed class AppServices : IAsyncDisposable, IRegistrationProofProvider
{
    private AppServices(
        PeerOnQPaths paths,
        ILoggerFactory loggerFactory,
        PeerOnQDatabase database,
        DeviceProvisioningService provisioning,
        HybridDeviceIdentityService hybridIdentity,
        DeviceIdentity identity,
        WebSocketSignalingClient signaling,
        SessionCoordinator coordinator,
        ISessionAuditLog sessionAudit,
        AddressBookService addressBook,
        TrustedDeviceService trustedDevices,
        IBlockedDeviceStore blockedDevices,
        UnattendedAccessService unattendedAccess,
        SupportInvitationService supportInvitations,
        ISecurityAuditLog securityAudit,
        PrivacySettingsService privacySettings,
        CrashReportService crashReports,
        DiagnosticBundleService diagnosticBundles,
        NetworkDoctorService networkDoctor,
        ProcessPerformanceSampler performance,
        SignalingEndpointStore signalingEndpoints,
        ResolvedSignalingEndpoint signalingEndpoint,
        bool isLanDevelopmentClient,
        CloudServiceEndpoints? cloudEndpoints,
        CloudPlatformClient? cloudPlatform,
        HttpClient? cloudHttpClient,
        UpdateService? updates,
        HttpClient? updateHttpClient)
    {
        Paths = paths;
        LoggerFactory = loggerFactory;
        Database = database;
        Provisioning = provisioning;
        HybridIdentity = hybridIdentity;
        Identity = identity;
        Signaling = signaling;
        Coordinator = coordinator;
        SessionAudit = sessionAudit;
        AddressBook = addressBook;
        TrustedDevices = trustedDevices;
        BlockedDevices = blockedDevices;
        UnattendedAccess = unattendedAccess;
        SupportInvitations = supportInvitations;
        SecurityAudit = securityAudit;
        PrivacySettings = privacySettings;
        CrashReports = crashReports;
        DiagnosticBundles = diagnosticBundles;
        NetworkDoctor = networkDoctor;
        Performance = performance;
        SignalingEndpoints = signalingEndpoints;
        SignalingEndpoint = signalingEndpoint;
        IsLanDevelopmentClient = isLanDevelopmentClient;
        CloudEndpoints = cloudEndpoints;
        CloudPlatform = cloudPlatform;
        _cloudHttpClient = cloudHttpClient;
        Updates = updates;
        _updateHttpClient = updateHttpClient;
    }

    public PeerOnQPaths Paths { get; }
    public ILoggerFactory LoggerFactory { get; }
    public PeerOnQDatabase Database { get; }
    public DeviceProvisioningService Provisioning { get; }
    public HybridDeviceIdentityService HybridIdentity { get; }
    public DeviceIdentity Identity { get; private set; }
    public WebSocketSignalingClient Signaling { get; }
    public SessionCoordinator Coordinator { get; }
    public ISessionAuditLog SessionAudit { get; }
    public AddressBookService AddressBook { get; }
    public TrustedDeviceService TrustedDevices { get; }
    public IBlockedDeviceStore BlockedDevices { get; }
    public UnattendedAccessService UnattendedAccess { get; }
    public SupportInvitationService SupportInvitations { get; }
    public ISecurityAuditLog SecurityAudit { get; }
    public PrivacySettingsService PrivacySettings { get; }
    public CrashReportService CrashReports { get; }
    public DiagnosticBundleService DiagnosticBundles { get; }
    public NetworkDoctorService NetworkDoctor { get; }
    public ProcessPerformanceSampler Performance { get; }
    public SignalingEndpointStore SignalingEndpoints { get; }
    public ResolvedSignalingEndpoint SignalingEndpoint { get; }
    public bool IsLanDevelopmentClient { get; }
    public CloudServiceEndpoints? CloudEndpoints { get; }
    public CloudPlatformClient? CloudPlatform { get; }
    public bool RoutingIdentityReady => CloudEndpoints is null || Identity.PublicIdServerAssigned;
    public UpdateService? Updates { get; }
    private readonly HttpClient? _updateHttpClient;
    private readonly HttpClient? _cloudHttpClient;

    /// <summary>Set by the permission dialog host before any session can arrive.</summary>
    public static IPermissionPrompt? PermissionPrompt { get; set; }

    public const string LocalDevelopmentSignalingUrl = "wss://signal.dev.localhost:5443/ws";
    public const string DataDirectoryEnvironmentVariableName = "PEERONQ_DATA_DIR";

    /// <summary>
    /// Overrides where this instance keeps its identity, database and logs.
    ///
    /// Two instances on one machine would otherwise share %LOCALAPPDATA%\PeerOnQ, end up with
    /// the same PeerOnQ ID, and the second registration would simply displace the first. Give
    /// each instance its own folder and they behave like two separate devices, which is what
    /// makes a viewer/sharer test possible without a second computer.
    /// </summary>
    public static string? DataDirectoryOverride =>
        ReadEnvironmentVariable(DataDirectoryEnvironmentVariableName);

    public static async Task<AppServices> CreateAsync(IPermissionPrompt prompt)
    {
        var paths = new PeerOnQPaths(DataDirectoryOverride).EnsureCreated();
        var appAssembly = typeof(AppServices).Assembly;
        var appVersion = appAssembly.GetName().Version?.ToString() ?? "unknown";
        var assemblyMetadata = appAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        var deploymentEnvironment =
            assemblyMetadata.GetValueOrDefault("PeerOnQDeploymentEnvironment") ?? "Development";
        var lanDevelopmentClient = bool.TryParse(
            assemblyMetadata.GetValueOrDefault("PeerOnQLanDevelopment"),
            out var configuredLanDevelopment) && configuredLanDevelopment;
        if (lanDevelopmentClient
            && !string.Equals(deploymentEnvironment, "Development", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("LAN development mode is allowed only in Development builds.");
        var loggerFactory = PeerOnQLogging.CreateLoggerFactory(
            paths.LogDirectory,
            appVersion,
            deploymentEnvironment,
            assemblyMetadata.GetValueOrDefault("PeerOnQRegion") ?? "local");
#if DEBUG
        const bool AllowDevelopmentEndpointOverrides = true;
#else
        const bool AllowDevelopmentEndpointOverrides = false;
#endif
        var cloudEndpoints = lanDevelopmentClient
            ? null
            : CloudEndpointConfiguration.TryCreate(appAssembly, AllowDevelopmentEndpointOverrides);
        var configuredDefault = assemblyMetadata.GetValueOrDefault("PeerOnQSignalingUrl");
        var endpointStore = new SignalingEndpointStore(
            paths,
            string.IsNullOrWhiteSpace(configuredDefault)
                ? LocalDevelopmentSignalingUrl
                : configuredDefault,
            AllowDevelopmentEndpointOverrides);
        var endpoint = endpointStore.Resolve();
        var developmentRootPath = Path.Combine(
            AppContext.BaseDirectory,
            "PeerOnQ-Local-Development-Root.cer");
        var trustedDevelopmentRoot = lanDevelopmentClient && File.Exists(developmentRootPath)
            ? File.ReadAllBytes(developmentRootPath)
            : null;

        var database = new PeerOnQDatabase(paths.DatabaseFile);
        database.Migrate();

        var secretStore = new DpapiSecretStore(paths.SecretsDirectory);
        var provisioning = new DeviceProvisioningService(
            new SqliteDeviceIdentityRepository(database),
            secretStore,
            loggerFactory.CreateLogger<DeviceProvisioningService>());

        var identity = await provisioning.GetOrCreateAsync(Environment.MachineName);
        var hybridIdentity = new HybridDeviceIdentityService(secretStore, identity);

        AppServices? services = null;

        var signaling = new WebSocketSignalingClient(
            new SignalingClientOptions
            {
                ServerUri = endpoint.Uri,
                ClientCapabilities = WindowsClientCapabilityProfile.Create(),
                ClientVersion = typeof(AppServices).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(AppServices).Assembly.GetName().Version?.ToString()
                    ?? "unknown",
                AutoReconnect = true,
                HeartbeatInterval = lanDevelopmentClient
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromSeconds(15),
                // Match the server deadline so a temporarily busy UI or LAN does not end an
                // otherwise healthy session. Regular pings still detect an actual disconnect.
                HeartbeatTimeout = TimeSpan.FromSeconds(45),
                ReconnectDelay = lanDevelopmentClient
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(2),
                MaxReconnectDelay = lanDevelopmentClient
                    ? TimeSpan.FromSeconds(4)
                    : TimeSpan.FromSeconds(8),
                MaxReconnectWindow = TimeSpan.FromSeconds(30),
                MaxReconnectAttempts = 10,
                TrustedDevelopmentRootCertificate = trustedDevelopmentRoot,
            },
            new LazyProofProvider(() => services!),
            loggerFactory.CreateLogger<WebSocketSignalingClient>(),
            new LazySignalingAttestationProvider(() => services));

        var auditKey = await secretStore.TryGetAsync("audit-integrity-key-v1");
        if (auditKey is null)
        {
            auditKey = RandomNumberGenerator.GetBytes(32);
            await secretStore.SetAsync("audit-integrity-key-v1", auditKey);
        }

        var securityAudit = new SqliteSecurityAuditLog(
            database,
            auditKey,
            identity.PublicId.Masked,
            appVersion);
        var privacySettings = new PrivacySettingsService(secretStore, securityAudit);
        var privacy = await privacySettings.GetAsync();
        await securityAudit.ApplyRetentionAsync(TimeSpan.FromDays(privacy.AuditRetentionDays));
        var crashReports = new CrashReportService(paths.CrashReportDirectory, privacySettings, securityAudit);
        var diagnosticBundles = new DiagnosticBundleService(paths.DiagnosticsDirectory, paths.LogDirectory);
        var networkDoctor = new NetworkDoctorService(
            new NetworkDoctorOptions
            {
                SignalingUri = endpoint.Uri,
                ApiBaseUri = cloudEndpoints?.ApiBaseUri,
                Region = cloudEndpoints?.Region ?? "local-development",
            },
            () => signaling.State,
            () => signaling.EstimatedServerClockOffset);
        var performance = new ProcessPerformanceSampler();
        var updateTelemetry = new SqliteClientUpdateTelemetryOutbox(database);

        HttpClient? updateHttpClient = null;
        UpdateService? updates = null;
        var allowDevelopmentUpdateTrust = string.Equals(
            deploymentEnvironment,
            "Development",
            StringComparison.OrdinalIgnoreCase);
        var updateOptions = UpdateConfiguration.TryCreate(
            appAssembly,
            paths.UpdateDirectory,
            identity.InternalId.ToString("D"),
            allowDevelopmentUpdateTrust);
        if (updateOptions is not null)
        {
            updateHttpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
            updates = new UpdateService(
                updateHttpClient,
                updateOptions,
                new WindowsAuthenticodeVerifier(),
                securityAudit,
                telemetry: updateTelemetry);
            await updates.ReportConfirmedInstallationAsync();
        }
        var collaborationStore = new ProtectedCollaborationProfileStore(secretStore);
        var trustedDevices = new TrustedDeviceService(collaborationStore, securityAudit);
        var unattendedAccess = new UnattendedAccessService(
            collaborationStore,
            trustedDevices,
            securityAudit);
        var supportInvitations = new SupportInvitationService(
            new ProtectedSupportInvitationStore(secretStore),
            securityAudit);
        var addressBook = new AddressBookService(collaborationStore, signaling);
        var inputController = new WindowsInputController(
            loggerFactory.CreateLogger<WindowsInputController>());

        var sessionAudit = new SqliteSessionAuditLog(database);
        var blockedDevices = new SqliteBlockedDeviceStore(database);
        var clientTelemetry = new SqliteClientTelemetryOutbox(database);
        var coordinator = new SessionCoordinator(
            signaling,
            new WebRtcMediaEngine(
                loggerFactory,
                hardwareH264Available: () => HardwareEncoderProbe.Supports(HardwareVideoCodec.H264)),
            prompt,
            blockedDevices,
            sessionAudit,
            () => new WindowsGraphicsCaptureSource(loggerFactory.CreateLogger<WindowsGraphicsCaptureSource>()),
            SessionOptions.Default,
            loggerFactory.CreateLogger<SessionCoordinator>(),
            inputSafetyController: inputController,
            remoteInputSink: inputController,
            unattendedAccess: unattendedAccess,
            clipboardFactory: () => new WindowsClipboardAdapter(),
            securityAudit: securityAudit,
            malwareScanner: new WindowsAmsiMalwareScanner(),
            clientSessionEvents: clientTelemetry,
            hybridIdentity: hybridIdentity,
            supportInvitations: supportInvitations,
            nativeBulkTransportFactory: new ManagedQuicBulkTransportFactory());

        HttpClient? cloudHttpClient = null;
        CloudPlatformClient? cloudPlatform = null;
        if (cloudEndpoints is not null)
        {
            cloudHttpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
            var channel = assemblyMetadata.GetValueOrDefault("PeerOnQReleaseChannel")?.ToLowerInvariant() switch
            {
                "stable" => PeerOnQ.Shared.Contracts.V1.InstallChannelV1.Stable,
                "beta" => PeerOnQ.Shared.Contracts.V1.InstallChannelV1.Beta,
                "enterprise" => PeerOnQ.Shared.Contracts.V1.InstallChannelV1.Enterprise,
                _ => PeerOnQ.Shared.Contracts.V1.InstallChannelV1.Development,
            };
            cloudPlatform = new CloudPlatformClient(
                cloudHttpClient,
                new CloudPlatformClientOptions
                {
                    Endpoints = cloudEndpoints,
                    InstallationId = identity.InternalId,
                    AppVersion = appVersion,
                    OsVersion = Environment.OSVersion.VersionString,
                    Architecture = CloudPlatformClient.CurrentArchitecture,
                    InstallChannel = channel,
                },
                identity,
                provisioning,
                provisioning.AssignServerPublicIdAsync,
                clientTelemetry,
                loggerFactory.CreateLogger<CloudPlatformClient>(),
                updateTelemetry);

            using var enrollmentDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await cloudPlatform.RegisterInstallationAsync(enrollmentDeadline.Token);
                identity = cloudPlatform.Identity;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || enrollmentDeadline.IsCancellationRequested)
            {
                loggerFactory.CreateLogger<AppServices>().LogWarning(
                    "Initial cloud enrollment did not complete; public routing remains disabled while bounded retry continues: {ErrorType}",
                    exception.GetType().Name);
            }
        }

        services = new AppServices(
            paths,
            loggerFactory,
            database,
            provisioning,
            hybridIdentity,
            identity,
            signaling,
            coordinator,
            sessionAudit,
            addressBook,
            trustedDevices,
            blockedDevices,
            unattendedAccess,
            supportInvitations,
            securityAudit,
            privacySettings,
            crashReports,
            diagnosticBundles,
            networkDoctor,
            performance,
            endpointStore,
            endpoint,
            lanDevelopmentClient,
            cloudEndpoints,
            cloudPlatform,
            cloudHttpClient,
            updates,
            updateHttpClient);
        if (cloudPlatform is not null)
        {
            cloudPlatform.EnrollmentCompleted += (_, enrolledIdentity) =>
                services.Identity = enrolledIdentity;
        }
        cloudPlatform?.Start();
        return services;
    }

    public async Task RenameAsync(string displayName) =>
        Identity = await Provisioning.RenameAsync(Identity, displayName);

    public Task<string> ComputeRegistrationProofAsync(string challenge, CancellationToken cancellationToken = default) =>
        Provisioning.ComputeRegistrationProofAsync(challenge, cancellationToken);

    private static string? ReadEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        await Signaling.DisposeAsync();
        NetworkDoctor.Dispose();
        HybridIdentity.Dispose();
        if (CloudPlatform is not null) await CloudPlatform.DisposeAsync();
        _cloudHttpClient?.Dispose();
        _updateHttpClient?.Dispose();
        LoggerFactory.Dispose();
    }

    private sealed class LazyProofProvider(Func<AppServices> resolve) : IRegistrationProofProvider
    {
        public Task<string> ComputeRegistrationProofAsync(string challenge, CancellationToken cancellationToken = default) =>
            resolve().ComputeRegistrationProofAsync(challenge, cancellationToken);
    }

    private sealed class LazySignalingAttestationProvider(Func<AppServices?> resolve) : ISignalingAttestationProvider
    {
        public async Task<string?> GetSignalingAttestationAsync(CancellationToken cancellationToken = default)
        {
            if (resolve()?.CloudPlatform is not { } cloud) return null;

            try
            {
                return await cloud.GetSignalingAttestationAsync(cancellationToken);
            }
            catch (CloudPlatformException exception) when (exception.Permanent)
            {
                throw new PeerOnQ.Domain.Errors.SignalingAuthenticationException(
                    "Cloud device authentication is no longer valid for signaling.",
                    exception);
            }
        }
    }
}
