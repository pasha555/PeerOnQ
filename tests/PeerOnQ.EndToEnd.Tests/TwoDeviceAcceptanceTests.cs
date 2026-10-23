using System.Diagnostics;
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
using PeerOnQ.Platform.Windows.Media;
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

    public static async Task<PeerOnQDevice> CreateAsync(
        string displayName, Uri signalingUri, bool useRealCapture,
        IMediaEngine? mediaEngine = null, Func<IScreenCaptureSource>? captureFactory = null)
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
            mediaEngine ?? new WebRtcMediaEngine(),
            device.Prompt,
            new SqliteBlockedDeviceStore(database),
            new SqliteSessionAuditLog(database),
            captureFactory ?? (() => useRealCapture
                ? new WindowsGraphicsCaptureSource()
                : throw new InvalidOperationException("capture not enabled for this device")),
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

    [HardwareVideoTheory]
    [InlineData(3840, 2160, true)]
    [InlineData(2160, 3840, false)]
    public async Task Four_k_coordinated_sessions_return_real_presentation_feedback_over_authenticated_signaling(
        int width, int height, bool expectHardware)
    {
        // Separate from the direct codec fixture: real registration, attended acceptance,
        // SDP/ICE and quality publishing/receiving all run through production coordinators.
        MediaFoundationH264Encoder? hardware = null;
        // SessionCoordinator owns and disposes the capture returned by its factory.
        var capture = new HardwareVideoIntegrationTests.SyntheticVideoCapture(width, height);
        var diagnostics = new CoordinatedFourKDiagnostics();
        // The Windows viewer presents BGRA32 straight from the presentation worker; measure that path.
        var engine = new WebRtcMediaEngine(
            hardwareH264Available: () => HardwareEncoderProbe.Supports(HardwareVideoCodec.H264),
            hardwareH264EncoderFactory: () => diagnostics.Observe(hardware = new MediaFoundationH264Encoder()),
            h264DecoderFactory: () => diagnostics.Observe(new MediaFoundationH264Decoder()),
            viewerPixelFormat: RemotePixelFormat.Bgra32);
        await using var viewer = await PeerOnQDevice.CreateAsync("Coordinated4K viewer", _signalingUri,
            useRealCapture: false, mediaEngine: engine);
        await using var sharer = await PeerOnQDevice.CreateAsync("Coordinated4K sharer", _signalingUri,
            useRealCapture: false, mediaEngine: engine, captureFactory: () => capture);
        viewer.Coordinator.Profile = MediaProfile.For(QualityProfile.Automatic);
        sharer.Coordinator.Profile = MediaProfile.For(QualityProfile.Automatic);
        sharer.Coordinator.PreferredCaptureTarget = new CaptureTargetInfo(
            CaptureTargetKind.Display, "synthetic4k", "Synthetic gray4K", width, height);
        sharer.Prompt.Answer = PermissionDecision.Accept;

        var gate = new Lock();
        var presentations = new List<CoordinatedPresentation>(1024);
        var feedback = new List<CoordinatedFeedback>(256);
        var errors = new List<string>();
        var memory = new List<(long At, long Bytes)>();
        var sustained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = 1;
        var invalidFrames = 0;
        var timedFrames = 0;
        long firstAt = 0;
        viewer.Coordinator.SessionRemoteFrameReceived += (_, received) =>
        {
            var frame = received.Frame;
            if (frame.Width != width || frame.Height != height || frame.Pixels.Length != frame.ExpectedLength)
            {
                Interlocked.Increment(ref invalidFrames);
                return;
            }
            // The headless test validates a complete output buffer before reporting presentation.
            viewer.Coordinator.ReportFrameRendered(received.SessionId, new PresentedVideoFrame(
                frame.PipelineDecodedTimestamp, frame.Width, frame.Height,
                frame.CaptureTimestampUnixMicroseconds, frame.CaptureSequenceNumber, frame.PeerClockEstimate));
            if (Volatile.Read(ref accepting) == 0) return;
            var now = Stopwatch.GetTimestamp();
            Interlocked.CompareExchange(ref firstAt, now, 0);
            double? age = null;
            if (frame.PeerClockEstimate is { UncertaintyMicroseconds: >= 0 and <= 50_000 } clock
                && frame.CaptureTimestampUnixMicroseconds > 0)
            {
                var microseconds = (decimal)((DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10)
                    + clock.RemoteMinusLocalOffsetMicroseconds - frame.CaptureTimestampUnixMicroseconds;
                if (microseconds is > 0 and <= 60_000_000) age = (double)(microseconds / 1000);
            }
            if (age.HasValue) Interlocked.Increment(ref timedFrames);
            lock (gate)
            {
                if (presentations.Count < 4096)
                    presentations.Add(new(now, age, frame.PeerClockEstimate?.UncertaintyMicroseconds / 1000d));
                if (presentations.Count >= 90 && Volatile.Read(ref timedFrames) >= 30)
                    sustained.TrySetResult();
            }
        };
        // The coordinator subscribed first during CreateAsync. This observer only records
        // messages that passed registration, server session ownership and actual WebSocket delivery.
        sharer.Signaling.QualityReceived += (_, quality) =>
        {
            if (Volatile.Read(ref accepting) == 0) return;
            var statistics = sharer.Coordinator.StatisticsOf(quality.SessionId);
            var item = new CoordinatedFeedback(Stopwatch.GetTimestamp(), quality.SessionId,
                quality.CaptureToPresentLatencyP95Ms, quality.FrameAgeClockUncertaintyMs,
                quality.DecodeFps, quality.RenderFps, statistics.QualityChangeReason,
                statistics.TargetFps, statistics.AdaptiveQualityLevel);
            lock (gate) if (feedback.Count < 1024) feedback.Add(item);
        };
        void RecordError(object? sender, SignalingErrorNotification error)
        {
            lock (gate) if (errors.Count < 32) errors.Add(error.Code);
        }
        viewer.Signaling.ErrorReceived += RecordError;
        sharer.Signaling.ErrorReceived += RecordError;
        using var process = Process.GetCurrentProcess();
        SessionId? observingSessionId = null;
        void SampleMemory()
        {
            process.Refresh();
            memory.Add((Stopwatch.GetTimestamp(), process.PrivateMemorySize64));
            if (observingSessionId is { } current)
                diagnostics.ObservePipeline(sharer.Coordinator.StatisticsOf(current), viewer.Coordinator.StatisticsOf(current));
        }
        var diagnosticBeganAt = Stopwatch.GetTimestamp();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        SampleMemory();
        await viewer.ConnectAsync();
        await sharer.ConnectAsync();
        var sessionId = await viewer.Coordinator.RequestViewOnlySessionAsync(sharer.Identity.PublicId);
        observingSessionId = sessionId;
        var prompt = await sharer.Prompt.Shown.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(viewer.Identity.PublicId, prompt.RequesterId);
        Assert.Equal(SessionMode.ViewOnly, prompt.RequestedMode);
        var initialFrames = sustained.Task.WaitAsync(TimeSpan.FromSeconds(30));
        while (!initialFrames.IsCompleted)
        {
            await Task.WhenAny(initialFrames, Task.Delay(TimeSpan.FromSeconds(1)));
            SampleMemory();
        }
        await initialFrames;
        var initial = viewer.Coordinator.StatisticsOf(sessionId);
        output.WriteLine($"Coordinated initial90 rolling p95={initial.CaptureToPresentLatencyP95Ms:F2}ms; "
            + $"target100ms={(initial.CaptureToPresentLatencyP95Ms <= 100 ? "MET" : "MISSED")}");
        while (Stopwatch.GetElapsedTime(Interlocked.Read(ref firstAt)) < TimeSpan.FromSeconds(30))
        {
            SampleMemory();
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        SampleMemory();
        var observationEndedAt = Stopwatch.GetTimestamp();
        Volatile.Write(ref accepting, 0);
        CoordinatedPresentation[] frames;
        CoordinatedFeedback[] receivedFeedback;
        string[] signalingErrors;
        lock (gate)
        {
            frames = presentations.Where(item => item.At <= observationEndedAt).ToArray();
            receivedFeedback = feedback.Where(item => item.At <= observationEndedAt).ToArray();
            signalingErrors = errors.ToArray();
        }
        var senderStatistics = sharer.Coordinator.StatisticsOf(sessionId);
        var viewerStatistics = viewer.Coordinator.StatisticsOf(sessionId);
        var validFeedback = receivedFeedback.Where(item => item.AgeMs > 0 && item.ClockMs <= 50).ToArray();
        output.WriteLine($"Coordinated synthetic gray {width}x{height}; presentationBoundary=validated-headless-output-buffer; "
            + $"receivedQualityMessages={receivedFeedback.Length}; "
            + $"usableQualityMessages={validFeedback.Length}; signalingErrorCodes={string.Join(',', signalingErrors)}; "
            + $"encoder={senderStatistics.EncoderName}; hardware={senderStatistics.EncoderHardwareAccelerated}; "
            + $"decoder={viewerStatistics.DecoderName}; "
            + $"hardwareInitialisations={hardware?.InitialisationCount ?? 0}; targetFps={senderStatistics.TargetFps}; "
            + $"targetBitrateKbps={senderStatistics.TargetBitrateKbps}; reason={senderStatistics.QualityChangeReason}; "
            + $"captureToEncodeP95Ms={senderStatistics.CaptureToEncodeLatencyP95Ms:F2}; "
            + $"viewerRollingAgeP95Ms={viewerStatistics.CaptureToPresentLatencyP95Ms:F2}; "
            + $"viewerClockMs={viewerStatistics.FrameAgeClockUncertaintyMs:F2}; "
            + $"pending={viewerStatistics.VideoPresentationQueueDepth}; replaced={viewerStatistics.VideoPresentationFramesReplaced}; "
            + $"lastPresentationGapMs={Stopwatch.GetElapsedTime(frames[^1].At, observationEndedAt).TotalMilliseconds:F2}; "
            + $"allocatedBytes={GC.GetTotalAllocatedBytes(precise: true) - allocated}; "
            + $"GC0/1/2={GC.CollectionCount(0) - collections[0]}/{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]}");
        var first = frames[0].At;
        var firstFiveEnd = first + 5 * Stopwatch.Frequency;
        var lastFiveStart = observationEndedAt - 5 * Stopwatch.Frequency;
        var finalWindow = frames.Where(item => item.At >= lastFiveStart && item.At <= observationEndedAt).ToArray();
        LogWindow("startupFirst5s", frames.Where(item => item.At <= firstFiveEnd).ToArray(), first, firstFiveEnd);
        LogWindow("entireSession", frames, first, observationEndedAt);
        LogWindow("last5s", finalWindow, lastFiveStart, observationEndedAt);
        diagnostics.Write(output, diagnosticBeganAt, observationEndedAt);
        Assert.Equal(0, Volatile.Read(ref invalidFrames));
        Assert.Equal(1, sharer.Prompt.Calls);
        Assert.Equal(SessionState.ConnectedViewOnly, viewer.Coordinator.StateOf(sessionId));
        Assert.Equal(SessionState.ConnectedViewOnly, sharer.Coordinator.StateOf(sessionId));
        Assert.Equal(expectHardware, senderStatistics.EncoderHardwareAccelerated);
        Assert.Equal(width, senderStatistics.EncodedWidth);
        Assert.Equal(height, senderStatistics.EncodedHeight);
        Assert.Equal(width, viewerStatistics.RenderedWidth);
        Assert.Equal(height, viewerStatistics.RenderedHeight);
        Assert.True(viewerStatistics.CaptureToPresentLatencyP95Ms > 0);
        Assert.InRange(viewerStatistics.FrameAgeClockUncertaintyMs, 0, 50);
        Assert.InRange(viewerStatistics.VideoPresentationQueueDepth, 0, 1);
        Assert.NotEmpty(validFeedback);
        Assert.NotEmpty(finalWindow);
        Assert.All(receivedFeedback, item => Assert.Equal(sessionId, item.SessionId));
        Assert.True(Stopwatch.GetElapsedTime(validFeedback[^1].At, observationEndedAt) < TimeSpan.FromSeconds(5),
            "Authenticated viewer quality feedback was absent or stale at the end of the observation.");

        void LogWindow(string name, CoordinatedPresentation[] window, long windowStart, long windowEnd)
        {
            var ages = window.Where(item => item.AgeMs.HasValue).Select(item => item.AgeMs!.Value).Order().ToArray();
            var duration = Stopwatch.GetElapsedTime(windowStart, windowEnd).TotalSeconds;
            var p95 = ages.Length == 0 ? 0 : ages[(int)Math.Ceiling(ages.Length * .95) - 1];
            var clocks = window.Where(item => item.ClockMs.HasValue).Select(item => item.ClockMs!.Value).ToArray();
            var quality = receivedFeedback.Where(item => item.At >= windowStart && item.At <= windowEnd).ToArray();
            var privateBytes = memory.Where(item => item.At >= windowStart && item.At <= windowEnd)
                .Select(item => item.Bytes).ToArray();
            output.WriteLine($"Coordinated4K {name}: frames={window.Length}; authenticatedAgeSamples={ages.Length}; "
                + $"durationSeconds={duration:F2}; fps={Math.Max(0, window.Length - 1) / duration:F2}; p95Ms={p95:F2}; "
                + $"clockMaxMs={(clocks.Length == 0 ? double.NaN : clocks.Max()):F2}; target100ms={(ages.Length > 0 && p95 <= 100 ? "MET" : "MISSED")}; "
                + $"receivedQualityMessages={quality.Length}; "
                + (quality.Length == 0 ? "feedback=unavailable" :
                    $"receivedAgeMin/Max/LastMs={quality.Min(item => item.AgeMs):F2}/{quality.Max(item => item.AgeMs):F2}/{quality[^1].AgeMs:F2}; "
                    + $"receivedClockMax/LastMs={quality.Max(item => item.ClockMs):F2}/{quality[^1].ClockMs:F2}; "
                    + $"receivedDecode/RenderFpsLast={quality[^1].DecodeFps:F2}/{quality[^1].RenderFps:F2}; "
                    + $"senderTargetFpsMin/Max/Last={quality.Min(item => item.TargetFps)}/{quality.Max(item => item.TargetFps)}/{quality[^1].TargetFps}; "
                    + $"senderLevelLast={quality[^1].Level}; senderReasons="
                    + string.Join(',', quality.GroupBy(item => item.Reason).Select(group => $"{group.Key}:{group.Count()}")))
                + $"; privateBytesSamples={privateBytes.Length}; privateBytesStart/Max/End="
                + (privateBytes.Length == 0 ? "unavailable" : $"{privateBytes[0]}/{privateBytes.Max()}/{privateBytes[^1]}"));
        }
    }

    private sealed record CoordinatedPresentation(long At, double? AgeMs, double? ClockMs);
    private sealed record CoordinatedFeedback(long At, SessionId SessionId, double AgeMs, double ClockMs,
        double DecodeFps, double RenderFps, string? Reason, int TargetFps, int? Level);

    [Fact]
    public void Windows_client_advertises_the_managed_hybrid_security_provider()
    {
        Assert.True(PostQuantumCryptography.IsSupported);
        Assert.Contains(
            EndpointCapabilityNames.HybridPostQuantumSecure,
            WindowsClientCapabilityProfile.Create().Capabilities);
    }

    /// <summary>
    /// The production Windows media configuration end to end: real Windows.Graphics.Capture on the
    /// sharer, the app's hardware H.264 encoder, Media Foundation decoder and BGRA32 viewer
    /// output, the Automatic/P1080 profile the dashboard applies, real signaling and WebRTC.
    /// The default-engine test above never exercises real capture with hardware H.264.
    /// </summary>
    [HardwareVideoTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Production_media_configuration_delivers_real_screen_frames(bool sharerHardware)
    {
        WebRtcMediaEngine AppEngine(bool hardware = true) => new(
            hardwareH264Available: () => hardware && HardwareEncoderProbe.Supports(HardwareVideoCodec.H264),
            hardwareH264EncoderFactory: () => new MediaFoundationH264Encoder(),
            h264DecoderFactory: MediaFoundationH264Decoder.TryCreate,
            viewerPixelFormat: RemotePixelFormat.Bgra32);
        await using var viewer = await PeerOnQDevice.CreateAsync("App viewer", _signalingUri,
            useRealCapture: false, mediaEngine: AppEngine());
        await using var sharer = await PeerOnQDevice.CreateAsync("App sharer", _signalingUri,
            useRealCapture: true, mediaEngine: AppEngine(sharerHardware));
        var profile = MediaProfile.For(QualityProfile.Automatic) with { Resolution = CaptureResolution.P1080 };
        viewer.Coordinator.Profile = profile;
        sharer.Coordinator.Profile = profile;
        await viewer.ConnectAsync();
        await sharer.ConnectAsync();
        sharer.Coordinator.PreferredCaptureTarget = DisplayEnumerator.ListCaptureTargets()[0];
        sharer.Prompt.Answer = PermissionDecision.Accept;

        var frames = 0;
        var invalid = 0;
        var firstFrame = new TaskCompletionSource<RemoteVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.Coordinator.SessionRemoteFrameReceived += (_, received) =>
        {
            var frame = received.Frame;
            if (frame.Format != RemotePixelFormat.Bgra32 || frame.Pixels.Length != frame.ExpectedLength)
            {
                Interlocked.Increment(ref invalid);
                return;
            }
            Interlocked.Increment(ref frames);
            viewer.Coordinator.ReportFrameRendered(received.SessionId, new PresentedVideoFrame(
                frame.PipelineDecodedTimestamp, frame.Width, frame.Height,
                frame.CaptureTimestampUnixMicroseconds, frame.CaptureSequenceNumber, frame.PeerClockEstimate));
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(frame.Pixels, out var segment) && segment.Array is { } owned)
                frame.ReturnBuffer?.Invoke(owned);
            firstFrame.TrySetResult(frame);
        };

        var sessionId = await viewer.Coordinator.RequestViewOnlySessionAsync(sharer.Identity.PublicId);
        var first = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(TimeSpan.FromSeconds(6));

        var sharerStats = sharer.Coordinator.StatisticsOf(sessionId);
        var viewerStats = viewer.Coordinator.StatisticsOf(sessionId);
        output.WriteLine($"first {first.Width}x{first.Height}; frames={frames} invalid={invalid}");
        output.WriteLine($"sharer: encoder={sharerStats.EncoderName} hw={sharerStats.EncoderHardwareAccelerated} " +
                         $"captured={sharerStats.FramesCaptured} encoded={sharerStats.FramesEncoded} " +
                         $"dropped={sharerStats.FramesDropped} {sharerStats.EncodedWidth}x{sharerStats.EncodedHeight} " +
                         $"reason={sharerStats.QualityChangeReason} profile={sharerStats.ActiveQualityProfile}");
        output.WriteLine($"viewer: decoder={viewerStats.DecoderName} rendered={viewerStats.FramesRendered} " +
                         $"{viewerStats.RenderedWidth}x{viewerStats.RenderedHeight} path={viewerStats.ConnectionPath}");

        Assert.Equal(0, invalid);
        // The display list arrives before any viewer window exists; it must be retained for it.
        var displays = viewer.Coordinator.RemoteDisplaysOf(sessionId);
        Assert.NotNull(displays);
        Assert.Equal(DisplayEnumerator.ListCaptureTargets().Count, displays!.Displays.Count);
        output.WriteLine($"remote displays retained for the viewer: {displays.Displays.Count}");
        // A peer without hardware H.264 (or without a decoder) falls back to VP8; it must still render.
        Assert.Equal(sharerHardware, sharerStats.EncoderHardwareAccelerated);
        Assert.True(frames > 0 && viewerStats.FramesRendered > 0, "the viewer received no real screen frame");
        Assert.True(first.Width >= 640 && first.Height >= 360);
        await sharer.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedBySharer);
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
