using PeerOnQ.Media.Codecs;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using Xunit;

namespace PeerOnQ.Media.Tests;

[Collection("Real WebRTC acceptance")]
public sealed class Vp8ScreenDecoderTests
{
    [Theory]
    [InlineData(320, 240)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void Parallel_conversion_preserves_every_pixel_and_interframe_state(int width, int height)
    {
        using var encoder = new Vp8ScreenEncoder(36_000, 30);
        using var expectedDecoder = new VpxVideoEncoder();
        using var decoder = new Vp8ScreenDecoder();

        for (var phase = 0; phase < 3; phase++)
        {
            var encoded = encoder.EncodeI420(width, height, CreatePattern(width, height, phase));
            Assert.NotEmpty(encoded);
            Assert.Equal(phase == 0 ? 0 : 1, encoded[0] & 1);
            AssertSameFrame(expectedDecoder, decoder, encoded, width, height);
        }
    }

    [Fact]
    public void Resolution_changes_remain_decodable_with_the_same_decoder()
    {
        using var encoder = new Vp8ScreenEncoder(12_000, 30);
        using var expectedDecoder = new VpxVideoEncoder();
        using var decoder = new Vp8ScreenDecoder();

        foreach (var (width, height) in new[] { (640, 360), (1920, 1080), (320, 240) })
        {
            var encoded = encoder.EncodeI420(width, height, CreatePattern(width, height, 0));
            AssertSameFrame(expectedDecoder, decoder, encoded, width, height);
        }
    }

    [Fact]
    public void Disposing_the_decoder_is_idempotent_and_rejects_further_frames()
    {
        using var encoder = new Vp8ScreenEncoder(4_000, 30);
        var decoder = new Vp8ScreenDecoder();
        var encoded = encoder.EncodeI420(320, 240, CreatePattern(320, 240, 0));
        Assert.Single(decoder.Decode(encoded));

        decoder.Dispose();
        decoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => decoder.Decode(encoded));
    }

    private static void AssertSameFrame(
        VpxVideoEncoder expectedDecoder,
        Vp8ScreenDecoder decoder,
        byte[] encoded,
        int width,
        int height)
    {
        var expected = Assert.Single(expectedDecoder.DecodeVideo(encoded, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8));
        var actual = Assert.Single(decoder.Decode(encoded));
        Assert.Equal((uint)width, actual.Width);
        Assert.Equal((uint)height, actual.Height);
        Assert.True(expected.Sample.AsSpan().SequenceEqual(actual.Sample), "Parallel BGR output differs from the existing decoder.");
    }

    private static byte[] CreatePattern(int width, int height, int phase)
    {
        var ySize = width * height;
        var source = new byte[ySize * 3 / 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                source[y * width + x] = (byte)(((x / 16 + y / 16) & 1) == 0 ? 30 : 220);
        }
        for (var index = 0; index < ySize / 4; index++)
        {
            source[ySize + index] = (byte)(32 + index / (width / 2) % 192);
            source[ySize + ySize / 4 + index] = (byte)(224 - index / (width / 2) % 192);
        }
        source[width * 10 + 10] = (byte)(30 + phase * 10);
        return source;
    }
}
