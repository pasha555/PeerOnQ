using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeerOnQ.Media.Codecs;

/// <summary>Preserves existing BGR color arithmetic, sharing chroma work across each I420 2x2 block.</summary>
internal static class I420BgrConverter
{
    private static readonly Vector128<byte> BgrShuffle = Vector128.Create(
        (byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 128, 128, 128, 128);
    private static bool VectorizedConversionSupported =>
        Avx2.IsSupported && Avx.IsSupported && Sse41.IsSupported && Ssse3.IsSupported && Sse2.IsSupported;

    private static readonly ParallelOptions ConversionOptions = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
    };

    public static unsafe byte[] Convert(byte[] source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!VideoCodecLimits.ValidDimensions(width, height)
            || source.Length != (long)width * height * 3 / 2)
            throw new ArgumentException("A complete bounded even-sized I420 image is required.", nameof(source));
        var ySize = width * height;
        var stride = width * 3;
        // Every packed pixel is written below before this buffer can be returned.
        var target = GC.AllocateUninitializedArray<byte>(checked(stride * height));
        fixed (byte* sourcePointer = source)
        fixed (byte* targetPointer = target)
        {
            // Parallel.For completes before the pins leave scope. Only disjoint output rows
            // are written; chroma is immutable and belongs to this independently decoded frame.
            var sourceAddress = (nint)sourcePointer;
            var targetAddress = (nint)targetPointer;
            void ConvertRows(int pair)
            {
                var input = (byte*)sourceAddress;
                var output = (byte*)targetAddress;
                var row = pair * 2;
                var top = input + row * width;
                var bottom = top + width;
                var destinationTop = output + row * stride;
                var destinationBottom = destinationTop + stride;
                var chroma = pair * (width / 2);
                var column = 0;
                if (VectorizedConversionSupported)
                {
                    for (; column <= width - 4; column += 4, chroma += 2)
                    {
                        var u0 = input[ySize + chroma] - 128;
                        var u1 = input[ySize + chroma + 1] - 128;
                        var v0 = input[ySize + ySize / 4 + chroma] - 128;
                        var v1 = input[ySize + ySize / 4 + chroma + 1] - 128;
                        var u = Avx.ConvertToVector256Double(Vector128.Create(u0, u0, u1, u1));
                        var v = Avx.ConvertToVector256Double(Vector128.Create(v0, v0, v1, v1));
                        var blue = Avx.Multiply(u, Vector256.Create(2.302));
                        var greenU = Avx.Multiply(u, Vector256.Create(0.395));
                        var greenV = Avx.Multiply(v, Vector256.Create(0.581));
                        var red = Avx.Multiply(v, Vector256.Create(1.140));
                        WriteFourPixels(destinationTop + column * 3, top + column, blue, greenU, greenV, red);
                        WriteFourPixels(destinationBottom + column * 3, bottom + column, blue, greenU, greenV, red);
                    }
                }
                for (; column < width; column += 2)
                {
                    var u = input[ySize + chroma] - 128;
                    var v = input[ySize + ySize / 4 + chroma++] - 128;
                    var blue = 2.302 * u;
                    var greenU = 0.395 * u;
                    var greenV = 0.581 * v;
                    var red = 1.140 * v;
                    var offset = column * 3;
                    WritePixel(destinationTop + offset, top[column], blue, greenU, greenV, red);
                    WritePixel(destinationTop + offset + 3, top[column + 1], blue, greenU, greenV, red);
                    WritePixel(destinationBottom + offset, bottom[column], blue, greenU, greenV, red);
                    WritePixel(destinationBottom + offset + 3, bottom[column + 1], blue, greenU, greenV, red);
                }
            }
            if ((long)width * height >= 1920 * 1080)
                Parallel.For(0, height / 2, ConversionOptions, ConvertRows);
            else for (var pair = 0; pair < height / 2; pair++) ConvertRows(pair);
        }
        return target;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WritePixel(byte* target, int y, double blue, double greenU, double greenV, double red)
    {
        // Keep operation order and truncation identical to the existing converter, including
        // saturated colors. Rows are packed, as required by RemoteVideoFrame and the renderers.
        target[0] = (byte)Math.Clamp((int)(y + blue), 0, 255);
        target[1] = (byte)Math.Clamp((int)(y - greenU - greenV), 0, 255);
        target[2] = (byte)Math.Clamp((int)(y + red), 0, 255);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WriteFourPixels(byte* target, byte* source,
        Vector256<double> blue, Vector256<double> greenU, Vector256<double> greenV, Vector256<double> red)
    {
        // Separate double operations retain scalar rounding/order; conversion truncates toward zero.
        var y = Avx.ConvertToVector256Double(Sse41.ConvertToVector128Int32(
            Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(source)).AsByte()));
        var b = Avx.ConvertToVector128Int32WithTruncation(Avx.Add(y, blue));
        var g = Avx.ConvertToVector128Int32WithTruncation(Avx.Subtract(Avx.Subtract(y, greenU), greenV));
        var r = Avx.ConvertToVector128Int32WithTruncation(Avx.Add(y, red));
        var maximum = Vector128.Create(255);
        b = Sse41.Min(Sse41.Max(b, Vector128<int>.Zero), maximum);
        g = Sse41.Min(Sse41.Max(g, Vector128<int>.Zero), maximum);
        r = Sse41.Min(Sse41.Max(r, Vector128<int>.Zero), maximum);
        var bgra = Sse2.Or(Sse2.Or(b, Sse2.ShiftLeftLogical(g, 8)), Sse2.ShiftLeftLogical(r, 16));
        var packed = Ssse3.Shuffle(bgra.AsByte(), BgrShuffle);
        // Exactly twelve packed bytes, including the last four pixels of an unpadded row.
        Unsafe.WriteUnaligned(target, packed.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(target + 8, packed.AsUInt32().GetElement(2));
    }
}
