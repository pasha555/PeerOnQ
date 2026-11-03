using System.Diagnostics;
using System.Numerics;
using PeerOnQ.Platform.Windows.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class CompositionVideoPresenterTests(ITestOutputHelper output)
{
    [HardwareVideoFact]
    public void Frames_upload_exactly_and_resizes_and_layout_changes_re_present()
    {
        using var presenter = D3D11CompositionVideoPresenter.TryCreate();
        Assert.NotNull(presenter);
        Assert.NotEqual(0, presenter!.SwapChainPointer);
        presenter.UpdateLayout(new VideoPresentationLayout(1280, 720, 1.5f, 1.5f, VideoPresentationScaleMode.Fit));

        foreach (var (width, height) in new[] { (1920, 1080), (3840, 2160), (1366, 768) })
        {
            var frame = Pattern(width, height);
            var began = Stopwatch.GetTimestamp();
            presenter.Present(width, height, frame);
            var milliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            output.WriteLine($"{width}x{height}: first present {milliseconds:F2} ms");

            Assert.Equal((width, height), (presenter.Width, presenter.Height));
            Assert.Equal(frame, presenter.ReadLastFrameForTesting());
        }

        var presented = presenter.FramesPresented;
        presenter.UpdateLayout(new VideoPresentationLayout(800, 600, 1f, 1f, VideoPresentationScaleMode.Fill));
        Assert.Equal(presented + 1, presenter.FramesPresented);
        // An unchanged layout does not present again.
        presenter.UpdateLayout(new VideoPresentationLayout(800, 600, 1f, 1f, VideoPresentationScaleMode.Fill));
        Assert.Equal(presented + 1, presenter.FramesPresented);

        var steady = Pattern(3840, 2160);
        presenter.Present(3840, 2160, steady);
        var start = Stopwatch.GetTimestamp();
        const int iterations = 20;
        for (var index = 0; index < iterations; index++) presenter.Present(3840, 2160, steady);
        output.WriteLine($"4K steady present {Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations:F2} ms");
    }

    [HardwareVideoFact]
    public void A_disposed_presenter_ignores_late_frames()
    {
        var presenter = D3D11CompositionVideoPresenter.TryCreate();
        Assert.NotNull(presenter);
        presenter!.Dispose();
        presenter.Present(64, 64, new byte[64 * 64 * 4]);
        presenter.UpdateLayout(new VideoPresentationLayout(64, 64, 1, 1, VideoPresentationScaleMode.Fit));
        Assert.Equal(0, presenter.FramesPresented);
        Assert.Throws<ArgumentException>(() =>
            D3D11CompositionVideoPresenterContract.PresentTooSmall());
    }

    [Theory]
    [InlineData(VideoPresentationScaleMode.Fit, 0.5f, 0.5f, 0f, 30f)]
    [InlineData(VideoPresentationScaleMode.Fill, 0.55556f, 0.55556f, -53.33333f, 0f)]
    [InlineData(VideoPresentationScaleMode.Stretch, 0.5f, 0.55556f, 0f, 0f)]
    [InlineData(VideoPresentationScaleMode.ActualSize, 0.8f, 0.8f, 0f, 0f)]
    public void Swap_chain_transform_mirrors_the_xaml_stretch_modes(
        VideoPresentationScaleMode mode, float scaleX, float scaleY, float offsetX, float offsetY)
    {
        // A 1920x1080 frame in a 960x600 DIP viewport at 125% display scale.
        var transform = D3D11CompositionVideoPresenter.ComputeTransform(
            1920, 1080, new VideoPresentationLayout(960, 600, 1.25f, 1.25f, mode));

        Assert.Equal(scaleX, transform.M11, 3);
        Assert.Equal(scaleY, transform.M22, 3);
        Assert.Equal(offsetX, transform.M31, 3);
        Assert.Equal(offsetY, transform.M32, 3);
        Assert.Equal((0f, 0f), (transform.M12, transform.M21));
        Assert.True(Matrix3x2.Invert(transform, out _));
    }

    [Theory]
    // A 1080p remote in a 1080p viewer window (toolbar and title bar take 87 px): uniform, centered.
    [InlineData(VideoPresentationScaleMode.Fit, 1920, 993, 0, 0, 1920, 1080, 77, 0, 1842, 993)]
    // Fullscreen at the remote's own size is exactly 1:1.
    [InlineData(VideoPresentationScaleMode.Fit, 1920, 1080, 0, 0, 1920, 1080, 0, 0, 1920, 1080)]
    // A frame that fits with under 2% to spare is not stretched for the sliver.
    [InlineData(VideoPresentationScaleMode.Fit, 1940, 1090, 0, 0, 1920, 1080, 10, 5, 1930, 1085)]
    [InlineData(VideoPresentationScaleMode.Fill, 1000, 1000, 420, 0, 1500, 1080, 0, 0, 1000, 1000)]
    [InlineData(VideoPresentationScaleMode.Stretch, 800, 600, 0, 0, 1920, 1080, 0, 0, 800, 600)]
    [InlineData(VideoPresentationScaleMode.ActualSize, 1600, 900, 0, 0, 1600, 900, 0, 0, 1600, 900)]
    public void Scaled_rectangles_are_whole_pixels_and_mirror_the_xaml_stretch_modes(
        VideoPresentationScaleMode mode, int bufferWidth, int bufferHeight,
        int sourceLeft, int sourceTop, int sourceRight, int sourceBottom,
        int left, int top, int right, int bottom)
    {
        var (source, destination) = D3D11CompositionVideoPresenter.ComputeScaledRects(
            1920, 1080, bufferWidth, bufferHeight, mode);

        Assert.Equal((sourceLeft, sourceTop, sourceRight, sourceBottom), (source.Left, source.Top, source.Right, source.Bottom));
        Assert.Equal((left, top, right, bottom), (destination.Left, destination.Top, destination.Right, destination.Bottom));
    }

    [HardwareVideoFact]
    public void The_presenter_scales_on_the_video_processor_once_the_viewport_is_known()
    {
        using var presenter = D3D11CompositionVideoPresenter.TryCreate();
        Assert.NotNull(presenter);
        presenter!.Present(1920, 1080, Pattern(1920, 1080));
        presenter.UpdateLayout(new VideoPresentationLayout(1536, 794.4, 1.25f, 1.25f, VideoPresentationScaleMode.Fit));
        presenter.Present(1920, 1080, Pattern(1920, 1080));

        Assert.True(presenter.UsesVideoProcessorScaling);
        Assert.Equal((1920, 993), D3D11CompositionVideoPresenter.PhysicalViewport(
            new VideoPresentationLayout(1536, 794.4, 1.25f, 1.25f, VideoPresentationScaleMode.Fit)));
    }

    [HardwareVideoFact]
    public void Video_processor_downscaling_keeps_one_pixel_text_strokes_darker_than_bilinear()
    {
        // One-pixel black strokes on white, every 7 px, scaled 1920 -> 1766 (a 1080p remote in a
        // 1080p window). Bilinear (the compositor's transform) splits most strokes into two greys.
        const int width = 1920, height = 1080, targetWidth = 1766, targetHeight = 993;
        var source = new byte[width * height * 4];
        var luma = new byte[width];
        for (var x = 0; x < width; x++) luma[x] = (byte)(x % 7 == 3 ? 0 : 255);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            source[offset] = source[offset + 1] = source[offset + 2] = luma[x];
            source[offset + 3] = 255;
        }

        var result = Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware,
            Vortice.Direct3D11.DeviceCreationFlags.BgraSupport, [Vortice.Direct3D.FeatureLevel.Level_11_0], out var device);
        Assert.True(result.Success);
        using (device)
        {
            using var context = device!.ImmediateContext;
            using var scaler = D3D11BgraScaler.TryCreate(device, context);
            Assert.NotNull(scaler);
            using var input = Texture(device, width, height, Vortice.Direct3D11.BindFlags.ShaderResource | Vortice.Direct3D11.BindFlags.RenderTarget);
            context.UpdateSubresource(source, input, 0, width * 4, 0);
            using var target = Texture(device, targetWidth, targetHeight, Vortice.Direct3D11.BindFlags.RenderTarget);
            scaler!.Scale(input, width, height, target, targetWidth, targetHeight,
                new Vortice.RawRect(0, 0, width, height), new Vortice.RawRect(0, 0, targetWidth, targetHeight));
            var row = ReadRow(device, context, target, targetWidth, targetHeight, targetHeight / 2);

            var bilinear = new byte[targetWidth];
            var scale = (double)targetWidth / width;
            for (var x = 0; x < targetWidth; x++)
            {
                var position = Math.Clamp((x + 0.5) / scale - 0.5, 0, width - 1);
                var x0 = (int)Math.Floor(position);
                var x1 = Math.Min(width - 1, x0 + 1);
                var weight = position - x0;
                bilinear[x] = (byte)Math.Round(luma[x0] * (1 - weight) + luma[x1] * weight);
            }

            double Darkness(byte[] scaled)
            {
                var total = 0.0;
                var strokes = 0;
                for (var x = 3; x < width - 7; x += 7)
                {
                    var mapped = (x + 0.5) * scale - 0.5;
                    var minimum = 255;
                    for (var t = (int)Math.Floor(mapped) - 1; t <= (int)Math.Ceiling(mapped) + 1; t++)
                        if (t >= 0 && t < targetWidth) minimum = Math.Min(minimum, scaled[t]);
                    total += 255 - minimum;
                    strokes++;
                }
                return total / strokes;
            }

            var processor = Darkness(row);
            var reference = Darkness(bilinear);
            output.WriteLine($"stroke darkness: video processor {processor:F1}, bilinear {reference:F1} (255 = black)");
            Assert.True(processor >= reference, $"video processor {processor:F1} < bilinear {reference:F1}");
        }
    }

    private static Vortice.Direct3D11.ID3D11Texture2D Texture(
        Vortice.Direct3D11.ID3D11Device device, int width, int height, Vortice.Direct3D11.BindFlags bind) =>
        device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = bind,
        });

    /// <summary>The green channel of one row (the pattern is grey).</summary>
    private static byte[] ReadRow(Vortice.Direct3D11.ID3D11Device device, Vortice.Direct3D11.ID3D11DeviceContext context,
        Vortice.Direct3D11.ID3D11Texture2D texture, int width, int height, int row)
    {
        using var staging = device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Staging,
            CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read,
        });
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = new byte[width * 4];
            System.Runtime.InteropServices.Marshal.Copy(mapped.DataPointer + (nint)((long)row * mapped.RowPitch), bytes, 0, bytes.Length);
            var green = new byte[width];
            for (var x = 0; x < width; x++) green[x] = bytes[x * 4 + 1];
            return green;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static byte[] Pattern(int width, int height)
    {
        var bytes = new byte[width * height * 4];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 31 + index / 4097);
        for (var index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
        return bytes;
    }

    private static class D3D11CompositionVideoPresenterContract
    {
        public static void PresentTooSmall()
        {
            using var presenter = D3D11CompositionVideoPresenter.TryCreate()
                ?? throw new InvalidOperationException("no presenter");
            presenter.Present(64, 64, new byte[64 * 64 * 4 - 1]);
        }
    }
}
