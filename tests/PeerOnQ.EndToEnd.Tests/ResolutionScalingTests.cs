using PeerOnQ.Application.Abstractions;
using PeerOnQ.Platform.Windows.Capture;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

public class FrameScalerTests
{
    [Fact]
    public void Performance_profile_uses_1080p_and_never_the_blurry_720p_default()
    {
        var profile = MediaProfile.For(QualityProfile.Performance);

        Assert.Equal(CaptureResolution.P1080, profile.Resolution);
        Assert.Equal(60, profile.TargetFps);
        Assert.Equal(12_000, profile.MaxBitrateKbps);
    }

    [Fact]
    public void Office_and_low_bandwidth_profiles_have_distinct_bounded_policies()
    {
        var office = MediaProfile.For(QualityProfile.Office);
        Assert.Equal(CaptureResolution.P1080, office.Resolution);
        Assert.Equal(30, office.TargetFps);

        var lowBandwidth = MediaProfile.For(QualityProfile.LowBandwidth);
        Assert.Equal(CaptureResolution.P720, lowBandwidth.Resolution);
        Assert.Equal(15, lowBandwidth.TargetFps);
        Assert.True(lowBandwidth.MaxBitrateKbps < office.MaxBitrateKbps);
    }

    [Theory]
    [InlineData(1920, 1080, CaptureResolution.Native, 1920, 1080)]
    [InlineData(3840, 2160, CaptureResolution.Native, 3840, 2160)]
    [InlineData(1920, 1080, CaptureResolution.Automatic, 1920, 1080)]
    [InlineData(3840, 2160, CaptureResolution.Automatic, 1920, 1080)]
    [InlineData(1920, 1080, CaptureResolution.P720, 1280, 720)]
    [InlineData(3840, 2160, CaptureResolution.P720, 1280, 720)]
    [InlineData(3840, 2160, CaptureResolution.P1080, 1920, 1080)]
    [InlineData(3840, 2160, CaptureResolution.P1440, 2560, 1440)]
    [InlineData(7680, 4320, CaptureResolution.P2160, 3840, 2160)]
    [InlineData(1920, 1080, CaptureResolution.P2160, 1920, 1080)]
    [InlineData(2560, 1600, CaptureResolution.P720, 1152, 720)]
    public void Output_size_follows_the_requested_resolution(
        int sourceWidth, int sourceHeight, CaptureResolution resolution, int expectedWidth, int expectedHeight)
    {
        var (width, height) = FrameScaler.ResolveOutputSize(sourceWidth, sourceHeight, resolution);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Theory]
    [InlineData(1280, 720, CaptureResolution.P1080)]
    [InlineData(1024, 768, CaptureResolution.P1080)]
    [InlineData(800, 600, CaptureResolution.P720)]
    public void A_small_display_is_never_upscaled(int width, int height, CaptureResolution resolution)
    {
        var (outputWidth, outputHeight) = FrameScaler.ResolveOutputSize(width, height, resolution);

        Assert.True(outputWidth <= width);
        Assert.True(outputHeight <= height);
    }

    [Fact]
    public void Output_dimensions_are_always_even()
    {
        foreach (var resolution in Enum.GetValues<CaptureResolution>())
        {
            foreach (var (w, h) in new[] { (1919, 1079), (1365, 767), (2559, 1439), (3, 3) })
            {
                var (width, height) = FrameScaler.ResolveOutputSize(w, h, resolution);

                Assert.Equal(0, width % 2);
                Assert.Equal(0, height % 2);
                Assert.True(width >= 2);
                Assert.True(height >= 2);
            }
        }
    }

    [Fact]
    public void An_empty_surface_produces_no_output_size()
    {
        Assert.Equal((0, 0), FrameScaler.ResolveOutputSize(0, 0, CaptureResolution.Native));
        Assert.Equal((0, 0), FrameScaler.ResolveOutputSize(1920, 0, CaptureResolution.Native));
    }

    [Fact]
    public unsafe void Scaling_produces_a_correctly_sized_I420_buffer()
    {
        const int sourceWidth = 640;
        const int sourceHeight = 480;
        const int stride = sourceWidth * 4;

        var source = new byte[stride * sourceHeight];
        Array.Fill(source, (byte)128);

        fixed (byte* pointer = source)
        {
            var scaled = FrameScaler.BgraToI420((nint)pointer, stride, sourceWidth, sourceHeight, 320, 240);

            Assert.Equal(320 * 240 * 3 / 2, scaled.Length);
        }
    }

    [Fact]
    public unsafe void A_solid_colour_survives_downscaling()
    {
        const int sourceWidth = 64;
        const int sourceHeight = 64;
        const int stride = sourceWidth * 4;

        // Solid mid-blue: B=200, G=30, R=30.
        var source = new byte[stride * sourceHeight];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 200;
            source[i + 1] = 30;
            source[i + 2] = 30;
            source[i + 3] = 255;
        }

        fixed (byte* pointer = source)
        {
            var full = FrameScaler.BgraToI420((nint)pointer, stride, sourceWidth, sourceHeight, 64, 64);
            var half = FrameScaler.BgraToI420((nint)pointer, stride, sourceWidth, sourceHeight, 32, 32);

            // Box averaging a uniform image must give the same luma at any scale.
            Assert.Equal(full[0], half[0]);
            Assert.All(half[..(32 * 32)], luma => Assert.Equal(full[0], luma));

            // Chroma of a blue field must sit clearly above the neutral 128 on U.
            var uPlane = half[(32 * 32)..((32 * 32) + (16 * 16))];
            Assert.All(uPlane, u => Assert.True(u > 140, $"expected blue chroma, got U={u}"));
        }
    }

    [Fact]
    public unsafe void Low_latency_scaling_preserves_solid_colour_and_output_shape()
    {
        const int sourceWidth = 64;
        const int sourceHeight = 64;
        const int outputWidth = 32;
        const int outputHeight = 32;
        const int stride = sourceWidth * 4;

        var source = new byte[stride * sourceHeight];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 200;
            source[i + 1] = 30;
            source[i + 2] = 30;
            source[i + 3] = 255;
        }

        fixed (byte* pointer = source)
        {
            var quality = FrameScaler.BgraToI420(
                (nint)pointer, stride, sourceWidth, sourceHeight, outputWidth, outputHeight);
            var lowLatency = FrameScaler.BgraToI420LowLatency(
                (nint)pointer, stride, sourceWidth, sourceHeight, outputWidth, outputHeight);

            Assert.Equal(outputWidth * outputHeight * 3 / 2, lowLatency.Length);
            Assert.Equal(quality, lowLatency);
        }
    }

    [Theory]
    [InlineData(31, 32)]
    [InlineData(32, 31)]
    public unsafe void Low_latency_scaling_rejects_odd_I420_dimensions(int width, int height)
    {
        var source = new byte[64 * 64 * 4];
        fixed (byte* pointer = source)
        {
            var address = (nint)pointer;
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FrameScaler.BgraToI420LowLatency(address, 64 * 4, 64, 64, width, height));
        }
    }

    [Fact]
    public unsafe void Downscaling_averages_rather_than_dropping_pixels()
    {
        const int sourceWidth = 4;
        const int sourceHeight = 4;
        const int stride = sourceWidth * 4;

        // Checkerboard of black and white.
        var source = new byte[stride * sourceHeight];
        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < sourceWidth; x++)
            {
                var value = (byte)((x + y) % 2 == 0 ? 0 : 255);
                var offset = (y * stride) + (x * 4);
                source[offset] = value;
                source[offset + 1] = value;
                source[offset + 2] = value;
                source[offset + 3] = 255;
            }
        }

        fixed (byte* pointer = source)
        {
            var half = FrameScaler.BgraToI420((nint)pointer, stride, sourceWidth, sourceHeight, 2, 2);

            // Each output pixel averages one black and one white source pixel, so the luma
            // must land in the middle rather than at either extreme.
            for (var i = 0; i < 4; i++)
            {
                Assert.InRange(half[i], 100, 160);
            }
        }
    }
}

[Collection(WindowsGraphicsCaptureCollection.Name)]
public class CaptureResolutionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Capturing_at_720p_really_delivers_720p_frames()
    {
        Assert.True(WindowsGraphicsCaptureSource.IsSupported);

        var target = DisplayEnumerator.ListCaptureTargets()[0];
        output.WriteLine($"source display: {target.Width}x{target.Height}");

        await using var capture = new WindowsGraphicsCaptureSource();

        CapturedFrame? sample = null;
        capture.FrameArrived += (_, frame) => sample ??= frame;

        await capture.StartAsync(new CaptureRequest
        {
            Target = target,
            Resolution = CaptureResolution.P720,
            MaxFramesPerSecond = 30,
        });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (sample is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        await capture.StopAsync();

        Assert.NotNull(sample);
        output.WriteLine($"encoder input: {sample!.Width}x{sample.Height}");

        var (expectedWidth, expectedHeight) =
            FrameScaler.ResolveOutputSize(target.Width, target.Height, CaptureResolution.P720);

        Assert.Equal(expectedHeight, sample.Height);
        Assert.Equal(expectedWidth, sample.Width);
        Assert.Equal(sample.Width * sample.Height * 3 / 2, sample.I420.Length);
    }

    [Fact]
    public async Task The_downscale_factor_shrinks_frames_without_restarting_capture()
    {
        Assert.True(WindowsGraphicsCaptureSource.IsSupported);

        var target = DisplayEnumerator.ListCaptureTargets()[0];
        await using var capture = new WindowsGraphicsCaptureSource();

        CapturedFrame? beforeSample = null;
        CapturedFrame? afterSample = null;
        var downscaled = false;

        capture.FrameArrived += (_, frame) =>
        {
            if (!downscaled)
            {
                beforeSample ??= frame;
                return;
            }

            // A frame already in flight when the factor changed still carries the old size,
            // so wait for the first frame that was actually converted at the new scale.
            if (beforeSample is not null && frame.Width < beforeSample.Width)
            {
                afterSample ??= frame;
            }
        };

        await capture.StartAsync(new CaptureRequest
        {
            Target = target,
            Resolution = CaptureResolution.P720,
            MaxFramesPerSecond = 30,
        });

        Assert.Equal(1, capture.DownscaleFactor);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (beforeSample is null && DateTime.UtcNow < deadline)
        {
            ScreenActivity.Nudge();
            await Task.Delay(100);
        }

        Assert.NotNull(beforeSample);

        downscaled = true;
        capture.SetDownscaleFactor(2);
        Assert.Equal(2, capture.DownscaleFactor);

        deadline = DateTime.UtcNow.AddSeconds(20);
        while (afterSample is null && DateTime.UtcNow < deadline)
        {
            ScreenActivity.Nudge();
            await Task.Delay(100);
        }

        await capture.StopAsync();

        Assert.NotNull(afterSample);
        output.WriteLine($"before={beforeSample!.Width}x{beforeSample.Height} " +
                         $"after={afterSample!.Width}x{afterSample.Height}");

        Assert.True(afterSample.Width < beforeSample.Width);
        Assert.True(afterSample.Height < beforeSample.Height);
        Assert.Equal(afterSample.Width * afterSample.Height * 3 / 2, afterSample.I420.Length);
    }

    [Fact]
    public void The_downscale_factor_is_clamped_to_supported_steps()
    {
        var capture = new WindowsGraphicsCaptureSource();

        capture.SetDownscaleFactor(0);
        Assert.Equal(1, capture.DownscaleFactor);

        capture.SetDownscaleFactor(2);
        Assert.Equal(2, capture.DownscaleFactor);

        capture.SetDownscaleFactor(3);
        Assert.Equal(4, capture.DownscaleFactor);

        capture.SetDownscaleFactor(64);
        Assert.Equal(4, capture.DownscaleFactor);
    }

    [Fact]
    public void Available_targets_are_reported_for_the_monitor_selector()
    {
        var capture = new WindowsGraphicsCaptureSource();

        var targets = capture.AvailableTargets;

        Assert.NotEmpty(targets);
        Assert.All(targets, t => Assert.Equal(CaptureTargetKind.Display, t.Kind));
    }
}
