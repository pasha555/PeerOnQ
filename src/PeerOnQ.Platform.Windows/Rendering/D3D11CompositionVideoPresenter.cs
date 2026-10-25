using System.Numerics;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PeerOnQ.Platform.Windows.Rendering;

/// <summary>How a remote frame is placed in the viewer's viewport (mirrors XAML Stretch).</summary>
public enum VideoPresentationScaleMode
{
    /// <summary>Uniform: the whole frame, letterboxed and centered.</summary>
    Fit,

    /// <summary>UniformToFill: the viewport covered, the frame centered and cropped.</summary>
    Fill,

    /// <summary>Fill: the frame distorted to the viewport.</summary>
    Stretch,

    /// <summary>One physical display pixel per remote pixel, at the top-left.</summary>
    ActualSize,
}

/// <summary>The viewport the swap chain panel occupies, in DIPs, and its composition scale.</summary>
public readonly record struct VideoPresentationLayout(
    double ViewportWidth,
    double ViewportHeight,
    float CompositionScaleX,
    float CompositionScaleY,
    VideoPresentationScaleMode Mode);

/// <summary>
/// Presents decoded BGRA frames through a Direct3D 11 composition swap chain (a WinUI
/// SwapChainPanel), off the UI thread.
///
/// A WriteableBitmap is copied on the UI thread and uploaded again by XAML at its next render
/// pass; a 4K frame then competes with input and layout for the UI thread. Here one upload goes
/// straight to the GPU from the presentation worker, and scaling is a swap chain transform the
/// compositor applies, so no CPU scaling pass and no UI-thread frame work remain. A copy of the
/// last frame stays on the GPU so a resize or scale-mode change re-presents without a new frame.
/// Any failure is reported to the caller, which falls back to the bitmap path.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class D3D11CompositionVideoPresenter : IDisposable
{
    private const int BufferCount = 3;
    private const Format PixelFormat = Format.B8G8R8A8_UNorm;

    private readonly Lock _gate = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDXGISwapChain2 _swapChain2;
    private ID3D11Texture2D? _frame;
    private VideoPresentationLayout? _layout;
    private bool _disposed;

    private D3D11CompositionVideoPresenter(
        ID3D11Device device, ID3D11DeviceContext context, IDXGISwapChain1 swapChain, IDXGISwapChain2 swapChain2)
    {
        _device = device;
        _context = context;
        _swapChain = swapChain;
        _swapChain2 = swapChain2;
    }

    /// <summary>The IDXGISwapChain1 to hand to ISwapChainPanelNative.SetSwapChain.</summary>
    public nint SwapChainPointer => _swapChain.NativePointer;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FramesPresented { get; private set; }

    /// <summary>Null when no hardware Direct3D 11 device or composition swap chain is available.</summary>
    /// <summary>
    /// Hardware adapters (software ones such as WARP / Microsoft Basic Render excluded) and the
    /// default adapter. The viewer uses the swap chain by default only with exactly one hardware
    /// adapter: hybrid-GPU laptops stay on the bitmap presenter until verified there.
    /// </summary>
    public static (int HardwareAdapters, string DefaultAdapter, bool DefaultIsSoftware) DescribeAdapters()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var hardware = 0;
            var defaultName = "none";
            var defaultSoftware = true;
            for (uint index = 0; factory.EnumAdapters1(index, out var adapter).Success; index++)
            {
                using (adapter)
                {
                    var description = adapter.Description1;
                    var software = IsSoftwareAdapter(description);
                    if (index == 0)
                    {
                        defaultName = description.Description;
                        defaultSoftware = software;
                    }
                    if (!software) hardware++;
                }
            }
            return (hardware, defaultName, defaultSoftware);
        }
        catch (Exception)
        {
            return (0, "unavailable", true);
        }
    }

    private static bool IsSoftwareAdapter(AdapterDescription1 description) =>
        ((int)description.Flags & 2) != 0 || description.VendorId == 0x1414;

    public static D3D11CompositionVideoPresenter? TryCreate()
    {
        ID3D11Device? device = null;
        IDXGISwapChain1? swapChain = null;
        try
        {
            var result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out device);
            if (result.Failure || device is null) return null;

            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            // A software rasterizer only moves the CPU cost elsewhere; the bitmap path is cheaper.
            using (var adapter1 = adapter.QueryInterface<IDXGIAdapter1>())
                if (IsSoftwareAdapter(adapter1.Description1)) { device.Dispose(); return null; }
            using var factory = adapter.GetParent<IDXGIFactory2>();
            swapChain = factory.CreateSwapChainForComposition(device, new SwapChainDescription1
            {
                Width = 16,
                Height = 16,
                Format = PixelFormat,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = BufferCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            });
            var swapChain2 = swapChain.QueryInterface<IDXGISwapChain2>();
            return new D3D11CompositionVideoPresenter(device, device.ImmediateContext, swapChain, swapChain2);
        }
        catch (Exception)
        {
            swapChain?.Dispose();
            device?.Dispose();
            return null;
        }
    }

    /// <summary>Uploads one packed BGRA32 frame and presents it with the current layout.</summary>
    public void Present(int width, int height, ReadOnlySpan<byte> bgra32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra32.Length < (long)width * height * 4)
            throw new ArgumentException("The frame is smaller than its dimensions.", nameof(bgra32));

        lock (_gate)
        {
            if (_disposed) return;
            if (_frame is null || width != Width || height != Height)
            {
                _frame?.Dispose();
                _frame = null;
                _swapChain.ResizeBuffers(BufferCount, (uint)width, (uint)height, PixelFormat, SwapChainFlags.None)
                    .CheckError();
                _frame = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = PixelFormat,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                });
                Width = width;
                Height = height;
            }

            _context.UpdateSubresource(bgra32[..(width * height * 4)], _frame, 0, (uint)(width * 4), 0);
            PresentLocked();
        }
    }

    /// <summary>Applies a new viewport layout and re-presents the last frame with it.</summary>
    public void UpdateLayout(VideoPresentationLayout layout)
    {
        lock (_gate)
        {
            if (_disposed || _layout == layout) return;
            _layout = layout;
            if (_frame is not null) PresentLocked();
        }
    }

    private void PresentLocked()
    {
        using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            _context.CopyResource(backBuffer, _frame!);
        if (_layout is { } layout)
            _swapChain2.MatrixTransform = ComputeTransform(Width, Height, layout);
        _swapChain.Present(0, PresentFlags.None).CheckError();
        FramesPresented++;
    }

    /// <summary>Maps swap chain pixels to panel DIPs exactly like the matching XAML Stretch.</summary>
    public static Matrix3x2 ComputeTransform(int width, int height, VideoPresentationLayout layout)
    {
        var viewportWidth = Math.Max(1, layout.ViewportWidth);
        var viewportHeight = Math.Max(1, layout.ViewportHeight);
        float scaleX, scaleY;
        switch (layout.Mode)
        {
            case VideoPresentationScaleMode.Stretch:
                scaleX = (float)(viewportWidth / width);
                scaleY = (float)(viewportHeight / height);
                return new Matrix3x2(scaleX, 0, 0, scaleY, 0, 0);
            case VideoPresentationScaleMode.ActualSize:
                scaleX = 1f / Math.Max(0.01f, layout.CompositionScaleX);
                scaleY = 1f / Math.Max(0.01f, layout.CompositionScaleY);
                return new Matrix3x2(scaleX, 0, 0, scaleY, 0, 0);
            default:
                var uniform = layout.Mode == VideoPresentationScaleMode.Fill
                    ? Math.Max(viewportWidth / width, viewportHeight / height)
                    : Math.Min(viewportWidth / width, viewportHeight / height);
                var offsetX = (viewportWidth - width * uniform) / 2;
                var offsetY = (viewportHeight - height * uniform) / 2;
                return new Matrix3x2((float)uniform, 0, 0, (float)uniform, (float)offsetX, (float)offsetY);
        }
    }

    /// <summary>Test hook: the uploaded copy of the last frame, read back as BGRA32.</summary>
    internal byte[] ReadLastFrameForTesting()
    {
        lock (_gate)
        {
            if (_frame is null) return [];
            using var staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = PixelFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _context.CopyResource(staging, _frame);
            var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var output = new byte[Width * Height * 4];
                for (var row = 0; row < Height; row++)
                    System.Runtime.InteropServices.Marshal.Copy(
                        mapped.DataPointer + (nint)((long)row * mapped.RowPitch), output, row * Width * 4, Width * 4);
                return output;
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _frame?.Dispose();
            _frame = null;
            _swapChain2.Dispose();
            _swapChain.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
