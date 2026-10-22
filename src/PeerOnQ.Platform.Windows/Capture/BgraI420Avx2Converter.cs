using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PeerOnQ.Platform.Windows.Capture;

// Exact integer BT.601 native-resolution conversion on one core, with caller-owned output.
internal static class BgraI420Avx2Converter
{
    private static readonly Vector256<int> ByteMask = Vector256.Create(255);
    private static readonly Vector256<int> Round = Vector256.Create(128);
    private static readonly Vector256<int> YOffset = Vector256.Create(16);
    private static readonly Vector256<int> PairOrder = Vector256.Create(0, 1, 4, 5, 0, 1, 4, 5);
    private static readonly Vector256<int> YR = Vector256.Create(66);
    private static readonly Vector256<int> YG = Vector256.Create(129);
    private static readonly Vector256<int> YB = Vector256.Create(25);
    private static readonly Vector256<int> UR = Vector256.Create(-38);
    private static readonly Vector256<int> UG = Vector256.Create(-74);
    private static readonly Vector256<int> UB = Vector256.Create(112);
    private static readonly Vector256<int> VR = Vector256.Create(112);
    private static readonly Vector256<int> VG = Vector256.Create(-94);
    private static readonly Vector256<int> VB = Vector256.Create(-18);

    internal static bool CanConvert(nint source, int stride, int width, int height) =>
        Avx2.IsSupported && Sse2.IsSupported && source != 0 && width > 0 && height > 0
        && (width & 1) == 0 && (height & 1) == 0 && stride >= (long)width * 4
        && (long)width * height <= int.MaxValue * 2L / 3;

    internal static unsafe byte[] Convert(nint source, int stride, int width, int height)
    {
        if (!CanConvert(source, stride, width, height))
            throw new ArgumentException("A supported complete even-sized BGRA surface is required.");
        var ySize = checked(width * height);
        var outputLength = checked(ySize + ySize / 2);

        // Keep the current fresh zeroed owned-buffer contract; no pool/lease change.
        var output = new byte[outputLength];
        var input = (byte*)source;
        fixed (byte* destination = output)
        {
            var uPlane = destination + ySize;
            var vPlane = uPlane + ySize / 4;
            for (var row = 0; row < height; row += 2)
            {
                var top = input + (long)row * stride;
                var bottom = top + stride;
                var yTop = destination + (long)row * width;
                var yBottom = yTop + width;
                var uRow = uPlane + (long)(row / 2) * (width / 2);
                var vRow = vPlane + (long)(row / 2) * (width / 2);
                var column = 0;
                // Exactly32 input bytes from each row. No vector load crosses visible pixels.
                for (; column <= width - 8; column += 8)
                {
                    var topPixels = Unsafe.ReadUnaligned<Vector256<int>>(top + (long)column * 4);
                    var bottomPixels = Unsafe.ReadUnaligned<Vector256<int>>(bottom + (long)column * 4);
                    var tb = Avx2.And(topPixels, ByteMask);
                    var tg = Avx2.And(Avx2.ShiftRightLogical(topPixels.AsUInt32(), 8).AsInt32(), ByteMask);
                    var tr = Avx2.And(Avx2.ShiftRightLogical(topPixels.AsUInt32(), 16).AsInt32(), ByteMask);
                    var bb = Avx2.And(bottomPixels, ByteMask);
                    var bg = Avx2.And(Avx2.ShiftRightLogical(bottomPixels.AsUInt32(), 8).AsInt32(), ByteMask);
                    var br = Avx2.And(Avx2.ShiftRightLogical(bottomPixels.AsUInt32(), 16).AsInt32(), ByteMask);
                    var topY = Avx2.Add(Avx2.ShiftRightArithmetic(Weighted(tr, tg, tb, YR, YG, YB), 8), YOffset);
                    var bottomY = Avx2.Add(Avx2.ShiftRightArithmetic(Weighted(br, bg, bb, YR, YG, YB), 8), YOffset);
                    StoreEightBytes(yTop + column, topY);
                    StoreEightBytes(yBottom + column, bottomY);

                    // Average CHANNELS over2x2 first, exactly as the scalar reference.
                    // Averaging already-converted chroma instead would change rounding.
                    var b = AverageBlocks(tb, bb);
                    var g = AverageBlocks(tg, bg);
                    var r = AverageBlocks(tr, br);
                    var u = Avx2.Add(Avx2.ShiftRightArithmetic(Weighted(r, g, b, UR, UG, UB), 8), Round);
                    var v = Avx2.Add(Avx2.ShiftRightArithmetic(Weighted(r, g, b, VR, VG, VB), 8), Round);
                    StoreFourBytes(uRow + column / 2, u);
                    StoreFourBytes(vRow + column / 2, v);
                }

                // Width is even, so the remaining0/2/4/6 pixels form complete2x2 blocks.
                for (; column < width; column += 2)
                {
                    var tl = top + (long)column * 4;
                    var tRight = tl + 4;
                    var bl = bottom + (long)column * 4;
                    var bRight = bl + 4;
                    yTop[column] = Luma(tl);
                    yTop[column + 1] = Luma(tRight);
                    yBottom[column] = Luma(bl);
                    yBottom[column + 1] = Luma(bRight);
                    var b = (tl[0] + tRight[0] + bl[0] + bRight[0]) >> 2;
                    var g = (tl[1] + tRight[1] + bl[1] + bRight[1]) >> 2;
                    var r = (tl[2] + tRight[2] + bl[2] + bRight[2]) >> 2;
                    uRow[column / 2] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                    vRow[column / 2] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                }
            }
        }
        return output;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Weighted(Vector256<int> r, Vector256<int> g, Vector256<int> b,
        Vector256<int> rc, Vector256<int> gc, Vector256<int> bc) =>
        Avx2.Add(Avx2.Add(Avx2.Add(Avx2.MultiplyLow(r, rc), Avx2.MultiplyLow(g, gc)), Avx2.MultiplyLow(b, bc)), Round);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> AverageBlocks(Vector256<int> top, Vector256<int> bottom)
    {
        var vertical = Avx2.Add(top, bottom); // each lane0..510
        // HADD is128-bit-lane local: [01,23,01,23 |45,67,45,67].
        var horizontal = Avx2.HorizontalAdd(vertical, vertical);
        var averaged = Avx2.ShiftRightLogical(horizontal.AsUInt32(), 2).AsInt32();
        // Low4 lanes become the four unique2x2 blocks in source order.
        return Avx2.PermuteVar8x32(averaged, PairOrder);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void StoreEightBytes(byte* destination, Vector256<int> values)
    {
        // All exact scalar Y results are16..235, U/V16..240. Saturating packs therefore
        // preserve Math.Clamp(...,0,255) exactly; all intermediate sums fit signed32bits.
        var words = Sse2.PackSignedSaturate(values.GetLower(), values.GetUpper());
        var bytes = Sse2.PackUnsignedSaturate(words, Vector128<short>.Zero);
        Unsafe.WriteUnaligned(destination, bytes.AsUInt64().GetElement(0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void StoreFourBytes(byte* destination, Vector256<int> values)
    {
        var words = Sse2.PackSignedSaturate(values.GetLower(), Vector128<int>.Zero);
        var bytes = Sse2.PackUnsignedSaturate(words, Vector128<short>.Zero);
        Unsafe.WriteUnaligned(destination, bytes.AsUInt32().GetElement(0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe byte Luma(byte* pixel) =>
        (byte)Math.Clamp(((66 * pixel[2] + 129 * pixel[1] + 25 * pixel[0] + 128) >> 8) + 16, 0, 255);
}
