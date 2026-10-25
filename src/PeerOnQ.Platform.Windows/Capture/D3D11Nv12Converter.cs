using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PeerOnQ.Platform.Windows.Capture;

/// <summary>
/// Scales a captured BGRA surface and converts it to BT.601 limited-range NV12 on the GPU with
/// Direct3D 11 video processing, then reads back only the NV12 result.
///
/// The CPU path reads back the whole BGRA surface (33 MB at 4K) and converts it on the capture
/// thread; its generic box scaler costs 39 ms (1440p→1080p) and 46 ms (4K→1080p) per frame in
/// Release builds. Here the GPU does both steps in a fraction of a millisecond and the readback
/// shrinks to 12 MB at 4K or 3 MB at 1080p. Driver auto-processing (denoise, edge enhancement)
/// is disabled so text is not altered. Any failure leaves the caller on its CPU path.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class D3D11Nv12Converter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext1 _videoContext;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _input;
    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11Texture2D? _output;
    private ID3D11VideoProcessorOutputView? _outputView;
    private ID3D11Texture2D? _staging;
    private int _inputWidth, _inputHeight, _outputWidth, _outputHeight;
    private bool _disposed;

    private D3D11Nv12Converter(
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11VideoDevice videoDevice,
        ID3D11VideoContext1 videoContext)
    {
        _device = device;
        _context = context;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
    }

    /// <summary>Null when the device has no video processor for BGRA input and NV12 output.</summary>
    public static D3D11Nv12Converter? TryCreate(ID3D11Device device, ID3D11DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);
        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext1? videoContext = null;
        try
        {
            if ((device.CheckFormatSupport(Format.B8G8R8A8_UNorm) & FormatSupport.VideoProcessorInput) == 0
                || (device.CheckFormatSupport(Format.NV12) & FormatSupport.VideoProcessorOutput) == 0)
                return null;
            videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
            videoContext = context.QueryInterfaceOrNull<ID3D11VideoContext1>();
            if (videoDevice is null || videoContext is null)
            {
                videoDevice?.Dispose();
                videoContext?.Dispose();
                return null;
            }
            return new D3D11Nv12Converter(device, context, videoDevice, videoContext);
        }
        catch (Exception)
        {
            videoDevice?.Dispose();
            videoContext?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Converts the top-left <paramref name="sourceWidth"/> x <paramref name="sourceHeight"/>
    /// region of <paramref name="source"/> into a new tightly packed NV12 frame of the target size.
    /// </summary>
    public byte[] Convert(ID3D11Texture2D source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0
            || (targetWidth & 1) != 0 || (targetHeight & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "NV12 output needs positive even dimensions.");

        var description = source.Description;
        if (sourceWidth > description.Width || sourceHeight > description.Height)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The source region exceeds the captured surface.");
        EnsureResources((int)description.Width, (int)description.Height, targetWidth, targetHeight);

        // The capture pool owns its surfaces; a GPU-side copy into a processor-bound texture is
        // cheap and avoids depending on the frame pool's bind flags.
        _context.CopyResource(_input!, source);
        var sourceRect = new RawRect(0, 0, sourceWidth, sourceHeight);
        var targetRect = new RawRect(0, 0, targetWidth, targetHeight);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, sourceRect);
        _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, targetRect);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor!, true, targetRect);
        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView! };
        _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, 1, [stream]).CheckError();
        _context.CopyResource(_staging!, _output!);

        var lumaBytes = targetWidth * targetHeight;
        var output = GC.AllocateUninitializedArray<byte>(lumaBytes * 3 / 2);
        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pitch = (int)mapped.RowPitch;
            var luma = mapped.DataPointer;
            // NV12 staging memory stores the interleaved UV plane after Height pitched rows. A
            // driver that pads the luma plane reports it through the subresource size.
            var lumaRows = targetHeight;
            var paddedRows = (long)mapped.DepthPitch * 2 / (3L * pitch);
            if (paddedRows > targetHeight && paddedRows <= targetHeight + 64 && (paddedRows & 1) == 0
                && paddedRows * pitch * 3 / 2 == mapped.DepthPitch)
                lumaRows = (int)paddedRows;
            var chroma = luma + (nint)((long)pitch * lumaRows);
            for (var row = 0; row < targetHeight; row++)
                Marshal.Copy(luma + (nint)((long)row * pitch), output, row * targetWidth, targetWidth);
            for (var row = 0; row < targetHeight / 2; row++)
                Marshal.Copy(chroma + (nint)((long)row * pitch), output, lumaBytes + row * targetWidth, targetWidth);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
        return output;
    }

    private void EnsureResources(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        if (_processor is not null
            && inputWidth == _inputWidth && inputHeight == _inputHeight
            && outputWidth == _outputWidth && outputHeight == _outputHeight)
            return;

        ReleaseResources();
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(30, 1),
            InputWidth = (uint)inputWidth,
            InputHeight = (uint)inputHeight,
            OutputFrameRate = new Rational(30, 1),
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            Usage = VideoUsage.OptimalSpeed,
        };
        _videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();

        _input = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)inputWidth,
            Height = (uint)inputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });
        _videoDevice.CreateVideoProcessorInputView(_input, _enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        }, out _inputView).CheckError();

        _output = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)outputWidth,
            Height = (uint)outputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _videoDevice.CreateVideoProcessorOutputView(_output, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        }, out _outputView).CheckError();

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)outputWidth,
            Height = (uint)outputHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        // Desktop pixels are full-range sRGB; the encoders expect BT.601 studio-swing YCbCr,
        // the same matrix and range as the CPU converter. No driver enhancement may touch text.
        _videoContext.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.YcbcrStudioG22LeftP601);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);

        _inputWidth = inputWidth;
        _inputHeight = inputHeight;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
    }

    private void ReleaseResources()
    {
        _staging?.Dispose();
        _outputView?.Dispose();
        _output?.Dispose();
        _inputView?.Dispose();
        _input?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _staging = null;
        _outputView = null;
        _output = null;
        _inputView = null;
        _input = null;
        _processor = null;
        _enumerator = null;
        _inputWidth = _inputHeight = _outputWidth = _outputHeight = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseResources();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
