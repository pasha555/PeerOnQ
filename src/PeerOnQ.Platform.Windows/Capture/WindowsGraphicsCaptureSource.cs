using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace PeerOnQ.Platform.Windows.Capture;

/// <summary>
/// Screen capture on top of Windows.Graphics.Capture.
///
/// Security and resource behavior:
///   * the caller must name a display (or the explicit "All displays" choice); there is no
///     implicit desktop capture
///   * the system capture border stays enabled, so the shared screen is visibly captured
///   * every frame is converted for the encoder and the GPU resources are released on stop,
///     device loss, or disposal
///
/// Every arriving frame is first copied on the GPU into a persistent canvas. A single display
/// fills the canvas; "All displays" places each display at its virtual-desktop offset. Frames
/// are converted from the canvas when the rate limiter allows, and a trailing conversion sends
/// the newest canvas after a skipped frame, so the final state of a burst (the last typed
/// character, the end of a scroll) is never left unsent on a now-static desktop.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class WindowsGraphicsCaptureSource(ILogger<WindowsGraphicsCaptureSource>? logger = null)
    : IScreenCaptureSource, IAdaptiveCaptureSource
{
    /// <summary>IID of IGraphicsCaptureItem, the interface the interop factory returns.</summary>
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>Consecutive failed frames tolerated before capture is restarted or stopped.</summary>
    internal const int MaximumConsecutiveFrameFailures = 3;

    /// <summary>A failure streak must also last this long, so one bad burst cannot end a session.</summary>
    internal static readonly TimeSpan FrameFailureGracePeriod = TimeSpan.FromSeconds(2);

    /// <summary>Capture restarts attempted per session before reporting a stopped capture.</summary>
    internal const int MaximumDeviceRecoveries = 3;

    private readonly ILogger _log = logger ?? NullLogger<WindowsGraphicsCaptureSource>.Instance;
    private readonly Lock _gate = new();
    private readonly FrameRateLimiter _captureRateLimiter = new(30);
    private readonly List<CaptureSlot> _slots = [];
    private CaptureRequest? _activeRequest;
    private int _consecutiveFrameFailures;
    private long _firstFrameFailureTimestamp;
    private int _deviceRecoveries;
    private int _recoveryInProgress;
    private CancellationTokenSource? _recoveryCancellation;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private ID3D11Texture2D? _canvas;
    private int _canvasWidth;
    private int _canvasHeight;
    private bool _composite;
    private bool _canvasDirty;
    private TimeSpan _canvasTimestamp;
    private Timer? _trailingTimer;
    private int _trailingScheduled;
    private ID3D11Texture2D? _staging;
    private D3D11Nv12Converter? _gpuConverter;
    private long _sequence;
    private int _disposed;
    private CaptureResolution _resolution = CaptureResolution.Automatic;
    private int _downscaleFactor = 1;
    private bool _includeCursor = true;
    private bool _lowLatencyConversion;
    private int _maxFps = 30;

    /// <summary>One captured display: its WGC item, pool, session and offset in the canvas.</summary>
    private sealed class CaptureSlot
    {
        public required GraphicsCaptureItem Item { get; init; }
        public required Direct3D11CaptureFramePool Pool { get; init; }
        public required GraphicsCaptureSession Session { get; init; }
        public required int OffsetX { get; init; }
        public required int OffsetY { get; init; }
        public SizeInt32 Size { get; set; }
        public global::Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object>? FrameHandler { get; set; }
    }

    [LoggerMessage(2000, LogLevel.Information, "Capture started on {Display} ({Width}x{Height}), cursor={Cursor}")]
    private static partial void LogCaptureStarted(
        ILogger logger,
        string display,
        int width,
        int height,
        bool cursor);

    [LoggerMessage(2001, LogLevel.Warning, "Hardware D3D11 device unavailable ({Result}); falling back to WARP")]
    private static partial void LogHardwareDeviceUnavailable(ILogger logger, string result);

    [LoggerMessage(2002, LogLevel.Information, "Capture target closed")]
    private static partial void LogCaptureTargetClosed(ILogger logger);

    [LoggerMessage(2003, LogLevel.Information, "Capture size changed to {Width}x{Height}")]
    private static partial void LogCaptureSizeChanged(ILogger logger, int width, int height);

    [LoggerMessage(2004, LogLevel.Error, "Graphics device lost during capture")]
    private static partial void LogGraphicsDeviceLost(ILogger logger, Exception exception);

    [LoggerMessage(2005, LogLevel.Error, "Capture frame handling failed")]
    private static partial void LogFrameHandlingFailed(ILogger logger, Exception exception);

    [LoggerMessage(2006, LogLevel.Information, "Capture downscale factor set to {Factor}")]
    private static partial void LogDownscaleChanged(ILogger logger, int factor);

    [LoggerMessage(2007, LogLevel.Information, "Capture stopped")]
    private static partial void LogCaptureStopped(ILogger logger);

    [LoggerMessage(2008, LogLevel.Information, "Capture pipeline configured for {Resolution} at {Fps} fps ({ConversionMode})")]
    private static partial void LogCapturePipelineConfigured(
        ILogger logger,
        CaptureResolution resolution,
        int fps,
        string conversionMode);

    [LoggerMessage(2009, LogLevel.Warning, "Restarting screen capture (attempt {Attempt})")]
    private static partial void LogDeviceRecoveryStarted(ILogger logger, int attempt);

    [LoggerMessage(2010, LogLevel.Error, "Screen capture could not be restarted")]
    private static partial void LogDeviceRecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(2011, LogLevel.Information, "Capture conversion path: {Path}")]
    private static partial void LogConversionPath(ILogger logger, string path);

    [LoggerMessage(2012, LogLevel.Warning, "GPU NV12 conversion failed; using the CPU converter for this device")]
    private static partial void LogGpuConversionFailed(ILogger logger, Exception exception);

    [LoggerMessage(2013, LogLevel.Information, "Display layout changed; recomposing all displays")]
    private static partial void LogLayoutChanged(ILogger logger);

    public bool IsCapturing { get; private set; }
    public CaptureTargetInfo? Target { get; private set; }

    public event EventHandler<CapturedFrame>? FrameArrived;
    public event EventHandler<CaptureStoppedReason>? CaptureStopped;

    /// <summary>True when the OS supports Windows.Graphics.Capture at all.</summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task StartAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        CancelRecovery();
        return StartCore(request, preserveAdaptiveState: false, recovery: null);
    }

    /// <summary>
    /// Starts (or restarts) capture. Owner starts reset adaptive state; display switches and
    /// restarts keep the session's current frame rate and downscale factor, so a software-encoder
    /// 1080p floor or an adaptive 4K fallback survives the restart.
    /// </summary>
    private Task StartCore(CaptureRequest request, bool preserveAdaptiveState, CancellationToken? recovery)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Target.Kind != CaptureTargetKind.Display)
        {
            throw new NotSupportedException("This build supports explicit full-display capture only.");
        }

        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Windows.Graphics.Capture is not available on this system.");
        }

        var composite = DisplayEnumerator.IsAllDisplays(request.Target.Id);
        var displays = composite
            ? DisplayEnumerator.ListDisplays()
            : [DisplayEnumerator.FindByDeviceName(request.Target.Id)
               ?? throw new InvalidOperationException($"Display '{request.Target.Id}' is no longer connected.")];
        var bounds = DisplayEnumerator.VirtualDesktop(displays)
                     ?? throw new InvalidOperationException("No display is connected.");
        if (composite && displays.Count < 2)
            throw new InvalidOperationException("All displays requires more than one connected display.");

        int canvasWidth, canvasHeight;
        lock (_gate)
        {
            // Owner Stop/Dispose/Start and restarts are serialized by this lock. A restart that
            // the owner cancelled, or a source disposed meanwhile, must not resurrect a capture
            // session; an existing session is always stopped before a new one starts.
            ObjectDisposedException.ThrowIf(_disposed == 1, this);
            if (recovery is { IsCancellationRequested: true }) return Task.CompletedTask;
            if (_slots.Count != 0) StopLocked();

            _activeRequest = request;
            _consecutiveFrameFailures = 0;
            _resolution = request.Resolution;
            _includeCursor = request.IncludeCursor;
            _lowLatencyConversion = request.MaxFramesPerSecond >= 60
                                    && request.Resolution is CaptureResolution.P720
                                        or CaptureResolution.P1080
                                        or CaptureResolution.P1440
                                        or CaptureResolution.P2160;
            SetTargetFps(preserveAdaptiveState ? Volatile.Read(ref _maxFps) : request.MaxFramesPerSecond);
            if (!preserveAdaptiveState) _downscaleFactor = 1;

            CreateDevice();
            _composite = composite;

            foreach (var display in displays)
            {
                var item = CreateItemForMonitor(display.MonitorHandle);
                var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    _winrtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    numberOfBuffers: 2,
                    item.Size);
                var session = pool.CreateCaptureSession(item);
                var slot = new CaptureSlot
                {
                    Item = item,
                    Pool = pool,
                    Session = session,
                    OffsetX = display.X - bounds.X,
                    OffsetY = display.Y - bounds.Y,
                    Size = item.Size,
                };
                slot.FrameHandler = (sender, _) => OnFrameArrived(slot, sender);
                pool.FrameArrived += slot.FrameHandler;
                item.Closed += OnItemClosed;
                session.IsCursorCaptureEnabled = request.IncludeCursor;

                // Keep the yellow capture border on: the person being shared must be able to
                // see that a capture is running.
                TrySetBorderRequired(session, required: true);
                _slots.Add(slot);
            }

            canvasWidth = composite ? bounds.Width : _slots[0].Size.Width;
            canvasHeight = composite ? bounds.Height : _slots[0].Size.Height;
            CreateCanvas(canvasWidth, canvasHeight);
            foreach (var slot in _slots) slot.Session.StartCapture();

            IsCapturing = true;
            Target = request.Target with { Width = canvasWidth, Height = canvasHeight };
        }

        LogCaptureStarted(_log, composite ? DisplayEnumerator.AllDisplaysName : displays[0].DisplayName,
            canvasWidth, canvasHeight, request.IncludeCursor);
        LogCapturePipelineConfigured(
            _log,
            request.Resolution,
            request.MaxFramesPerSecond,
            _lowLatencyConversion ? "low-latency" : "quality");

        return Task.CompletedTask;
    }

    private static void TrySetBorderRequired(GraphicsCaptureSession session, bool required)
    {
        try
        {
            // Available from Windows 11; older builds always show the border anyway.
            session.IsBorderRequired = required;
        }
        catch (Exception)
        {
            // Property not present on this build: the OS still draws its own indicator.
        }
    }

    private void CreateDevice()
    {
        // A restart (display switch or recovery) replaces the device; release the previous one
        // instead of leaking a D3D11 device per restart.
        ReleaseDevice();
        var flags = DeviceCreationFlags.BgraSupport;

        // Video support enables GPU scaling and NV12 conversion; a driver that rejects it still
        // captures through the CPU conversion path.
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out var device);
        if (result.Failure)
        {
            result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                flags,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out device);
        }

        if (result.Failure)
        {
            if (_log.IsEnabled(LogLevel.Warning))
                LogHardwareDeviceUnavailable(_log, result.ToString());

            result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Warp,
                flags,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out device);

            result.CheckError();
        }

        // CheckError above throws on failure, so a null device here would be a driver bug.
        _device = device ?? throw new InvalidOperationException("D3D11 returned no device.");
        _context = _device.ImmediateContext;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);

        _gpuConverter = D3D11Nv12Converter.TryCreate(_device, _context);
        LogConversionPath(_log, _gpuConverter is null ? "CPU BGRA to I420" : "GPU BGRA to NV12");
    }

    /// <summary>A persistent GPU canvas cleared to opaque black (gaps between displays).</summary>
    private void CreateCanvas(int width, int height)
    {
        _canvas?.Dispose();
        _canvas = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });
        using (var target = _device.CreateRenderTargetView(_canvas))
            _context!.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));
        _canvasWidth = width;
        _canvasHeight = height;
        _canvasDirty = false;
        _staging?.Dispose();
        _staging = null;
    }

    private static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
    {
        // The interop interface lives on the activation factory, not on an instance.
        using var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();

        var iid = GraphicsCaptureItemIid;
        var itemPointer = interop.CreateForMonitor(monitor, ref iid);

        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        LogCaptureTargetClosed(_log);
        if (_composite)
        {
            // One display of the composed view disappeared: recompose the remaining displays.
            LogLayoutChanged(_log);
            BeginRecovery(new InvalidOperationException("A composed display was removed."),
                CaptureStoppedReason.TargetClosed, countAttempt: false);
            return;
        }
        _ = StopAsync();
        CaptureStopped?.Invoke(this, CaptureStoppedReason.TargetClosed);
    }

    private void OnFrameArrived(CaptureSlot slot, Direct3D11CaptureFramePool sender)
    {
        if (_disposed == 1) return;

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            lock (_gate)
            {
                if (!IsCapturing || !_slots.Contains(slot) || _canvas is null || _context is null) return;

                // The display resolution or rotation changed: rebuild the pool for the new size.
                if (frame.ContentSize.Width != slot.Size.Width || frame.ContentSize.Height != slot.Size.Height)
                {
                    LogCaptureSizeChanged(_log, frame.ContentSize.Width, frame.ContentSize.Height);
                    slot.Size = frame.ContentSize;
                    slot.Pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, slot.Size);
                    if (_composite)
                    {
                        // Offsets of every display may have moved; recompose from the new layout.
                        LogLayoutChanged(_log);
                        BeginRecovery(new InvalidOperationException("The display layout changed."),
                            CaptureStoppedReason.Error, countAttempt: false);
                    }
                    else
                    {
                        CreateCanvas(slot.Size.Width, slot.Size.Height);
                        Target = Target is null ? null : Target with { Width = slot.Size.Width, Height = slot.Size.Height };
                    }
                    return;
                }

                using var surface = Direct3D11Helper.CreateTexture(frame.Surface);
                if (surface is null) return;
                var copyWidth = Math.Min(frame.ContentSize.Width, _canvasWidth - slot.OffsetX);
                var copyHeight = Math.Min(frame.ContentSize.Height, _canvasHeight - slot.OffsetY);
                var description = surface.Description;
                copyWidth = Math.Min(copyWidth, (int)description.Width);
                copyHeight = Math.Min(copyHeight, (int)description.Height);
                if (copyWidth <= 0 || copyHeight <= 0) return;
                _context.CopySubresourceRegion(_canvas, 0, (uint)slot.OffsetX, (uint)slot.OffsetY, 0,
                    surface, 0, new Box(0, 0, 0, copyWidth, copyHeight, 1));
                _canvasDirty = true;
                _canvasTimestamp = frame.SystemRelativeTime;

                // Drop excess WGC frames before the expensive readback and conversion; the canvas
                // keeps their content and the trailing conversion sends the final state.
                if (!_captureRateLimiter.ShouldAccept())
                {
                    ScheduleTrailingEmit();
                    return;
                }

                EmitCanvasLocked();
            }
        }
        catch (Exception ex)
        {
            HandleFrameFailure(ex);
        }
    }

    /// <summary>Converts the canvas and raises one frame. Callers hold the capture lock.</summary>
    private void EmitCanvasLocked()
    {
        var pixels = ConvertCanvas(
            out var width,
            out var height,
            out var sourceWidth,
            out var sourceHeight,
            out var requestedWidth,
            out var requestedHeight,
            out var pixelFormat);
        if (pixels is null) return;
        _canvasDirty = false;

        FrameArrived?.Invoke(this, new CapturedFrame
        {
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            RequestedWidth = requestedWidth,
            RequestedHeight = requestedHeight,
            Width = width,
            Height = height,
            I420 = pixels,
            Format = pixelFormat,
            Timestamp = _canvasTimestamp,
            SequenceNumber = Interlocked.Increment(ref _sequence),
            BufferOwnershipCanTransfer = true,
        });
        Volatile.Write(ref _consecutiveFrameFailures, 0);
    }

    /// <summary>
    /// Re-sends the persistent canvas through the rate-limited trailing path, so a key frame the
    /// viewer requested is produced even when the desktop is static.
    /// </summary>
    public void RequestRefresh()
    {
        if (_disposed == 1) return;
        lock (_gate)
        {
            if (!IsCapturing || _canvas is null) return;
            _canvasDirty = true;
        }
        ScheduleTrailingEmit();
    }

    private void ScheduleTrailingEmit()
    {
        if (Interlocked.Exchange(ref _trailingScheduled, 1) == 1) return;
        var interval = TimeSpan.FromMilliseconds(Math.Ceiling(1000d / Math.Max(1, Volatile.Read(ref _maxFps))) + 1);
        _trailingTimer ??= new Timer(_ => OnTrailingEmit());
        _trailingTimer.Change(interval, Timeout.InfiniteTimeSpan);
    }

    private void OnTrailingEmit()
    {
        Volatile.Write(ref _trailingScheduled, 0);
        if (_disposed == 1) return;
        try
        {
            lock (_gate)
            {
                if (!IsCapturing || !_canvasDirty || _canvas is null) return;
                if (!_captureRateLimiter.ShouldAccept())
                {
                    ScheduleTrailingEmit();
                    return;
                }
                EmitCanvasLocked();
            }
        }
        catch (Exception ex)
        {
            HandleFrameFailure(ex);
        }
    }

    private void HandleFrameFailure(Exception ex)
    {
        if (ex is SharpGen.Runtime.SharpGenException sharpGen && IsDeviceLost(sharpGen)
            || ex is COMException com && IsDeviceLostResult(com.HResult))
        {
            // The WinRT frame pool reports removal through a projected COM exception, D3D11
            // through SharpGen; both invalidate every resource and need a fresh device.
            LogGraphicsDeviceLost(_log, ex);
            BeginRecovery(ex, CaptureStoppedReason.DeviceLost);
            return;
        }

        LogFrameHandlingFailed(_log, ex);
        // One failed conversion or subscriber callback must not end a remote session. A failure
        // streak that persists for the grace period restarts capture (bounded); only a failure
        // that survives the restarts is reported as stopped capture.
        if (RecordFrameFailure(out var firstFailure)) BeginRecovery(ex, CaptureStoppedReason.Error);
        else if (firstFailure) ScheduleFailureCheck(ex);
    }

    private bool RecordFrameFailure(out bool firstFailure)
    {
        var now = Stopwatch.GetTimestamp();
        var failures = Interlocked.Increment(ref _consecutiveFrameFailures);
        firstFailure = failures == 1;
        if (firstFailure) Volatile.Write(ref _firstFrameFailureTimestamp, now);
        var persisted = Stopwatch.GetElapsedTime(Volatile.Read(ref _firstFrameFailureTimestamp), now);
        return failures >= MaximumConsecutiveFrameFailures && persisted >= FrameFailureGracePeriod;
    }

    /// <summary>
    /// Windows.Graphics.Capture delivers frames only on change. After a failed frame on a static
    /// desktop no further frame may arrive, so a still-failing streak is checked on a timer; the
    /// restart then produces a fresh initial frame instead of leaving the viewer frozen.
    /// </summary>
    private void ScheduleFailureCheck(Exception cause)
    {
        var streakStartedAt = Volatile.Read(ref _firstFrameFailureTimestamp);
        _ = Task.Run(async () =>
        {
            await Task.Delay(FrameFailureGracePeriod + TimeSpan.FromMilliseconds(250));
            if (_disposed == 1 || !IsCapturing) return;
            if (Volatile.Read(ref _consecutiveFrameFailures) > 0
                && Volatile.Read(ref _firstFrameFailureTimestamp) == streakStartedAt)
                BeginRecovery(cause, CaptureStoppedReason.Error);
        });
    }

    /// <summary>
    /// Device removal (driver update, TDR, adapter change) invalidates every D3D resource, a
    /// persistent frame failure needs a fresh pipeline, and a composed view must follow layout
    /// changes. Rebuild the device, frame pools and sessions for the same target; failure
    /// recoveries are bounded per session. The restart runs under the capture lock and is
    /// cancelled by any owner Stop, Switch or Dispose.
    /// </summary>
    private void BeginRecovery(Exception cause, CaptureStoppedReason reason, bool countAttempt = true)
    {
        if (Interlocked.Exchange(ref _recoveryInProgress, 1) != 0) return;
        var request = _activeRequest;
        var attempt = countAttempt ? Interlocked.Increment(ref _deviceRecoveries) : Volatile.Read(ref _deviceRecoveries);
        if (request is null || attempt > MaximumDeviceRecoveries || _disposed == 1)
        {
            IsCapturing = false;
            Volatile.Write(ref _recoveryInProgress, 0);
            LogDeviceRecoveryFailed(_log, cause);
            CaptureStopped?.Invoke(this, reason);
            return;
        }

        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _recoveryCancellation, cancellation)?.Cancel();
        var token = cancellation.Token;
        LogDeviceRecoveryStarted(_log, Math.Max(1, attempt));
        _ = Task.Run(async () =>
        {
            try
            {
                // Give the driver (or the display layout) a moment to settle first.
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Max(1, attempt)), token);
                await StartCore(request, preserveAdaptiveState: true, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The owner stopped, switched or disposed the capture first.
            }
            catch (ObjectDisposedException) when (_disposed == 1)
            {
                // Disposed while the restart was pending.
            }
            catch (Exception ex)
            {
                IsCapturing = false;
                LogDeviceRecoveryFailed(_log, ex);
                CaptureStopped?.Invoke(this, reason);
            }
            finally
            {
                Interlocked.CompareExchange(ref _recoveryCancellation, null, cancellation);
                Volatile.Write(ref _recoveryInProgress, 0);
            }
        });
    }

    private void ReleaseDevice()
    {
        _gpuConverter?.Dispose();
        _gpuConverter = null;
        _staging?.Dispose();
        _staging = null;
        _canvas?.Dispose();
        _canvas = null;
        _winrtDevice?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _winrtDevice = null;
        _context = null;
        _device = null;
    }

    private static bool IsDeviceLost(SharpGen.Runtime.SharpGenException ex) =>
        ex.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved ||
        ex.ResultCode == Vortice.DXGI.ResultCode.DeviceReset;

    // DXGI_ERROR_DEVICE_REMOVED, DXGI_ERROR_DEVICE_HUNG and DXGI_ERROR_DEVICE_RESET.
    private static bool IsDeviceLostResult(int result) =>
        result is unchecked((int)0x887A0005) or unchecked((int)0x887A0006) or unchecked((int)0x887A0007);

    /// <summary>
    /// Converts the canvas for the encoder: on the GPU to NV12 when supported, otherwise through
    /// a CPU-readable staging copy to I420.
    /// </summary>
    private byte[]? ConvertCanvas(
        out int width,
        out int height,
        out int sourceWidth,
        out int sourceHeight,
        out int requestedWidth,
        out int requestedHeight,
        out CapturedPixelFormat format)
    {
        format = CapturedPixelFormat.I420;
        // Chroma subsampling needs even dimensions.
        sourceWidth = _canvasWidth - (_canvasWidth % 2);
        sourceHeight = _canvasHeight - (_canvasHeight % 2);
        width = requestedWidth = sourceWidth;
        height = requestedHeight = sourceHeight;
        if (_device is null || _context is null || _canvas is null || sourceWidth <= 0 || sourceHeight <= 0) return null;

        // Keep requested and actual dimensions separate. Adaptation may reduce larger sources,
        // but never below the capture floor or above the source's real detail.
        var (targetWidth, targetHeight) = FrameScaler.ResolveOutputSize(sourceWidth, sourceHeight, _resolution);
        var factor = Volatile.Read(ref _downscaleFactor);
        var (adaptedWidth, adaptedHeight) = factor > 1
            ? FrameScaler.ResolveOutputSize(sourceWidth, sourceHeight, _resolution, factor)
            : (targetWidth, targetHeight);
        if (_composite)
        {
            // The composed view can be wider than any hardware encoder accepts.
            (targetWidth, targetHeight) = FrameScaler.FitWithinHardwareWindow(targetWidth, targetHeight);
            (adaptedWidth, adaptedHeight) = FrameScaler.FitWithinHardwareWindow(adaptedWidth, adaptedHeight);
        }
        requestedWidth = targetWidth;
        requestedHeight = targetHeight;
        if (adaptedWidth <= 0 || adaptedHeight <= 0) return null;
        width = adaptedWidth;
        height = adaptedHeight;

        if (_gpuConverter is { } gpu)
        {
            try
            {
                var nv12 = gpu.Convert(_canvas, sourceWidth, sourceHeight, adaptedWidth, adaptedHeight);
                format = CapturedPixelFormat.Nv12;
                return nv12;
            }
            catch (SharpGen.Runtime.SharpGenException ex) when (IsDeviceLost(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                // A driver without full video-processor support must not stop the stream.
                LogGpuConversionFailed(_log, ex);
                gpu.Dispose();
                _gpuConverter = null;
            }
        }

        if (_staging is null
            || _staging.Description.Width != (uint)_canvasWidth
            || _staging.Description.Height != (uint)_canvasHeight)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_canvasWidth,
                Height = (uint)_canvasHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        _context.CopyResource(_staging, _canvas);
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            return _lowLatencyConversion
                ? FrameScaler.BgraToI420LowLatency(
                    mapped.DataPointer, (int)mapped.RowPitch,
                    sourceWidth, sourceHeight,
                    adaptedWidth, adaptedHeight)
                : FrameScaler.BgraToI420(
                    mapped.DataPointer, (int)mapped.RowPitch,
                    sourceWidth, sourceHeight,
                    adaptedWidth, adaptedHeight);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    // ---------------------------------------------------------- IAdaptiveCaptureSource

    public IReadOnlyList<CaptureTargetInfo> AvailableTargets => DisplayEnumerator.ListCaptureTargets();

    public int DownscaleFactor => Volatile.Read(ref _downscaleFactor);

    public int TargetFps => Volatile.Read(ref _maxFps);

    public void SetTargetFps(int fps)
    {
        var clamped = Math.Clamp(fps, 1, 120);
        Volatile.Write(ref _maxFps, clamped);
        _captureRateLimiter.SetTargetFps(clamped);
    }

    /// <summary>
    /// 1 = the requested resolution, 2 = half, 4 = quarter. Applied on the next frame, so the
    /// adaptive controller can react without restarting the capture session.
    /// </summary>
    public void SetDownscaleFactor(int factor)
    {
        var clamped = factor switch
        {
            <= 1 => 1,
            2 => 2,
            _ => 4,
        };

        if (Interlocked.Exchange(ref _downscaleFactor, clamped) != clamped)
        {
            LogDownscaleChanged(_log, clamped);
        }
    }

    /// <summary>Switches the shared display without tearing down the peer connection.</summary>
    public async Task SwitchTargetAsync(CaptureTargetInfo target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var request = new CaptureRequest
        {
            Target = target,
            IncludeCursor = _includeCursor,
            MaxFramesPerSecond = _maxFps,
            Resolution = _resolution,
        };

        // The restart stops the previous display under the capture lock; if the new display is
        // gone the exception leaves the current capture running instead of a blank session.
        CancelRecovery();
        await StartCore(request, preserveAdaptiveState: true, recovery: null);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancelRecovery();
        bool stopped;
        lock (_gate) stopped = StopLocked();
        if (stopped) LogCaptureStopped(_log);
        return Task.CompletedTask;
    }

    private bool StopLocked()
    {
        if (!IsCapturing && _slots.Count == 0) return false;

        IsCapturing = false;
        _trailingTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Volatile.Write(ref _trailingScheduled, 0);
        foreach (var slot in _slots)
        {
            if (slot.FrameHandler is not null) slot.Pool.FrameArrived -= slot.FrameHandler;
            slot.Item.Closed -= OnItemClosed;
            slot.Session.Dispose();
            slot.Pool.Dispose();
        }
        _slots.Clear();
        _staging?.Dispose();
        _staging = null;
        _canvasDirty = false;
        return true;
    }

    private void CancelRecovery() => Interlocked.Exchange(ref _recoveryCancellation, null)?.Cancel();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        CancelRecovery();
        await StopAsync();

        lock (_gate)
        {
            _trailingTimer?.Dispose();
            _trailingTimer = null;
            ReleaseDevice();
        }
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}

[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    nint CreateForWindow([In] nint window, [In] ref Guid iid);

    nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
}

/// <summary>Bridges a WinRT IDirect3DSurface back to the underlying D3D11 texture.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class Direct3D11Helper
{
    public static ID3D11Texture2D? CreateTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var pointer = access.GetInterface(typeof(ID3D11Texture2D).GUID);

        return pointer == nint.Zero ? null : new ID3D11Texture2D(pointer);
    }
}

[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    nint GetInterface([In] in Guid iid);
}
