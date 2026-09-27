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
///   * the caller must name a display; there is no implicit desktop capture
///   * the system capture border stays enabled, so the shared screen is visibly captured
///   * every frame is converted to I420 for the encoder and the GPU resources are released
///     on stop, device loss, or disposal
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class WindowsGraphicsCaptureSource(ILogger<WindowsGraphicsCaptureSource>? logger = null)
    : IScreenCaptureSource, IAdaptiveCaptureSource
{
    /// <summary>IID of IGraphicsCaptureItem, the interface the interop factory returns.</summary>
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly ILogger _log = logger ?? NullLogger<WindowsGraphicsCaptureSource>.Instance;
    private readonly Lock _gate = new();
    private readonly FrameRateLimiter _captureRateLimiter = new(30);

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _lastSize;
    private long _sequence;
    private int _disposed;
    private CaptureResolution _resolution = CaptureResolution.Automatic;
    private int _downscaleFactor = 1;
    private bool _includeCursor = true;
    private bool _lowLatencyConversion;
    private int _maxFps = 30;

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

        var display = DisplayEnumerator.FindByDeviceName(request.Target.Id)
                      ?? throw new InvalidOperationException(
                          $"Display '{request.Target.Id}' is no longer connected.");

        lock (_gate)
        {
            _resolution = request.Resolution;
            _includeCursor = request.IncludeCursor;
            _lowLatencyConversion = request.MaxFramesPerSecond >= 60
                                    && request.Resolution is CaptureResolution.P720
                                        or CaptureResolution.P1080
                                        or CaptureResolution.P1440
                                        or CaptureResolution.P2160;
            SetTargetFps(request.MaxFramesPerSecond);
            _downscaleFactor = 1;

            CreateDevice();

            _item = CreateItemForMonitor(display.MonitorHandle);
            _item.Closed += OnItemClosed;

            _lastSize = _item.Size;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                _lastSize);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = request.IncludeCursor;

            // Keep the yellow capture border on: the person being shared must be able to
            // see that a capture is running.
            TrySetBorderRequired(_session, required: true);

            _session.StartCapture();

            IsCapturing = true;
            Target = request.Target with { Width = _lastSize.Width, Height = _lastSize.Height };
        }

        LogCaptureStarted(_log, display.DisplayName, _lastSize.Width, _lastSize.Height, request.IncludeCursor);
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
        var flags = DeviceCreationFlags.BgraSupport;

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
            out var device);

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
        _ = StopAsync();
        CaptureStopped?.Invoke(this, CaptureStoppedReason.TargetClosed);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed == 1) return;

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            // The display resolution or rotation changed: rebuild the pool for the new size.
            if (frame.ContentSize.Width != _lastSize.Width || frame.ContentSize.Height != _lastSize.Height)
            {
                LogCaptureSizeChanged(_log, frame.ContentSize.Width, frame.ContentSize.Height);

                lock (_gate)
                {
                    _lastSize = frame.ContentSize;
                    _staging?.Dispose();
                    _staging = null;
                    _framePool?.Recreate(
                        _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _lastSize);
                    Target = Target is null ? null : Target with { Width = _lastSize.Width, Height = _lastSize.Height };
                }

                return;
            }

            // Drop excess WGC frames before the expensive 4K GPU readback and BGRA-to-I420
            // conversion. The bounded latest-frame encoder queue handles later backpressure.
            if (!_captureRateLimiter.ShouldAccept()) return;

            var i420 = ConvertToI420(
                frame,
                out var width,
                out var height,
                out var sourceWidth,
                out var sourceHeight,
                out var requestedWidth,
                out var requestedHeight);
            if (i420 is null) return;

            FrameArrived?.Invoke(this, new CapturedFrame
            {
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                RequestedWidth = requestedWidth,
                RequestedHeight = requestedHeight,
                Width = width,
                Height = height,
                I420 = i420,
                Timestamp = frame.SystemRelativeTime,
                SequenceNumber = Interlocked.Increment(ref _sequence),
                BufferOwnershipCanTransfer = true,
            });
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (IsDeviceLost(ex))
        {
            LogGraphicsDeviceLost(_log, ex);
            IsCapturing = false;
            CaptureStopped?.Invoke(this, CaptureStoppedReason.DeviceLost);
        }
        catch (Exception ex)
        {
            LogFrameHandlingFailed(_log, ex);
            CaptureStopped?.Invoke(this, CaptureStoppedReason.Error);
        }
    }

    private static bool IsDeviceLost(SharpGen.Runtime.SharpGenException ex) =>
        ex.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved ||
        ex.ResultCode == Vortice.DXGI.ResultCode.DeviceReset;

    /// <summary>
    /// Copies the captured BGRA surface into a CPU-readable staging texture and converts it
    /// to I420, which is what the VP8 encoder consumes.
    /// </summary>
    private byte[]? ConvertToI420(
        Direct3D11CaptureFrame frame,
        out int width,
        out int height,
        out int sourceWidth,
        out int sourceHeight,
        out int requestedWidth,
        out int requestedHeight)
    {
        sourceWidth = 0;
        sourceHeight = 0;
        requestedWidth = 0;
        requestedHeight = 0;
        width = frame.ContentSize.Width;
        height = frame.ContentSize.Height;

        // Chroma subsampling needs even dimensions.
        width -= width % 2;
        height -= height % 2;
        if (width <= 0 || height <= 0) return null;

        using var surface = Direct3D11Helper.CreateTexture(frame.Surface);
        if (surface is null) return null;

        lock (_gate)
        {
            if (_device is null || _context is null) return null;

            var sourceDescription = surface.Description;

            // The staging texture mirrors the captured surface exactly; the even-sized crop
            // happens during the I420 conversion instead of during the GPU copy.
            if (_staging is null ||
                _staging.Description.Width != sourceDescription.Width ||
                _staging.Description.Height != sourceDescription.Height)
            {
                _staging?.Dispose();
                _staging = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = sourceDescription.Width,
                    Height = sourceDescription.Height,
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

            sourceWidth = Math.Min(width, (int)sourceDescription.Width);
            sourceHeight = Math.Min(height, (int)sourceDescription.Height);
            if (sourceWidth <= 0 || sourceHeight <= 0) return null;

            // The encoder input size follows the requested resolution, and the adaptive
            // controller can shrink it further while the session is running.
            var (targetWidth, targetHeight) =
                FrameScaler.ResolveOutputSize(sourceWidth, sourceHeight, _resolution);
            requestedWidth = targetWidth;
            requestedHeight = targetHeight;

            var factor = Volatile.Read(ref _downscaleFactor);
            if (factor > 1)
            {
                targetWidth = Math.Max(2, targetWidth / factor);
                targetHeight = Math.Max(2, targetHeight / factor);
                targetWidth -= targetWidth % 2;
                targetHeight -= targetHeight % 2;
            }

            if (targetWidth <= 0 || targetHeight <= 0) return null;

            width = targetWidth;
            height = targetHeight;

            _context.CopyResource(_staging, surface);

            var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                return _lowLatencyConversion
                    ? FrameScaler.BgraToI420LowLatency(
                        mapped.DataPointer, (int)mapped.RowPitch,
                        sourceWidth, sourceHeight,
                        targetWidth, targetHeight)
                    : FrameScaler.BgraToI420(
                        mapped.DataPointer, (int)mapped.RowPitch,
                        sourceWidth, sourceHeight,
                        targetWidth, targetHeight);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
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

        await StopAsync(cancellationToken);
        await StartAsync(request, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!IsCapturing && _session is null) return Task.CompletedTask;

            IsCapturing = false;

            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }

            if (_item is not null)
            {
                _item.Closed -= OnItemClosed;
            }

            _session?.Dispose();
            _framePool?.Dispose();
            _staging?.Dispose();

            _session = null;
            _framePool = null;
            _staging = null;
            _item = null;
        }

        LogCaptureStopped(_log);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await StopAsync();

        lock (_gate)
        {
            _winrtDevice?.Dispose();
            _context?.Dispose();
            _device?.Dispose();

            _winrtDevice = null;
            _context = null;
            _device = null;
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
