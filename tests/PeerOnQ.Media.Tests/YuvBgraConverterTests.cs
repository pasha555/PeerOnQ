using PeerOnQ.Media.Codecs;
using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class YuvBgraConverterTests
{
    [Theory]
    [InlineData(2, 2)]
    [InlineData(6, 4)]
    [InlineData(8, 2)]
    [InlineData(18, 6)]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]
    public void Vector_and_scalar_I420_paths_are_byte_identical(int width, int height)
    {
        var source = RandomBytes(width * height * 3 / 2, width * 31 + height);
        var vector = new byte[YuvBgraConverter.OutputLength(width, height)];
        var scalar = new byte[vector.Length];

        YuvBgraConverter.ConvertI420(source, width, height, vector, vectorized: YuvBgraConverter.VectorizedConversionSupported);
        YuvBgraConverter.ConvertI420(source, width, height, scalar, vectorized: false);

        Assert.Equal(scalar, vector);
        AssertOpaque(vector);
    }

    [Theory]
    [InlineData(2, 2, 2, 2)]
    [InlineData(10, 6, 16, 8)]
    [InlineData(30, 6, 32, 8)]
    [InlineData(1920, 1080, 2048, 1088)]
    public void Vector_and_scalar_NV12_paths_honor_stride_and_buffer_height(
        int width, int height, int stride, int bufferHeight)
    {
        var pixels = RandomBytes(stride * bufferHeight * 3 / 2, width + stride);
        var frame = new DecodedNv12Frame(width, height, stride, bufferHeight, pixels);
        var vector = new byte[YuvBgraConverter.OutputLength(width, height)];
        var scalar = new byte[vector.Length];

        YuvBgraConverter.ConvertNv12(frame, vector, vectorized: YuvBgraConverter.VectorizedConversionSupported);
        YuvBgraConverter.ConvertNv12(frame, scalar, vectorized: false);

        Assert.Equal(scalar, vector);
        AssertOpaque(vector);
    }

    [Fact]
    public void Every_NV12_sample_matches_the_studio_swing_H264_decoder_arithmetic_exactly()
    {
        // 512x512 NV12 holds every (U, V) pair once; 64 luma offsets cover all 256 Y values.
        const int width = 512, height = 512;
        var luma = width * height;
        var nv12 = new byte[luma * 3 / 2];
        for (var pair = 0; pair < 65_536; pair++)
        {
            nv12[luma + 2 * pair] = (byte)(pair >> 8);
            nv12[luma + 2 * pair + 1] = (byte)pair;
        }
        var frame = new DecodedNv12Frame(width, height, width, height, nv12);
        var bgra = new byte[YuvBgraConverter.OutputLength(width, height)];
        for (var offset = 0; offset < 64; offset++)
        {
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                    nv12[row * width + column] = (byte)(offset + ((row & 1) * 2 + (column & 1)) * 64);
            YuvBgraConverter.ConvertNv12(frame, bgra);
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    // The Media Foundation decoder's original scalar BT.601 studio-swing formula.
                    var y = Math.Max(0, nv12[row * width + column] - 16);
                    var chroma = luma + row / 2 * width + (column & ~1);
                    var u = nv12[chroma] - 128;
                    var v = nv12[chroma + 1] - 128;
                    var pixel = (row * width + column) * 4;
                    Assert.Equal((byte)Math.Clamp((298 * y + 516 * u + 128) >> 8, 0, 255), bgra[pixel]);
                    Assert.Equal((byte)Math.Clamp((298 * y - 100 * u - 208 * v + 128) >> 8, 0, 255), bgra[pixel + 1]);
                    Assert.Equal((byte)Math.Clamp((298 * y + 409 * v + 128) >> 8, 0, 255), bgra[pixel + 2]);
                    Assert.Equal(255, bgra[pixel + 3]);
                }
        }
    }

    [Fact]
    public void Studio_swing_black_white_and_red_map_to_full_range_pixels()
    {
        // H.264 frames are studio swing: Y 16 is black, Y 235 is white, (81, 90, 240) is red.
        var black = ConvertSolidNv12(16, 128, 128);
        var white = ConvertSolidNv12(235, 128, 128);
        var red = ConvertSolidNv12(81, 90, 240);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, black);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, white);
        Assert.InRange(red[2], (byte)250, (byte)255);
        Assert.InRange(red[1], (byte)0, (byte)5);
        Assert.InRange(red[0], (byte)0, (byte)5);
    }

    private static byte[] ConvertSolidNv12(byte y, byte u, byte v)
    {
        const int width = 16, height = 2;
        var nv12 = new byte[width * height * 3 / 2];
        Array.Fill(nv12, y, 0, width * height);
        for (var index = width * height; index < nv12.Length; index += 2)
        {
            nv12[index] = u;
            nv12[index + 1] = v;
        }
        var bgra = new byte[YuvBgraConverter.OutputLength(width, height)];
        YuvBgraConverter.ConvertNv12(new DecodedNv12Frame(width, height, width, height, nv12), bgra);
        return bgra[..4];
    }

    [Fact]
    public void Every_chroma_pair_stays_within_one_level_of_the_legacy_BGR_converter()
    {
        // 512x512 I420 holds exactly 65,536 chroma samples: every (U, V) pair once. Each 2x2
        // luma block carries four spread Y values; 64 offsets cover all 256 Y values per pair.
        const int width = 512, height = 512;
        var luma = width * height;
        var source = new byte[luma * 3 / 2];
        for (var pair = 0; pair < 65_536; pair++)
        {
            source[luma + pair] = (byte)(pair >> 8);
            source[luma + luma / 4 + pair] = (byte)pair;
        }
        var bgra = new byte[YuvBgraConverter.OutputLength(width, height)];
        var maximumDifference = 0;
        for (var offset = 0; offset < 64; offset++)
        {
            for (var row = 0; row < height; row++)
                for (var column = 0; column < width; column++)
                {
                    var corner = (row & 1) * 2 + (column & 1);
                    source[row * width + column] = (byte)(offset + corner * 64);
                }
            YuvBgraConverter.ConvertI420(source, width, height, bgra);
            var legacy = I420BgrConverter.Convert(source, width, height);
            for (var pixel = 0; pixel < luma; pixel++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var difference = Math.Abs(bgra[pixel * 4 + channel] - legacy[pixel * 3 + channel]);
                    if (difference > maximumDifference) maximumDifference = difference;
                }
                Assert.Equal(255, bgra[pixel * 4 + 3]);
            }
        }

        Assert.InRange(maximumDifference, 0, 1);
    }

    [Fact]
    public void Invalid_inputs_are_rejected_before_any_write()
    {
        Assert.Throws<ArgumentException>(() =>
            YuvBgraConverter.ConvertI420(new byte[5], 2, 2, new byte[16]));
        Assert.Throws<ArgumentException>(() =>
            YuvBgraConverter.ConvertI420(new byte[6], 2, 2, new byte[15]));
        Assert.Throws<ArgumentException>(() =>
            YuvBgraConverter.ConvertNv12(new DecodedNv12Frame(2, 2, 1, 2, new byte[6]), new byte[16]));
    }

    [Fact]
    public void Presentation_pool_reuses_returned_buffers_and_clears_them_on_close_and_resize()
    {
        var pool = new FrameBufferPool(capacity: 2);
        var first = pool.Rent(64);
        Array.Fill(first, (byte)7);
        pool.Return(first);
        pool.Return(first);
        Assert.Equal(1, pool.RetainedBuffers);
        Assert.Same(first, pool.Rent(64));

        pool.Return(first);
        var resized = pool.Rent(128);
        Assert.NotSame(first, resized);
        Assert.Equal(0, pool.RetainedBuffers);
        Assert.All(first, value => Assert.Equal(0, value));

        Array.Fill(resized, (byte)9);
        pool.Return(resized);
        pool.Close();
        Assert.Equal(0, pool.RetainedBuffers);
        Assert.All(resized, value => Assert.Equal(0, value));
        Assert.NotSame(resized, pool.Rent(128));
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static void AssertOpaque(byte[] bgra)
    {
        for (var index = 3; index < bgra.Length; index += 4) Assert.Equal(255, bgra[index]);
    }
}
