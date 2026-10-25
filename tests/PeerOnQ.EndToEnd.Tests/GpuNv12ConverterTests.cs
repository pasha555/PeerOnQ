using System.Diagnostics;
using PeerOnQ.Media.Codecs;
using PeerOnQ.Platform.Windows.Capture;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class GpuNv12ConverterTests(ITestOutputHelper output)
{
    [HardwareVideoTheory]
    [InlineData(3840, 2160, 3840, 2160)]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(2560, 1440, 1920, 1080)]
    [InlineData(1920, 1080, 1920, 1080)]
    public unsafe void Gpu_conversion_matches_the_cpu_reference_and_is_fast(
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out var device).CheckError();
        using var _ = device;
        using var context = device!.ImmediateContext;
        using var converter = D3D11Nv12Converter.TryCreate(device, context);
        Assert.NotNull(converter);

        var stride = sourceWidth * 4;
        var bgra = SmoothDesktop(sourceWidth, sourceHeight);
        ID3D11Texture2D texture;
        fixed (byte* pixels = bgra)
        {
            texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)sourceWidth,
                Height = (uint)sourceHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            }, [new SubresourceData(pixels, (uint)stride)]);
        }
        using var __ = texture;

        var nv12 = converter!.Convert(texture, sourceWidth, sourceHeight, targetWidth, targetHeight);
        Assert.Equal(targetWidth * targetHeight * 3 / 2, nv12.Length);
        var began = Stopwatch.GetTimestamp();
        const int iterations = 10;
        for (var index = 0; index < iterations; index++)
            nv12 = converter.Convert(texture, sourceWidth, sourceHeight, targetWidth, targetHeight);
        var milliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds / iterations;

        byte[] expected;
        fixed (byte* pixels = bgra)
            expected = FrameScaler.BgraToI420((nint)pixels, stride, sourceWidth, sourceHeight, targetWidth, targetHeight);
        var actual = Nv12Planes.ToI420(targetWidth, targetHeight, nv12);
        var luma = targetWidth * targetHeight;
        var (lumaMean, lumaMax, lumaOutliers) = Difference(expected.AsSpan(0, luma), actual.AsSpan(0, luma));
        var (chromaMean, chromaMax, chromaOutliers) = Difference(expected.AsSpan(luma), actual.AsSpan(luma));
        output.WriteLine($"GPU {sourceWidth}x{sourceHeight}->{targetWidth}x{targetHeight}: {milliseconds:F2} ms/frame; "
            + $"luma mean/max/outliers {lumaMean:F2}/{lumaMax}/{lumaOutliers:P3}; "
            + $"chroma mean/max/outliers {chromaMean:F2}/{chromaMax}/{chromaOutliers:P3}");

        // Both converters use the same BT.601 studio-swing matrix, so smooth regions agree within
        // rounding. The CPU box filter and the GPU's scaling filter legitimately differ on the
        // hard block edges of this fixture; those are bounded to a small share of the pixels.
        Assert.InRange(lumaMean, 0, 1.0);
        Assert.InRange(chromaMean, 0, 1.5);
        Assert.InRange(lumaOutliers, 0, 0.01);
        Assert.InRange(chromaOutliers, 0, 0.02);
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
            Assert.InRange(lumaMax, 0, 3);
    }

    private static byte[] SmoothDesktop(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var offset = (row * width + column) * 4;
                var block = (column / 256 + row / 256) % 3;
                bgra[offset] = (byte)(block == 0 ? 230 : column * 255 / width);
                bgra[offset + 1] = (byte)(block == 1 ? 200 : row * 255 / height);
                bgra[offset + 2] = (byte)(block == 2 ? 60 : (column + row) * 255 / (width + height));
                bgra[offset + 3] = 255;
            }
        return bgra;
    }

    private static (double Mean, int Max, double OutlierShare) Difference(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        long total = 0;
        long outliers = 0;
        var maximum = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            var difference = Math.Abs(expected[index] - actual[index]);
            total += difference;
            if (difference > 6) outliers++;
            if (difference > maximum) maximum = difference;
        }
        return ((double)total / expected.Length, maximum, (double)outliers / expected.Length);
    }
}

public sealed class AbsolutePointerCoordinateTests
{
    [Theory]
    [InlineData(1366)]
    [InlineData(1920)]
    [InlineData(2560)]
    [InlineData(3840)]
    [InlineData(5120)]
    [InlineData(7680)]
    [InlineData(11520)]
    public void Every_pixel_round_trips_through_the_windows_absolute_mapping(int virtualExtent)
    {
        for (var pixel = 0; pixel < virtualExtent; pixel++)
        {
            var absolute = PeerOnQ.Platform.Windows.Input.WindowsInputController.ToAbsoluteCoordinate(pixel, virtualExtent);
            Assert.InRange(absolute, 0, 65_535);
            // win32k: pixel = normalized * extent / 65536, truncated.
            Assert.Equal(pixel, (int)((long)absolute * virtualExtent / 65_536));
        }
    }

    [Fact]
    public void Out_of_range_offsets_are_clamped_to_the_virtual_desktop()
    {
        Assert.Equal(0, PeerOnQ.Platform.Windows.Input.WindowsInputController.ToAbsoluteCoordinate(-5, 3840));
        Assert.Equal(
            PeerOnQ.Platform.Windows.Input.WindowsInputController.ToAbsoluteCoordinate(3839, 3840),
            PeerOnQ.Platform.Windows.Input.WindowsInputController.ToAbsoluteCoordinate(5000, 3840));
    }
}
