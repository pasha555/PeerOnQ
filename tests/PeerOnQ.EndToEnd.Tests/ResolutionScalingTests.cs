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
        Assert.Equal(CaptureResolution.P1080, lowBandwidth.Resolution);
        Assert.Equal(15, lowBandwidth.TargetFps);
        Assert.True(lowBandwidth.MaxBitrateKbps < office.MaxBitrateKbps);
    }

    [Theory]
    [InlineData(1920, 1080, CaptureResolution.Native, 1920, 1080)]
    [InlineData(3840, 2160, CaptureResolution.Native, 3840, 2160)]
    [InlineData(1920, 1080, CaptureResolution.Automatic, 1920, 1080)]
    [InlineData(3840, 2160, CaptureResolution.Automatic, 3840, 2160)]
    [InlineData(5120, 2880, CaptureResolution.Automatic, 3840, 2160)]
    [InlineData(2560, 1440, CaptureResolution.Automatic, 1920, 1080)]
    [InlineData(2160, 3840, CaptureResolution.Automatic, 1080, 1920)]
    [InlineData(2160, 3840, CaptureResolution.P2160, 1080, 1920)]
    [InlineData(5120, 2160, CaptureResolution.Automatic, 2560, 1080)]
    [InlineData(7680, 1080, CaptureResolution.Automatic, 7680, 1080)]
    [InlineData(4320, 7680, CaptureResolution.P2160, 1080, 1920)]
    [InlineData(1920, 1080, CaptureResolution.P720, 1920, 1080)]
    [InlineData(3840, 2160, CaptureResolution.P720, 1920, 1080)]
    [InlineData(3840, 2160, CaptureResolution.P1080, 1920, 1080)]
    [InlineData(3840, 2160, CaptureResolution.P1440, 1920, 1080)]
    [InlineData(7680, 4320, CaptureResolution.P2160, 3840, 2160)]
    [InlineData(1920, 1080, CaptureResolution.P2160, 1920, 1080)]
    [InlineData(2560, 1600, CaptureResolution.P720, 1728, 1080)]
    [InlineData(3440, 1440, CaptureResolution.Native, 2580, 1080)]
    [InlineData(1440, 3440, CaptureResolution.P1440, 1080, 2580)]
    [InlineData(2160, 5120, CaptureResolution.P2160, 1080, 2560)]
    public void Output_size_streams_4k_sources_natively_and_every_other_source_at_the_1080p_short_edge(
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
    [InlineData(1280, 720, CaptureResolution.P2160)]
    [InlineData(720, 1280, CaptureResolution.Native)]
    [InlineData(1024, 768, CaptureResolution.Automatic)]
    public void A_small_display_is_never_upscaled(int width, int height, CaptureResolution resolution)
    {
        var (outputWidth, outputHeight) = FrameScaler.ResolveOutputSize(width, height, resolution);

        Assert.True(outputWidth <= width);
        Assert.True(outputHeight <= height);
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(2160, 3840)]
    [InlineData(5120, 2160)]
    [InlineData(1440, 3440)]
    [InlineData(7680, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1919, 1079)]
    public void Every_resolution_choice_preserves_aspect_and_never_exceeds_its_tier_or_source(
        int sourceWidth, int sourceHeight)
    {
        foreach (var resolution in Enum.GetValues<CaptureResolution>())
            foreach (var factor in new[] { 1, 2, 4 })
            {
                var (width, height) = FrameScaler.ResolveOutputSize(sourceWidth, sourceHeight, resolution, factor);

                Assert.InRange(width, 2, sourceWidth);
                Assert.InRange(height, 2, sourceHeight);
                var ultraHd = factor == 1
                              && CaptureResolutionPolicy.AllowsUltraHd(resolution)
                              && CaptureResolutionPolicy.IsUltraHdSource(sourceWidth, sourceHeight);
                Assert.InRange(Math.Min(width, height), 2, ultraHd ? 2160 : 1080);
                Assert.InRange(width, 2, ultraHd ? CaptureResolutionPolicy.MaximumUltraHdWidth : int.MaxValue);
                Assert.Equal(0, width % 2);
                Assert.Equal(0, height % 2);
                // Rounding to an even I420 dimension may lose at most two source pixels
                // of cross-product precision; it must not crop or stretch the display.
                var aspectError = Math.Abs((long)width * sourceHeight - (long)height * sourceWidth);
                Assert.InRange(aspectError, 0L, 2L * Math.Max(sourceWidth, sourceHeight));
            }
    }

    [Theory]
    [InlineData(1920, 1080, CaptureResolution.Automatic, 4, 1920, 1080)]
    [InlineData(3840, 2160, CaptureResolution.Automatic, 4, 1920, 1080)]
    [InlineData(2160, 3840, CaptureResolution.Automatic, 4, 1080, 1920)]
    [InlineData(3840, 2160, CaptureResolution.Native, 4, 1920, 1080)]
    [InlineData(2160, 3840, CaptureResolution.Native, 4, 1080, 1920)]
    [InlineData(3840, 2160, CaptureResolution.P2160, 2, 1920, 1080)]
    [InlineData(2160, 3840, CaptureResolution.P2160, 4, 1080, 1920)]
    [InlineData(2560, 1440, CaptureResolution.P1440, 2, 1920, 1080)]
    [InlineData(1080, 1920, CaptureResolution.Automatic, 4, 1080, 1920)]
    [InlineData(1280, 720, CaptureResolution.Automatic, 4, 1280, 720)]
    public void Congestion_preserves_the_capture_floor_without_upscaling(
        int sourceWidth, int sourceHeight, CaptureResolution resolution, int factor,
        int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight),
            FrameScaler.ResolveOutputSize(sourceWidth, sourceHeight, resolution, factor));
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

    [Theory]
    [InlineData(2, 2, 0)]
    [InlineData(6, 4, 4)]
    [InlineData(10, 6, 32)]
    [InlineData(1920, 1080, 0)]
    [InlineData(3840, 2160, 64)]
    [InlineData(2160, 3840, 64)]
    public unsafe void Native_conversion_preserves_exact_scalar_planes_and_padded_input(int width, int height, int padding)
    {
        var stride = width * 4 + padding;
        var source = Enumerable.Range(0, stride * height).Select(index => (byte)(index * 37 + 17)).ToArray();
        var original = source.ToArray();
        var expected = ReferenceNativeConversion(source, stride, width, height);
        byte[] actual;
        fixed (byte* pointer = source)
            actual = FrameScaler.BgraToI420((nint)pointer, stride, width, height, width, height);
        Assert.Equal(width * height * 3 / 2, actual.Length);
        Assert.NotSame(source, actual);
        Assert.True(expected.AsSpan().SequenceEqual(actual), $"Native planes differ at {width}x{height}, stride {stride}.");
        Assert.Equal(original, source);
    }

    private static byte[] ReferenceNativeConversion(byte[] source, int stride, int width, int height)
    {
        var ySize = width * height;
        var output = new byte[ySize * 3 / 2];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var pixel = row * stride + column * 4;
                var b = source[pixel]; var g = source[pixel + 1]; var r = source[pixel + 2];
                output[row * width + column] = (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
            }
        for (var row = 0; row < height; row += 2)
            for (var column = 0; column < width; column += 2)
            {
                var top = row * stride + column * 4;
                var bottom = top + stride;
                var b = (source[top] + source[top + 4] + source[bottom] + source[bottom + 4]) >> 2;
                var g = (source[top + 1] + source[top + 5] + source[bottom + 1] + source[bottom + 5]) >> 2;
                var r = (source[top + 2] + source[top + 6] + source[bottom + 2] + source[bottom + 6]) >> 2;
                var chroma = row / 2 * (width / 2) + column / 2;
                output[ySize + chroma] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                output[ySize + ySize / 4 + chroma] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
            }
        return output;
    }

}

[Collection(WindowsGraphicsCaptureCollection.Name)]
public class CaptureResolutionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Legacy_720p_capture_requests_preserve_the_1080p_floor()
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
        Assert.True(Math.Min(sample.Width, sample.Height) >= Math.Min(1080,
            Math.Min(target.Width & ~1, target.Height & ~1)));
        Assert.True(Math.Min(sample.Width, sample.Height) <= 1080);
        Assert.Equal(sample.Width * sample.Height * 3 / 2, sample.I420.Length);
    }

    [Fact]
    public async Task Adaptive_downscaling_preserves_the_1080p_floor_without_restarting_capture()
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

            // Capture is already bounded at the 1080p floor/cap. An adaptive request
            // must keep producing fresh frames without restarting or shrinking below it.
            if (beforeSample is not null &&
                frame.SequenceNumber > beforeSample.SequenceNumber + 1)
            {
                afterSample ??= frame;
            }
        };

        await capture.StartAsync(new CaptureRequest
        {
            Target = target,
            Resolution = CaptureResolution.Native,
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

        var sourceShortEdge = Math.Min(beforeSample.Width, beforeSample.Height);
        Assert.True(sourceShortEdge <= 1080);
        Assert.InRange(Math.Min(afterSample.Width, afterSample.Height), Math.Min(1080, sourceShortEdge), sourceShortEdge);
        Assert.Equal(beforeSample.Width, afterSample.Width);
        Assert.Equal(beforeSample.Height, afterSample.Height);
        Assert.True(afterSample.SequenceNumber > beforeSample.SequenceNumber);
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

public sealed class AllDisplaysLayoutTests
{
    [Fact]
    public void The_composed_view_covers_every_display_including_negative_offsets()
    {
        var displays = new[]
        {
            new DisplayTarget(1, @"\.\DISPLAY1", "Primary", 0, 0, 3840, 2160, true),
            new DisplayTarget(2, @"\.\DISPLAY2", "Left", -1920, 600, 1920, 1080, false),
            new DisplayTarget(3, @"\.\DISPLAY3", "Top", 0, -1440, 2560, 1440, false),
        };

        var all = DisplayEnumerator.VirtualDesktop(displays)!;

        Assert.Equal(DisplayEnumerator.AllDisplaysId, all.DeviceName);
        Assert.Equal((-1920, -1440, 5760, 3600), (all.X, all.Y, all.Width, all.Height));
        // A large composition is judged by its pixels: 5760x3600 is a 4K-class canvas (3456x2160),
        // while two 1080p displays side by side stay on the 1080 tier at 3840x1080.
        Assert.True(CaptureResolutionPolicy.IsUltraHdSource(all.Width, all.Height));
        Assert.Equal((3840, 1080), FrameScaler.ResolveOutputSize(3840, 1080, CaptureResolution.Automatic));
        var (width, height) = FrameScaler.ResolveOutputSize(all.Width, all.Height, CaptureResolution.Automatic);
        var (fittedWidth, fittedHeight) = FrameScaler.FitWithinHardwareWindow(width, height);
        Assert.InRange(fittedWidth, 2, CaptureResolutionPolicy.MaximumUltraHdWidth);
        Assert.InRange(fittedHeight, 2, CaptureResolutionPolicy.MaximumUltraHdHeight);
        Assert.Equal(0, fittedWidth % 2);
        Assert.Equal(0, fittedHeight % 2);
    }

    [Theory]
    [InlineData(5760, 1080, 4096, 768)]
    [InlineData(3840, 1080, 3840, 1080)]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1728, 1080, 1728, 1080)]
    public void Wide_compositions_fit_the_hardware_encoder_without_distortion(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), FrameScaler.FitWithinHardwareWindow(width, height));
    }

    [Fact]
    public void A_single_display_machine_offers_no_composed_target()
    {
        var targets = DisplayEnumerator.ListCaptureTargets();
        var displays = DisplayEnumerator.ListDisplays();
        Assert.Equal(displays.Count > 1, targets.Any(target => DisplayEnumerator.IsAllDisplays(target.Id)));
        if (displays.Count == 1) Assert.Null(DisplayEnumerator.FindByDeviceName(DisplayEnumerator.AllDisplaysId));
    }
}
