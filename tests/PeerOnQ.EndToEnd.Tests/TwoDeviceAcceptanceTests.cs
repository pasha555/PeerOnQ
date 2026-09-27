using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Identity;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Infrastructure;
using PeerOnQ.Infrastructure.Persistence;
using PeerOnQ.Infrastructure.Security;
using PeerOnQ.Media;
using PeerOnQ.Platform.Windows;
using PeerOnQ.Platform.Windows.Capture;
using PeerOnQ.Signaling.Server;
using PeerOnQ.Transport;
using PeerOnQ.Transport.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>
/// One complete PeerOnQ installation: its own data directory, SQLite database, protected
/// secret store, signaling client, media engine and session coordinator. Two of these plus a
/// signaling server reproduce the two-computer acceptance flow inside one machine.
/// </summary>
public sealed class PeerOnQDevice : IAsyncDisposable, IRegistrationProofProvider
{
    private readonly DeviceProvisioningService _provisioning;
    private readonly PeerOnQPaths _paths;
    private readonly HybridDeviceIdentityService _hybridIdentity;
    private readonly TaskCompletionSource<SessionCollaborationContext> _collaborationAvailable =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PeerOnQDevice(
        string displayName,
        PeerOnQPaths paths,
        DeviceProvisioningService provisioning,
        HybridDeviceIdentityService hybridIdentity)
    {
        DisplayName = displayName;
        _paths = paths;
        _provisioning = provisioning;
        _hybridIdentity = hybridIdentity;
    }

    public string DisplayName { get; }
    public DeviceIdentity Identity { get; private set; } = null!;
    public WebSocketSignalingClient Signaling { get; private set; } = null!;
    public SessionCoordinator Coordinator { get; private set; } = null!;
    public RecordingPermissionPrompt Prompt { get; } = new();
    public Task<SessionCollaborationContext> CollaborationAvailable => _collaborationAvailable.Task;
    public string DataRoot => _paths.Root;

    public static async Task<PeerOnQDevice> CreateAsync(string displayName, Uri signalingUri, bool useRealCapture)
    {
        var paths = new PeerOnQPaths(Path.Combine(
            Path.GetTempPath(), "peeronq-e2e", Guid.NewGuid().ToString("N"))).EnsureCreated();

        var database = new PeerOnQDatabase(paths.DatabaseFile);
        database.Migrate();

        var secretStore = new DpapiSecretStore(paths.SecretsDirectory);
        var provisioning = new DeviceProvisioningService(
            new SqliteDeviceIdentityRepository(database),
            secretStore,
            NullLogger<DeviceProvisioningService>.Instance);

        var identity = await provisioning.GetOrCreateAsync(displayName);
        var hybridIdentity = new HybridDeviceIdentityService(secretStore, identity);
        var device = new PeerOnQDevice(displayName, paths, provisioning, hybridIdentity)
        {
            Identity = identity,
        };

        device.Signaling = new WebSocketSignalingClient(
            new SignalingClientOptions
            {
                ServerUri = signalingUri,
                ClientCapabilities = WindowsClientCapabilityProfile.Create(),
                AutoReconnect = false,
                HeartbeatInterval = TimeSpan.FromSeconds(5),
            },
            device,
            NullLogger<WebSocketSignalingClient>.Instance);

        device.Coordinator = new SessionCoordinator(
            device.Signaling,
            new WebRtcMediaEngine(),
            device.Prompt,
            new SqliteBlockedDeviceStore(database),
            new SqliteSessionAuditLog(database),
            () => useRealCapture
                ? new WindowsGraphicsCaptureSource()
                : throw new InvalidOperationException("capture not enabled for this device"),
            PeerOnQ.Application.Sessions.SessionOptions.Default with
            {
                PermissionTimeout = TimeSpan.FromSeconds(20),
                NegotiationTimeout = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(30),
            },
            hybridIdentity: hybridIdentity);
        device.Coordinator.CollaborationAvailable += (_, context) =>
            device._collaborationAvailable.TrySetResult(context);

        return device;
    }

    public Task ConnectAsync() => Signaling.ConnectAsync(Identity);

    public Task<string> ComputeRegistrationProofAsync(string challenge, CancellationToken cancellationToken = default) =>
        _provisioning.ComputeRegistrationProofAsync(challenge, cancellationToken);

    /// <summary>Restarts the app: new objects, same data directory.</summary>
    public static async Task<DeviceIdentity> ReloadIdentityAsync(string dataRoot)
    {
        var paths = new PeerOnQPaths(dataRoot);
        var database = new PeerOnQDatabase(paths.DatabaseFile);
        database.Migrate();

        return await new DeviceProvisioningService(
                new SqliteDeviceIdentityRepository(database),
                new DpapiSecretStore(paths.SecretsDirectory),
                NullLogger<DeviceProvisioningService>.Instance)
            .GetOrCreateAsync("ignored-on-second-run");
    }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        await Signaling.DisposeAsync();
        _hybridIdentity.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}

public sealed class RecordingPermissionPrompt : IPermissionPrompt
{
    private readonly TaskCompletionSource<PermissionRequest> _shown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PermissionDecision Answer { get; set; } = PermissionDecision.Accept;
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public int Calls { get; private set; }
    public Task<PermissionRequest> Shown => _shown.Task;
    public PermissionRequest? LastRequest { get; private set; }

    public async Task<PermissionDecision> AskAsync(PermissionRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;
        _shown.TrySetResult(request);

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Answer;
    }
}

[Collection(WindowsGraphicsCaptureCollection.Name)]
public class TwoDeviceAcceptanceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private WebApplication? _server;
    private Uri _signalingUri = null!;

    public async Task InitializeAsync()
    {
        _server = SignalingApp.Create(["--environment", "Testing"], options =>
        {
            options.PermissionTimeout = TimeSpan.FromSeconds(20);
            options.NegotiationTimeout = TimeSpan.FromSeconds(40);
            options.SweepInterval = TimeSpan.FromMilliseconds(250);

            // Tests must not share pins or touch the working directory.
            options.PinStorePath = null;
            options.Attestation.Required = false;
            options.Attestation.AllowDevelopmentTofuFallback = true;
        });

        _server.Urls.Clear();
        _server.Urls.Add("http://127.0.0.1:0");
        await _server.StartAsync();

        var address = _server.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        _signalingUri = new Uri($"ws://127.0.0.1:{new Uri(address).Port}{SignalingApp.WebSocketPath}");
        output.WriteLine($"Signaling server: {_signalingUri}");
    }

    public async Task DisposeAsync()
    {
        if (_server is null) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _server.StopAsync(cts.Token);
        await _server.DisposeAsync();
    }

    [Fact]
    public void Windows_client_advertises_the_managed_hybrid_security_provider()
    {
        Assert.True(PostQuantumCryptography.IsSupported);
        Assert.Contains(
            EndpointCapabilityNames.HybridPostQuantumSecure,
            WindowsClientCapabilityProfile.Create().Capabilities);
    }

    /// <summary>
    /// The attended remote-view acceptance flow, executed against real components: two provisioned
    /// identities, a real signaling server, an explicit permission prompt, real
    /// Windows.Graphics.Capture on the sharer, and a real WebRTC video connection.
    /// </summary>
    [Fact]
    public async Task Viewer_sees_the_sharer_screen_after_an_explicit_accept()
    {
        await using var viewer = await PeerOnQDevice.CreateAsync("Viewer PC", _signalingUri, useRealCapture: false);
        await using var sharer = await PeerOnQDevice.CreateAsync("Sharer PC", _signalingUri, useRealCapture: true);

        // 3. Both devices have unique IDs.
        output.WriteLine($"viewer id = {viewer.Identity.PublicId.Masked}");
        output.WriteLine($"sharer id = {sharer.Identity.PublicId.Masked}");
        Assert.NotEqual(viewer.Identity.PublicId, sharer.Identity.PublicId);

        await viewer.ConnectAsync();
        await sharer.ConnectAsync();
        Assert.Equal(SignalingConnectionState.Registered, viewer.Signaling.State);
        Assert.Equal(SignalingConnectionState.Registered, sharer.Signaling.State);

        // The sharer must pick a display; nothing is captured implicitly.
        sharer.Coordinator.PreferredCaptureTarget = DisplayEnumerator.ListCaptureTargets()[0];
        sharer.Prompt.Answer = PermissionDecision.Accept;

        var viewerConnected = new TaskCompletionSource<ActiveSessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Coordinator.SessionChanged += (_, info) =>
        {
            if (info.State == SessionState.ConnectedViewOnly) viewerConnected.TrySetResult(info);
        };

        var frames = 0;
        var firstFrame = new TaskCompletionSource<RemoteVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Coordinator.RemoteFrameReceived += (_, frame) =>
        {
            Interlocked.Increment(ref frames);
            firstFrame.TrySetResult(frame);
        };

        // 4. Device A enters Device B's ID.
        var sessionId = await viewer.Coordinator.RequestViewOnlySessionAsync(sharer.Identity.PublicId);

        // 5. Device B receives a visible permission request with a masked ID.
        var prompt = await sharer.Prompt.Shown.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(viewer.Identity.PublicId, prompt.RequesterId);
        Assert.Equal("Viewer PC", prompt.RequesterDisplayName);
        Assert.Equal(SessionMode.ViewOnly, prompt.RequestedMode);
        Assert.Equal(viewer.Identity.PublicId.MaskedDisplay, prompt.MaskedRequesterId);
        Assert.DoesNotContain(viewer.Identity.PublicId.Value, prompt.MaskedRequesterId);
        output.WriteLine($"permission dialog: {prompt.RequesterDisplayName} ({prompt.MaskedRequesterId}) " +
                         $"mode={prompt.RequestedMode} expires={prompt.ExpiresAt:O}");

        // 6-7. After the accept, the viewer reaches ConnectedViewOnly and real frames arrive.
        var info = await viewerConnected.Task.WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"viewer state = {info.State}, peer = {info.PeerDisplayName} ({info.PeerMaskedId})");
        Assert.Equal(SessionMode.ViewOnly, info.Mode);
        Assert.Equal(SessionRole.Viewer, info.Role);

        var frame = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"first decoded frame: {frame.Width}x{frame.Height} {frame.Format}, {frame.Pixels.Length} bytes");
        Assert.True(frame.Width > 0 && frame.Height > 0);
        Assert.Equal(frame.ExpectedLength, frame.Pixels.Length);
        // The headless acceptance harness acts as the UI after validating the frame buffer.
        viewer.Coordinator.ReportFrameRendered(sessionId);

        await Task.Delay(2000);

        var sharerStats = sharer.Coordinator.StatisticsOf(sessionId);
        var viewerStats = viewer.Coordinator.StatisticsOf(sessionId);
        output.WriteLine($"sharer: captured={sharerStats.FramesCaptured} encoded={sharerStats.FramesEncoded} " +
                         $"dropped={sharerStats.FramesDropped} sent={sharerStats.BytesSent}B " +
                         $"fps={sharerStats.CurrentFps:0.0} kbps={sharerStats.CurrentBitrateKbps:0}");
        output.WriteLine($"viewer: rendered={viewerStats.FramesRendered} received={viewerStats.BytesReceived}B " +
                         $"fps={viewerStats.CurrentFps:0.0} {viewerStats.Width}x{viewerStats.Height} " +
                         $"path={viewerStats.ConnectionPath}");

        // 10. Real metrics, not placeholders.
        //
        // Windows.Graphics.Capture is change driven: on an idle desktop it emits a frame only
        // when something on screen actually moves, so the frame count here reflects real
        // desktop activity rather than a fixed rate. Continuous streaming at the target rate
        // is covered by the media loopback test, which drives a constantly changing source.
        Assert.True(sharerStats.FramesCaptured > 0, "no frames came out of Windows.Graphics.Capture");
        Assert.True(sharerStats.FramesEncoded > 0, "no frame was VP8 encoded");
        Assert.True(sharerStats.BytesSent > 0, "no media bytes left the sharer");
        Assert.True(viewerStats.FramesRendered > 0, "the viewer painted nothing");
        Assert.True(frames > 0, "the viewer received no screen frame");
        Assert.Equal(sharerStats.FramesEncoded > 0, viewerStats.FramesRendered > 0);
        Assert.Contains(
            viewerStats.ConnectionPath,
            new[] { ConnectionPath.DirectLan, ConnectionPath.DirectInternet });

        // 8. This accepted profile remains view-only; Phase 4 profiles do not expand it.
        Assert.Equal(SessionMode.ViewOnly, viewer.Coordinator.ActiveSessions.Single().Mode);
        Assert.Equal(SessionPermission.ViewScreen, viewer.Coordinator.ActiveSessions.Single().Permissions);

        // 10-11. Either side can end; all media and capture stop afterwards.
        await sharer.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedBySharer);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (viewer.Coordinator.ActiveSessions.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Empty(sharer.Coordinator.ActiveSessions);
        Assert.Empty(viewer.Coordinator.ActiveSessions);
        output.WriteLine("session ended and both sides released their resources");
    }

    [Fact]
    public async Task File_transfer_mode_moves_an_exact_file_after_remote_acceptance_without_screen_capture()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "peeronq-e2e-file", Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(testRoot, "source");
        var destinationRoot = Path.Combine(testRoot, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);

        try
        {
            await using var sender = await PeerOnQDevice.CreateAsync("Sender PC", _signalingUri, useRealCapture: false);
            await using var receiver = await PeerOnQDevice.CreateAsync("Receiver PC", _signalingUri, useRealCapture: false);
            await sender.ConnectAsync();
            await receiver.ConnectAsync();
            receiver.Prompt.Answer = PermissionDecision.Accept;

            var source = Path.Combine(sourceRoot, "peeronq-file-transfer.bin");
            var expected = Enumerable.Range(0, 256 * 1024).Select(index => (byte)(index % 251)).ToArray();
            await File.WriteAllBytesAsync(source, expected);

            var sessionId = await sender.Coordinator.RequestSessionAsync(
                receiver.Identity.PublicId,
                SessionMode.FileTransferOnly,
                SessionPermission.FileTransfer,
                SessionAccessKind.Attended);

            var prompt = await receiver.Prompt.Shown.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(SessionMode.FileTransferOnly, prompt.RequestedMode);
            Assert.Equal(SessionPermission.FileTransfer, prompt.RequestedPermissions);

            var senderContext = await sender.CollaborationAvailable.WaitAsync(TimeSpan.FromSeconds(30));
            var receiverContext = await receiver.CollaborationAvailable.WaitAsync(TimeSpan.FromSeconds(30));
            var readyDeadline = DateTime.UtcNow.AddSeconds(30);
            while ((!senderContext.IsReady || !receiverContext.IsReady) && DateTime.UtcNow < readyDeadline)
                await Task.Delay(50);
            Assert.True(senderContext.IsReady && receiverContext.IsReady, "file-transfer data channel did not open");

            var senderTransfers = Assert.IsType<FileTransferService>(senderContext.FileTransfers);
            var receiverTransfers = Assert.IsType<FileTransferService>(receiverContext.FileTransfers);
            Assert.Null(senderContext.RemoteInput);
            Assert.Null(receiverContext.RemoteInput);

            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            receiverTransfers.IncomingOffer += (_, offer) =>
            {
                _ = AcceptAsync(offer);
                return;

                async Task AcceptAsync(TransferOffer incoming)
                {
                    try
                    {
                        await receiverTransfers.AcceptAsync(
                            incoming.TransferId,
                            destinationRoot,
                            TransferCollisionPolicy.Rename);
                    }
                    catch (Exception ex)
                    {
                        completed.TrySetException(ex);
                    }
                }
            };
            receiverTransfers.TransferChanged += (_, transfer) =>
            {
                if (transfer.Status == TransferStatus.Completed) completed.TrySetResult();
                if (transfer.Status == TransferStatus.Failed)
                    completed.TrySetException(new InvalidOperationException(transfer.FailureCode ?? "transfer_failed"));
            };

            await senderTransfers.OfferAsync([source]);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(60));

            var received = await File.ReadAllBytesAsync(Path.Combine(destinationRoot, Path.GetFileName(source)));
            Assert.Equal(expected, received);
            Assert.All(sender.Coordinator.ActiveSessions, session =>
                Assert.Equal(SessionPermission.FileTransfer, session.Permissions));
            Assert.All(receiver.Coordinator.ActiveSessions, session =>
                Assert.Equal(SessionPermission.FileTransfer, session.Permissions));

            await sender.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedByViewer);
            output.WriteLine($"file transfer completed with byte-for-byte equality: {received.Length} bytes");
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Graceful_application_shutdown_notifies_the_peer_and_closes_both_sessions()
    {
        await using var sender = await PeerOnQDevice.CreateAsync("Sender PC", _signalingUri, useRealCapture: false);
        await using var receiver = await PeerOnQDevice.CreateAsync("Receiver PC", _signalingUri, useRealCapture: false);
        await sender.ConnectAsync();
        await receiver.ConnectAsync();
        receiver.Prompt.Answer = PermissionDecision.Accept;

        var receiverClosed = new TaskCompletionSource<SessionEndReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Coordinator.SessionClosed += (_, closed) => receiverClosed.TrySetResult(closed.Reason);

        await sender.Coordinator.RequestSessionAsync(
            receiver.Identity.PublicId,
            SessionMode.FileTransferOnly,
            SessionPermission.FileTransfer,
            SessionAccessKind.Attended);

        var senderContext = await sender.CollaborationAvailable.WaitAsync(TimeSpan.FromSeconds(30));
        var receiverContext = await receiver.CollaborationAvailable.WaitAsync(TimeSpan.FromSeconds(30));
        var readyDeadline = DateTime.UtcNow.AddSeconds(30);
        while ((!senderContext.IsReady || !receiverContext.IsReady) && DateTime.UtcNow < readyDeadline)
            await Task.Delay(50);
        Assert.True(senderContext.IsReady && receiverContext.IsReady, "data channel did not open");

        await sender.Coordinator.DisposeAsync();

        Assert.Equal(
            SessionEndReason.ApplicationShutdown,
            await receiverClosed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Empty(sender.Coordinator.ActiveSessions);
        Assert.Empty(receiver.Coordinator.ActiveSessions);
    }

    [Fact]
    public async Task A_declined_request_never_starts_capture()
    {
        await using var viewer = await PeerOnQDevice.CreateAsync("Viewer PC", _signalingUri, useRealCapture: false);
        await using var sharer = await PeerOnQDevice.CreateAsync("Sharer PC", _signalingUri, useRealCapture: true);

        await viewer.ConnectAsync();
        await sharer.ConnectAsync();

        sharer.Coordinator.PreferredCaptureTarget = DisplayEnumerator.ListCaptureTargets()[0];
        sharer.Prompt.Answer = PermissionDecision.Decline;

        var closed = new TaskCompletionSource<SessionEndReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Coordinator.SessionClosed += (_, e) => closed.TrySetResult(e.Reason);

        await viewer.Coordinator.RequestViewOnlySessionAsync(sharer.Identity.PublicId);
        await sharer.Prompt.Shown.WaitAsync(TimeSpan.FromSeconds(15));

        var reason = await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(SessionEndReason.PermissionDeclined, reason);
        Assert.Empty(viewer.Coordinator.ActiveSessions);
        Assert.Empty(sharer.Coordinator.ActiveSessions);
    }

    [Fact]
    public async Task No_answer_within_the_window_declines_and_never_shares()
    {
        await using var viewer = await PeerOnQDevice.CreateAsync("Viewer PC", _signalingUri, useRealCapture: false);
        await using var sharer = await PeerOnQDevice.CreateAsync("Sharer PC", _signalingUri, useRealCapture: true);

        await viewer.ConnectAsync();
        await sharer.ConnectAsync();

        sharer.Coordinator.PreferredCaptureTarget = DisplayEnumerator.ListCaptureTargets()[0];
        sharer.Prompt.Answer = PermissionDecision.Accept;
        sharer.Prompt.Delay = TimeSpan.FromMinutes(5); // the user walks away

        var closed = new TaskCompletionSource<SessionEndReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Coordinator.SessionClosed += (_, e) => closed.TrySetResult(e.Reason);

        await viewer.Coordinator.RequestViewOnlySessionAsync(sharer.Identity.PublicId);

        var reason = await closed.Task.WaitAsync(TimeSpan.FromSeconds(40));

        Assert.Equal(SessionEndReason.PermissionTimeout, reason);
        Assert.Empty(viewer.Coordinator.ActiveSessions);
    }

    [Fact]
    public async Task An_offline_or_unknown_id_produces_a_real_error()
    {
        await using var viewer = await PeerOnQDevice.CreateAsync("Viewer PC", _signalingUri, useRealCapture: false);
        await viewer.ConnectAsync();

        var error = new TaskCompletionSource<SignalingErrorNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Signaling.ErrorReceived += (_, e) => error.TrySetResult(e);

        await viewer.Coordinator.RequestViewOnlySessionAsync(PeerOnQId.Parse("LNK-999-888-777-666"));

        var received = await error.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("target_offline", received.Code);
        output.WriteLine($"error surfaced to the UI: {received.Code} - {received.Message}");
    }

    [Fact]
    public async Task Restarting_the_application_keeps_the_same_device_id()
    {
        await using var device = await PeerOnQDevice.CreateAsync("Sharer PC", _signalingUri, useRealCapture: false);
        var original = device.Identity;

        var afterRestart = await PeerOnQDevice.ReloadIdentityAsync(device.DataRoot);

        Assert.Equal(original.PublicId, afterRestart.PublicId);
        Assert.Equal(original.InternalId, afterRestart.InternalId);
        Assert.Equal("Sharer PC", afterRestart.DisplayName);
        output.WriteLine($"device id survived a restart: {afterRestart.PublicId.Masked}");
    }

    [Fact]
    public async Task No_secret_material_appears_in_the_database_or_the_logs()
    {
        await using var device = await PeerOnQDevice.CreateAsync("Sharer PC", _signalingUri, useRealCapture: false);
        await device.ConnectAsync();

        var paths = new PeerOnQPaths(device.DataRoot);
        var secrets = new DpapiSecretStore(paths.SecretsDirectory);
        var deviceSecret = await secrets.TryGetAsync(DeviceProvisioningService.DeviceSecretName);
        var signingKey = await secrets.TryGetAsync(DeviceProvisioningService.SigningKeyName);

        Assert.NotNull(deviceSecret);
        Assert.NotNull(signingKey);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var databaseBytes = await File.ReadAllBytesAsync(paths.DatabaseFile);

        Assert.False(ContainsSequence(databaseBytes, deviceSecret!), "device secret found in SQLite");
        Assert.False(ContainsSequence(databaseBytes, signingKey!), "signing key found in SQLite");

        var text = System.Text.Encoding.UTF8.GetString(databaseBytes);
        Assert.DoesNotContain(Convert.ToBase64String(deviceSecret!), text);
        output.WriteLine("database contains no device secret and no private key");
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }

            if (match) return true;
        }

        return false;
    }
}
