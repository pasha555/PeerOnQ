using System.Runtime.Versioning;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Platform.Windows.Capture;

/// <summary>
/// Turns a captured BGRA surface into planar I420 at the requested output size.
///
/// Downscaling uses box averaging over the source rectangle that maps to each output pixel,
/// which is cheap and avoids the shimmering that nearest-neighbour produces on text.
/// Upscaling is never performed: asking for 1080p on a 720p display keeps 720p.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FrameScaler
{
    /// <summary>
    /// Resolves the encoder input size for a capture surface.
    /// Output dimensions are always even, because I420 subsamples chroma 2x2.
    /// </summary>
    public static (int Width, int Height) ResolveOutputSize(
        int sourceWidth,
        int sourceHeight,
        CaptureResolution resolution)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (0, 0);

        var targetHeight = resolution switch
        {
            CaptureResolution.P720 => 720,
            CaptureResolution.P1080 => 1080,
            CaptureResolution.P1440 => 1440,
            CaptureResolution.P2160 => 2160,
            CaptureResolution.Native => sourceHeight,

            // Automatic keeps native up to 1080p and scales anything larger down to it.
            CaptureResolution.Automatic => Math.Min(sourceHeight, 1080),
            _ => sourceHeight,
        };

        // Never upscale: it costs bandwidth and adds no detail.
        targetHeight = Math.Min(targetHeight, sourceHeight);

        var scale = (double)targetHeight / sourceHeight;
        var targetWidth = (int)Math.Round(sourceWidth * scale);

        targetWidth = Math.Max(2, targetWidth - (targetWidth % 2));
        targetHeight = Math.Max(2, targetHeight - (targetHeight % 2));

        return (targetWidth, targetHeight);
    }

    /// <summary>
    /// BT.601 BGRA to planar I420, resampling to <paramref name="dstWidth"/> x
    /// <paramref name="dstHeight"/> in the same pass.
    /// </summary>
    public static unsafe byte[] BgraToI420(
        nint source,
        int stride,
        int srcWidth,
        int srcHeight,
        int dstWidth,
        int dstHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dstWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dstHeight);

        // Native-resolution capture is the latency-sensitive common path. Avoid the generic
        // floating-point box scaler when no resize is requested; it otherwise limits a 4K
        // desktop to single-digit frame rates before VP8 encoding even begins.
        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            return BgraToI420Native(source, stride, srcWidth, srcHeight);
        }

        var ySize = dstWidth * dstHeight;
        var chromaSize = ySize / 4;
        var output = new byte[ySize + (chromaSize * 2)];

        var src = (byte*)source;
        var xRatio = (double)srcWidth / dstWidth;
        var yRatio = (double)srcHeight / dstHeight;

        fixed (byte* dst = output)
        {
            var yPlane = dst;
            var uPlane = dst + ySize;
            var vPlane = uPlane + chromaSize;

            for (var row = 0; row < dstHeight; row++)
            {
                var srcRowStart = (int)(row * yRatio);
                var srcRowEnd = Math.Min(srcHeight, Math.Max(srcRowStart + 1, (int)((row + 1) * yRatio)));

                for (var col = 0; col < dstWidth; col++)
                {
                    var srcColStart = (int)(col * xRatio);
                    var srcColEnd = Math.Min(srcWidth, Math.Max(srcColStart + 1, (int)((col + 1) * xRatio)));

                    SampleBox(src, stride, srcRowStart, srcRowEnd, srcColStart, srcColEnd,
                        out var b, out var g, out var r);

                    yPlane[(row * dstWidth) + col] =
                        (byte)Math.Clamp(((66 * r) + (129 * g) + (25 * b) + 128 >> 8) + 16, 0, 255);
                }
            }

            for (var row = 0; row < dstHeight; row += 2)
            {
                var srcRowStart = (int)(row * yRatio);
                var srcRowEnd = Math.Min(srcHeight, Math.Max(srcRowStart + 1, (int)((row + 2) * yRatio)));

                for (var col = 0; col < dstWidth; col += 2)
                {
                    var srcColStart = (int)(col * xRatio);
                    var srcColEnd = Math.Min(srcWidth, Math.Max(srcColStart + 1, (int)((col + 2) * xRatio)));

                    SampleBox(src, stride, srcRowStart, srcRowEnd, srcColStart, srcColEnd,
                        out var b, out var g, out var r);

                    var index = (row / 2 * (dstWidth / 2)) + (col / 2);
                    uPlane[index] = (byte)Math.Clamp(((-38 * r) - (74 * g) + (112 * b) + 128 >> 8) + 128, 0, 255);
                    vPlane[index] = (byte)Math.Clamp(((112 * r) - (94 * g) - (18 * b) + 128 >> 8) + 128, 0, 255);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// Converts and downsamples with one source lookup per destination luma pixel. This path is
    /// reserved for the explicit low-latency profile; quality-oriented profiles keep box averaging.
    /// </summary>
    public static unsafe byte[] BgraToI420LowLatency(
        nint source,
        int stride,
        int srcWidth,
        int srcHeight,
        int dstWidth,
        int dstHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(srcWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(srcHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dstWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dstHeight);
        if ((dstWidth & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(dstWidth), "I420 output width must be even.");
        if ((dstHeight & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(dstHeight), "I420 output height must be even.");

        if (srcWidth == dstWidth && srcHeight == dstHeight)
        {
            return BgraToI420Native(source, stride, srcWidth, srcHeight);
        }

        var ySize = checked(dstWidth * dstHeight);
        var chromaSize = ySize / 4;
        var output = new byte[checked(ySize + (chromaSize * 2))];
        var src = (byte*)source;

        fixed (byte* dst = output)
        {
            var yPlane = dst;
            var uPlane = dst + ySize;
            var vPlane = uPlane + chromaSize;

            for (var row = 0; row < dstHeight; row++)
            {
                var sourceRow = Math.Min(srcHeight - 1, (int)((long)row * srcHeight / dstHeight));
                var line = src + ((long)sourceRow * stride);

                for (var col = 0; col < dstWidth; col++)
                {
                    var sourceCol = Math.Min(srcWidth - 1, (int)((long)col * srcWidth / dstWidth));
                    var pixel = line + ((long)sourceCol * 4);
                    var b = pixel[0];
                    var g = pixel[1];
                    var r = pixel[2];

                    yPlane[((long)row * dstWidth) + col] =
                        (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                }
            }

            for (var row = 0; row < dstHeight; row += 2)
            {
                var sourceRow0 = Math.Min(srcHeight - 1, (int)((long)row * srcHeight / dstHeight));
                var sourceRow1 = Math.Min(srcHeight - 1, (int)((long)(row + 1) * srcHeight / dstHeight));
                var line0 = src + ((long)sourceRow0 * stride);
                var line1 = src + ((long)sourceRow1 * stride);

                for (var col = 0; col < dstWidth; col += 2)
                {
                    var sourceCol0 = Math.Min(srcWidth - 1, (int)((long)col * srcWidth / dstWidth));
                    var sourceCol1 = Math.Min(srcWidth - 1, (int)((long)(col + 1) * srcWidth / dstWidth));
                    var topLeft = line0 + ((long)sourceCol0 * 4);
                    var topRight = line0 + ((long)sourceCol1 * 4);
                    var bottomLeft = line1 + ((long)sourceCol0 * 4);
                    var bottomRight = line1 + ((long)sourceCol1 * 4);

                    var b = (topLeft[0] + topRight[0] + bottomLeft[0] + bottomRight[0]) >> 2;
                    var g = (topLeft[1] + topRight[1] + bottomLeft[1] + bottomRight[1]) >> 2;
                    var r = (topLeft[2] + topRight[2] + bottomLeft[2] + bottomRight[2]) >> 2;

                    var index = ((long)(row / 2) * (dstWidth / 2)) + (col / 2);
                    uPlane[index] =
                        (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                    vPlane[index] =
                        (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                }
            }
        }

        return output;
    }

    private static unsafe byte[] BgraToI420Native(nint source, int stride, int width, int height)
    {
        var ySize = width * height;
        var chromaSize = ySize / 4;
        var output = new byte[ySize + (chromaSize * 2)];
        var src = (byte*)source;

        fixed (byte* dst = output)
        {
            var yPlane = dst;
            var uPlane = dst + ySize;
            var vPlane = uPlane + chromaSize;

            for (var row = 0; row < height; row++)
            {
                var line = src + ((long)row * stride);

                for (var col = 0; col < width; col++)
                {
                    var pixel = line + ((long)col * 4);
                    var b = pixel[0];
                    var g = pixel[1];
                    var r = pixel[2];

                    yPlane[((long)row * width) + col] =
                        (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 0, 255);
                }
            }

            for (var row = 0; row < height; row += 2)
            {
                var line0 = src + ((long)row * stride);
                var line1 = src + ((long)(row + 1) * stride);

                for (var col = 0; col < width; col += 2)
                {
                    var topLeft = line0 + ((long)col * 4);
                    var topRight = topLeft + 4;
                    var bottomLeft = line1 + ((long)col * 4);
                    var bottomRight = bottomLeft + 4;

                    var b = (topLeft[0] + topRight[0] + bottomLeft[0] + bottomRight[0]) >> 2;
                    var g = (topLeft[1] + topRight[1] + bottomLeft[1] + bottomRight[1]) >> 2;
                    var r = (topLeft[2] + topRight[2] + bottomLeft[2] + bottomRight[2]) >> 2;

                    var index = ((long)(row / 2) * (width / 2)) + (col / 2);
                    uPlane[index] =
                        (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                    vPlane[index] =
                        (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                }
            }
        }

        return output;
    }

    private static unsafe void SampleBox(
        byte* src, int stride,
        int rowStart, int rowEnd, int colStart, int colEnd,
        out int b, out int g, out int r)
    {
        long sumB = 0, sumG = 0, sumR = 0;
        var count = 0;

        for (var y = rowStart; y < rowEnd; y++)
        {
            var line = src + ((long)y * stride);

            for (var x = colStart; x < colEnd; x++)
            {
                var pixel = line + ((long)x * 4);
                sumB += pixel[0];
                sumG += pixel[1];
                sumR += pixel[2];
                count++;
            }
        }

        if (count == 0)
        {
            b = g = r = 0;
            return;
        }

        b = (int)(sumB / count);
        g = (int)(sumG / count);
        r = (int)(sumR / count);
    }
}
