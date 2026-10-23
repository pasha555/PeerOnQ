using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PeerOnQ.Media.Codecs;

/// <summary>
/// Converts decoded 4:2:0 images directly into packed opaque BGRA32, the Windows viewer's back
/// buffer layout. This removes the BGR24 intermediate and the per-pixel expansion that otherwise
/// runs on the UI thread, where it delays pointer and keyboard handling at 4K.
///
/// Each decoder keeps the colour arithmetic of the BGR24 converter it replaces:
///   * NV12 (H.264) uses the BT.601 studio-swing integer matrix of the Media Foundation decoder
///     path, byte-identical per channel: 298(Y-16) with 516/100/208/409 chroma weights.
///   * I420 (VP8) uses the viewer's legacy coefficients (B = Y + 2.302U, G = Y - 0.395U - 0.581V,
///     R = Y + 1.140V) in 16.16 fixed point, within one level of the double-precision original.
/// The vector and scalar paths share each integer definition exactly.
/// </summary>
internal static class YuvBgraConverter
{
    internal const int BlueU = 150864;  // round(2.302 * 65536)
    internal const int GreenU = 25887;  // round(0.395 * 65536)
    internal const int GreenV = 38076;  // round(0.581 * 65536)
    internal const int RedV = 74711;    // round(1.140 * 65536)
    private const uint OpaqueAlpha = 0xFF000000u;

    private static readonly ParallelOptions ConversionOptions = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
    };

    internal static bool VectorizedConversionSupported => Vector128.IsHardwareAccelerated;

    public static int OutputLength(int width, int height) => checked(width * height * 4);

    /// <summary>Converts a tightly packed VP8 I420 image into <paramref name="destination"/>.</summary>
    public static void ConvertI420(byte[] source, int width, int height, byte[] destination) =>
        ConvertI420(source, width, height, destination, VectorizedConversionSupported);

    internal static unsafe void ConvertI420(byte[] source, int width, int height, byte[] destination, bool vectorized)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!VideoCodecLimits.ValidDimensions(width, height)
            || source.Length != (long)width * height * 3 / 2)
            throw new ArgumentException("A complete bounded even-sized I420 image is required.", nameof(source));
        if (destination.Length != OutputLength(width, height))
            throw new ArgumentException("A complete BGRA32 destination is required.", nameof(destination));
        var luma = width * height;
        fixed (byte* sourcePointer = source)
        fixed (byte* targetPointer = destination)
        {
            Convert(width, height,
                (nint)sourcePointer, width,
                (nint)(sourcePointer + luma), (nint)(sourcePointer + luma + luma / 4), width / 2, 1,
                (nint)targetPointer, vectorized, studioRange: false);
        }
    }

    /// <summary>Converts an H.264 decoder NV12 image with its native stride and buffer height.</summary>
    public static void ConvertNv12(DecodedNv12Frame frame, byte[] destination) =>
        ConvertNv12(frame, destination, VectorizedConversionSupported);

    internal static unsafe void ConvertNv12(DecodedNv12Frame frame, byte[] destination, bool vectorized)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(destination);
        if (!frame.HasValidLayout)
            throw new ArgumentException("A complete bounded NV12 image is required.", nameof(frame));
        if (destination.Length != OutputLength(frame.Width, frame.Height))
            throw new ArgumentException("A complete BGRA32 destination is required.", nameof(destination));
        var chromaOffset = (long)frame.Stride * frame.BufferHeight;
        fixed (byte* sourcePointer = frame.Pixels)
        fixed (byte* targetPointer = destination)
        {
            Convert(frame.Width, frame.Height,
                (nint)sourcePointer, frame.Stride,
                (nint)(sourcePointer + chromaOffset), (nint)(sourcePointer + chromaOffset + 1), frame.Stride, 2,
                (nint)targetPointer, vectorized, studioRange: true);
        }
    }

    private static unsafe void Convert(
        int width, int height,
        nint lumaAddress, int lumaStride,
        nint uAddress, nint vAddress, int chromaStride, int chromaStep,
        nint targetAddress, bool vectorized, bool studioRange)
    {
        // Each worker owns disjoint output row pairs; the pinned inputs are immutable here.
        void ConvertRows(int pair)
        {
            var row = pair * 2;
            var yTop = (byte*)lumaAddress + (long)row * lumaStride;
            var yBottom = yTop + lumaStride;
            var u = (byte*)uAddress + (long)pair * chromaStride;
            var v = (byte*)vAddress + (long)pair * chromaStride;
            var outTop = (uint*)targetAddress + (long)row * width;
            var outBottom = outTop + width;
            var column = !vectorized
                ? 0
                : chromaStep == 1
                    ? ConvertPlanarVector(yTop, yBottom, u, v, outTop, outBottom, width, studioRange)
                    : ConvertInterleavedVector(yTop, yBottom, u, outTop, outBottom, width, studioRange);
            for (; column < width; column += 2)
            {
                var chroma = (column / 2) * chromaStep;
                var uValue = u[chroma] - 128;
                var vValue = v[chroma] - 128;
                var (blue, green, red) = Offsets(uValue, vValue, studioRange);
                outTop[column] = Pixel(yTop[column], blue, green, red, studioRange);
                outTop[column + 1] = Pixel(yTop[column + 1], blue, green, red, studioRange);
                outBottom[column] = Pixel(yBottom[column], blue, green, red, studioRange);
                outBottom[column + 1] = Pixel(yBottom[column + 1], blue, green, red, studioRange);
            }
        }

        if ((long)width * height >= 1920 * 1080)
            Parallel.For(0, height / 2, ConversionOptions, ConvertRows);
        else
            for (var pair = 0; pair < height / 2; pair++) ConvertRows(pair);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int Blue, int Green, int Red) Offsets(int u, int v, bool studioRange) => studioRange
        ? (516 * u, -100 * u - 208 * v, 409 * v)
        : ((u * BlueU) >> 16, -(u * GreenU + v * GreenV) >> 16, (v * RedV) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Pixel(int y, int blue, int green, int red, bool studioRange)
    {
        if (studioRange)
        {
            var scaled = 298 * Math.Max(0, y - 16) + 128;
            return (uint)Clamp((scaled + blue) >> 8)
                   | ((uint)Clamp((scaled + green) >> 8) << 8)
                   | ((uint)Clamp((scaled + red) >> 8) << 16)
                   | OpaqueAlpha;
        }
        return (uint)Clamp(y + blue) | ((uint)Clamp(y + green) << 8) | ((uint)Clamp(y + red) << 16) | OpaqueAlpha;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;

    /// <summary>Eight pixels per step from separate U and V planes; returns the first unconverted column.</summary>
    private static unsafe int ConvertPlanarVector(
        byte* yTop, byte* yBottom, byte* u, byte* v, uint* outTop, uint* outBottom, int width, bool studioRange)
    {
        var column = 0;
        for (; column <= width - 8; column += 8)
        {
            var chroma = column / 2;
            var uValues = WidenFour(Unsafe.ReadUnaligned<uint>(u + chroma)) - Vector128.Create(128);
            var vValues = WidenFour(Unsafe.ReadUnaligned<uint>(v + chroma)) - Vector128.Create(128);
            WriteEight(yTop + column, yBottom + column, outTop + column, outBottom + column, uValues, vValues, studioRange);
        }
        return column;
    }

    /// <summary>Eight pixels per step from an interleaved NV12 UV plane.</summary>
    private static unsafe int ConvertInterleavedVector(
        byte* yTop, byte* yBottom, byte* uv, uint* outTop, uint* outBottom, int width, bool studioRange)
    {
        var column = 0;
        for (; column <= width - 8; column += 8)
        {
            // Four little-endian UV pairs read as 16-bit (U | V << 8) values, widened to 32 bits.
            var pairs = Vector128.WidenLower(
                Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(uv + column)).AsUInt16());
            var packed = pairs.AsInt32();
            var uValues = (packed & Vector128.Create(0xFF)) - Vector128.Create(128);
            var vValues = Vector128.ShiftRightLogical(packed, 8) - Vector128.Create(128);
            WriteEight(yTop + column, yBottom + column, outTop + column, outBottom + column, uValues, vValues, studioRange);
        }
        return column;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> WidenFour(uint fourBytes) =>
        Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalar(fourBytes).AsByte())).AsInt32();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteEight(
        byte* yTop, byte* yBottom, uint* outTop, uint* outBottom, Vector128<int> u, Vector128<int> v, bool studioRange)
    {
        Vector128<int> blue, green, red;
        if (studioRange)
        {
            blue = u * 516;
            green = -(u * 100) - v * 208;
            red = v * 409;
        }
        else
        {
            blue = Vector128.ShiftRightArithmetic(u * BlueU, 16);
            green = Vector128.ShiftRightArithmetic(-(u * GreenU + v * GreenV), 16);
            red = Vector128.ShiftRightArithmetic(v * RedV, 16);
        }
        var lowIndex = Vector128.Create(0, 0, 1, 1);
        var highIndex = Vector128.Create(2, 2, 3, 3);
        var blueLow = Vector128.Shuffle(blue, lowIndex);
        var greenLow = Vector128.Shuffle(green, lowIndex);
        var redLow = Vector128.Shuffle(red, lowIndex);
        var blueHigh = Vector128.Shuffle(blue, highIndex);
        var greenHigh = Vector128.Shuffle(green, highIndex);
        var redHigh = Vector128.Shuffle(red, highIndex);
        WriteFour(yTop, outTop, blueLow, greenLow, redLow, studioRange);
        WriteFour(yTop + 4, outTop + 4, blueHigh, greenHigh, redHigh, studioRange);
        WriteFour(yBottom, outBottom, blueLow, greenLow, redLow, studioRange);
        WriteFour(yBottom + 4, outBottom + 4, blueHigh, greenHigh, redHigh, studioRange);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteFour(
        byte* luma, uint* target, Vector128<int> blue, Vector128<int> green, Vector128<int> red, bool studioRange)
    {
        var y = WidenFour(Unsafe.ReadUnaligned<uint>(luma));
        var zero = Vector128<int>.Zero;
        var maximum = Vector128.Create(255);
        Vector128<int> b, g, r;
        if (studioRange)
        {
            var scaled = Vector128.Max(y - Vector128.Create(16), zero) * 298 + Vector128.Create(128);
            b = Vector128.ShiftRightArithmetic(scaled + blue, 8);
            g = Vector128.ShiftRightArithmetic(scaled + green, 8);
            r = Vector128.ShiftRightArithmetic(scaled + red, 8);
        }
        else
        {
            b = y + blue;
            g = y + green;
            r = y + red;
        }
        b = Vector128.Min(Vector128.Max(b, zero), maximum);
        g = Vector128.Min(Vector128.Max(g, zero), maximum);
        r = Vector128.Min(Vector128.Max(r, zero), maximum);
        var pixels = b | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(r, 16)
                     | Vector128.Create(unchecked((int)OpaqueAlpha));
        Unsafe.WriteUnaligned(target, pixels);
    }
}
