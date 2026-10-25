using System.Buffers.Binary;
using System.Runtime.Intrinsics.X86;
using PeerOnQ.Platform.Windows.Capture;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class BgraI420ConversionTests(ITestOutputHelper output)
{
    [Fact]
    public unsafe void Every_rgb_triple_in_uniform_two_by_two_blocks_matches_exact_integer_planes()
    {
        const int width = 256, height = 64, stride = width * 4 + 13;
        var source = new byte[stride * height + 36];
        Array.Fill(source, (byte)0xa5);
        var pixels = source.AsSpan(17, stride * height);
        fixed (byte* beginning = source)
        {
            for (var colorBase = 0; colorBase < 1 << 24; colorBase += 4096)
            {
                for (var block = 0; block < 4096; block++)
                {
                    var row = block / (width / 2) * 2;
                    var column = block % (width / 2) * 2;
                    var position = row * stride + column * 4;
                    var bgra = 0xff000000u | (uint)(colorBase + block);
                    BinaryPrimitives.WriteUInt32LittleEndian(pixels.Slice(position, 4), bgra);
                    BinaryPrimitives.WriteUInt32LittleEndian(pixels.Slice(position + 4, 4), bgra);
                    BinaryPrimitives.WriteUInt32LittleEndian(pixels.Slice(position + stride, 4), bgra);
                    BinaryPrimitives.WriteUInt32LittleEndian(pixels.Slice(position + stride + 4, 4), bgra);
                }
                var expected = ScalarPlanes(pixels, stride, width, height);
                var actual = FrameScaler.BgraToI420((nint)(beginning + 17), stride, width, height, width, height);
                Assert.True(expected.AsSpan().SequenceEqual(actual), $"Exact RGB planes differ in batch{colorBase}.");
            }
        }
        AssertGuards(source, stride * height);
        output.WriteLine($"All16,777,216 RGB triples matched exact integer I420; Avx2.IsSupported={Avx2.IsSupported}.");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(14)]
    [InlineData(18)]
    [InlineData(1366)]
    public unsafe void Nonuniform_chroma_alpha_unaligned_padding_and_vector_tails_are_exact(int width)
    {
        const int height = 14;
        var random = new Random(0x10218 + width);
        foreach (var padding in new[] { 0, 1, 13 })
        {
            var stride = width * 4 + padding;
            var source = new byte[stride * height + 36];
            Array.Fill(source, (byte)0xa5);
            var pixels = source.AsSpan(17, stride * height);
            random.NextBytes(pixels);
            var unchanged = source.ToArray();
            var expected = ScalarPlanes(pixels, stride, width, height);
            byte[] actual;
            byte[] lowLatency;
            fixed (byte* beginning = source)
            {
                actual = FrameScaler.BgraToI420((nint)(beginning + 17), stride, width, height, width, height);
                lowLatency = FrameScaler.BgraToI420LowLatency((nint)(beginning + 17), stride, width, height, width, height);
            }
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Native mismatch at width{width}, padding{padding}.");
            Assert.True(expected.AsSpan().SequenceEqual(lowLatency), $"Low-latency native mismatch at width{width}, padding{padding}.");
            Assert.True(unchanged.AsSpan().SequenceEqual(source), "Input pixels, alpha and row padding must remain unchanged.");
            AssertGuards(source, stride * height);
        }
    }

    [Fact]
    public unsafe void Complete_native_frames_have_independent_owned_arrays()
    {
        const int width = 18, height = 6, stride = width * 4 + 3;
        var source = new byte[stride * height];
        new Random(0x518).NextBytes(source);
        var expected = ScalarPlanes(source, stride, width, height);
        byte[] first;
        byte[] second;
        fixed (byte* input = source)
        {
            first = FrameScaler.BgraToI420((nint)input, stride, width, height, width, height);
            second = FrameScaler.BgraToI420((nint)input, stride, width, height, width, height);
        }
        Assert.NotSame(first, second);
        Assert.Equal(width * height * 3 / 2, first.Length);
        first[0] ^= 0xff;
        Assert.True(expected.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public unsafe void Intrinsics_guard_requires_runtime_support_and_complete_positive_stride_geometry()
    {
        var source = new byte[8 * 4 * 2];
        fixed (byte* input = source)
        {
            Assert.Equal(Avx2.IsSupported && Sse2.IsSupported,
                BgraI420Avx2Converter.CanConvert((nint)input, 32, 8, 2));
            Assert.False(BgraI420Avx2Converter.CanConvert(0, 32, 8, 2));
            Assert.False(BgraI420Avx2Converter.CanConvert((nint)input, 31, 8, 2));
            Assert.False(BgraI420Avx2Converter.CanConvert((nint)input, 32, 7, 2));
            Assert.False(BgraI420Avx2Converter.CanConvert((nint)input, 32, 8, 1));
            Assert.False(BgraI420Avx2Converter.CanConvert((nint)input, 32, 0, 2));
        }
        output.WriteLine($"Runtime guard: Avx2.IsSupported={Avx2.IsSupported}; Sse2.IsSupported={Sse2.IsSupported}.");
    }

    private static void AssertGuards(byte[] source, int pixelRegionLength)
    {
        Assert.All(source.Take(17), item => Assert.Equal(0xa5, item));
        Assert.All(source.Skip(17 + pixelRegionLength), item => Assert.Equal(0xa5, item));
    }

    private static byte[] ScalarPlanes(ReadOnlySpan<byte> pixels, int stride, int width, int height)
    {
        var plane = width * height;
        var expected = new byte[plane * 3 / 2];
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
        {
            var index = row * stride + column * 4;
            expected[row * width + column] = (byte)Math.Clamp(
                ((66 * pixels[index + 2] + 129 * pixels[index + 1] + 25 * pixels[index] + 128) >> 8) + 16, 0, 255);
        }
        for (var row = 0; row < height; row += 2)
        for (var column = 0; column < width; column += 2)
        {
            var tl = row * stride + column * 4;
            var tr = tl + 4;
            var bl = tl + stride;
            var br = bl + 4;
            var b = (pixels[tl] + pixels[tr] + pixels[bl] + pixels[br]) >> 2;
            var g = (pixels[tl + 1] + pixels[tr + 1] + pixels[bl + 1] + pixels[br + 1]) >> 2;
            var r = (pixels[tl + 2] + pixels[tr + 2] + pixels[bl + 2] + pixels[br + 2]) >> 2;
            var chroma = row / 2 * (width / 2) + column / 2;
            expected[plane + chroma] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
            expected[plane + plane / 4 + chroma] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
        }
        return expected;
    }
}
