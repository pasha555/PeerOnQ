using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.App.Linux;

internal static class FramePixelConverter
{
    private const int MaximumDimension = 16_384;
    private const int MaximumPixels = 33_554_432;

    public static bool TryConvertToBgra(RemoteVideoFrame frame, out byte[] pixels)
    {
        pixels = [];
        if (frame.Width <= 0 || frame.Height <= 0
            || frame.Width > MaximumDimension || frame.Height > MaximumDimension)
        {
            return false;
        }

        int pixelCount;
        try
        {
            pixelCount = checked(frame.Width * frame.Height);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (pixelCount > MaximumPixels) return false;

        return frame.Format switch
        {
            RemotePixelFormat.Bgr24 => TryConvertBgr24(frame.Pixels.Span, pixelCount, out pixels),
            RemotePixelFormat.I420 => TryConvertI420(frame.Pixels.Span, frame.Width, frame.Height, pixelCount, out pixels),
            _ => false,
        };
    }

    private static bool TryConvertBgr24(ReadOnlySpan<byte> source, int pixelCount, out byte[] pixels)
    {
        pixels = [];
        int expectedLength;
        try
        {
            expectedLength = checked(pixelCount * 3);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (source.Length != expectedLength) return false;
        pixels = GC.AllocateUninitializedArray<byte>(checked(pixelCount * 4));
        for (var sourceIndex = 0; sourceIndex < source.Length; sourceIndex += 3)
        {
            var destinationIndex = sourceIndex / 3 * 4;
            pixels[destinationIndex] = source[sourceIndex];
            pixels[destinationIndex + 1] = source[sourceIndex + 1];
            pixels[destinationIndex + 2] = source[sourceIndex + 2];
            pixels[destinationIndex + 3] = byte.MaxValue;
        }

        return true;
    }

    private static bool TryConvertI420(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int pixelCount,
        out byte[] pixels)
    {
        pixels = [];
        if ((width & 1) != 0 || (height & 1) != 0) return false;
        var chromaLength = pixelCount / 4;
        if (source.Length != pixelCount + (chromaLength * 2)) return false;

        var uOffset = pixelCount;
        var vOffset = pixelCount + chromaLength;
        pixels = GC.AllocateUninitializedArray<byte>(checked(pixelCount * 4));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = (y * width) + x;
                var chromaIndex = ((y / 2) * (width / 2)) + (x / 2);
                var luminance = Math.Max(0, source[pixelIndex] - 16);
                var blueDifference = source[uOffset + chromaIndex] - 128;
                var redDifference = source[vOffset + chromaIndex] - 128;

                var red = Clamp((298 * luminance + 409 * redDifference + 128) >> 8);
                var green = Clamp((298 * luminance - 100 * blueDifference - 208 * redDifference + 128) >> 8);
                var blue = Clamp((298 * luminance + 516 * blueDifference + 128) >> 8);
                var destinationIndex = pixelIndex * 4;
                pixels[destinationIndex] = blue;
                pixels[destinationIndex + 1] = green;
                pixels[destinationIndex + 2] = red;
                pixels[destinationIndex + 3] = byte.MaxValue;
            }
        }

        return true;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
}
