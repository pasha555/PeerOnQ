using System.Runtime.Versioning;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PeerOnQ.Platform.Windows.Rendering;

/// <summary>
/// Scales a BGRA frame into a BGRA target with the Direct3D 11 video processor, so the swap chain
/// can stay at the viewport's physical size and one-pixel text lands on pixel-aligned rectangles.
///
/// Auto-processing is off and both sides use full-range sRGB (RgbFullG22NoneP709), so the
/// processor only scales: no colour conversion, sharpening or noise reduction. The processor is
/// recreated when the input or output size changes; the input view is recreated when the source
/// texture changes. <see cref="TryCreate"/> returns null when the device cannot process BGRA video,
/// and the caller then keeps the frame-sized swap chain and compositor transform.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class D3D11BgraScaler : IDisposable
{
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext1 _videoContext;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _inputTexture;
    private ID3D11VideoProcessorInputView? _inputView;
    private int _inputWidth, _inputHeight, _outputWidth, _outputHeight;
    private bool _disposed;

    private D3D11BgraScaler(ID3D11VideoDevice videoDevice, ID3D11VideoContext1 videoContext)
    {
        _videoDevice = videoDevice;
        _videoContext = videoContext;
    }

    /// <summary>Returns a scaler, or null when the device has no BGRA-capable video processor.</summary>
    public static D3D11BgraScaler? TryCreate(ID3D11Device device, ID3D11DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);
        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext1? videoContext = null;
        try
        {
            var support = device.CheckFormatSupport(Format.B8G8R8A8_UNorm);
            if ((support & FormatSupport.VideoProcessorInput) == 0 || (support & FormatSupport.VideoProcessorOutput) == 0)
                return null;

            videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
            videoContext = context.QueryInterfaceOrNull<ID3D11VideoContext1>();
            if (videoDevice is null || videoContext is null)
            {
                videoDevice?.Dispose();
                videoContext?.Dispose();
                return null;
            }
            return new D3D11BgraScaler(videoDevice, videoContext);
        }
        catch (Exception)
        {
            videoDevice?.Dispose();
            videoContext?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Scales <paramref name="sourceRect"/> of <paramref name="source"/> into
    /// <paramref name="destinationRect"/> of <paramref name="target"/>; the rest of the target is
    /// filled with opaque black.
    /// </summary>
    public void Scale(
        ID3D11Texture2D source, int sourceWidth, int sourceHeight,
        ID3D11Texture2D target, int targetWidth, int targetHeight,
        RawRect sourceRect, RawRect destinationRect)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureProcessor(sourceWidth, sourceHeight, targetWidth, targetHeight);
        EnsureInputView(source);

        _videoDevice.CreateVideoProcessorOutputView(target, _enumerator, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        }, out var outputView).CheckError();
        using (outputView)
        {
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, sourceRect);
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, destinationRect);
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, targetWidth, targetHeight));
            var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView };
            _videoContext.VideoProcessorBlt(_processor, outputView, 0, 1, [stream]).CheckError();
        }
    }

    private void EnsureProcessor(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        if (_processor is not null && inputWidth == _inputWidth && inputHeight == _inputHeight
            && outputWidth == _outputWidth && outputHeight == _outputHeight)
            return;

        ReleaseProcessor();
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)inputWidth,
            InputHeight = (uint)inputHeight,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            Usage = VideoUsage.OptimalQuality,
        };
        _videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();
        _videoContext.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetOutputBackgroundColor(_processor, false, new VideoColor
        {
            Rgba = new VideoColorRgba { R = 0f, G = 0f, B = 0f, A = 1f },
        });
        _inputWidth = inputWidth;
        _inputHeight = inputHeight;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
    }

    private void EnsureInputView(ID3D11Texture2D source)
    {
        if (_inputView is not null && ReferenceEquals(_inputTexture, source)) return;

        _inputView?.Dispose();
        _inputView = null;
        _inputTexture = null;
        _videoDevice.CreateVideoProcessorInputView(source, _enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        }, out _inputView).CheckError();
        _inputTexture = source;
    }

    private void ReleaseProcessor()
    {
        _inputView?.Dispose();
        _inputView = null;
        _inputTexture = null;
        _processor?.Dispose();
        _enumerator?.Dispose();
        _processor = null;
        _enumerator = null;
        _inputWidth = _inputHeight = _outputWidth = _outputHeight = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseProcessor();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
