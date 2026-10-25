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
