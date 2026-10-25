using System.Diagnostics;
using System.Runtime;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Codecs;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.Encoders.Codecs;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.Media.Tests;

[Collection("Real WebRTC acceptance")]
public sealed class Vp8ScreenDecoderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(2160, 3840)]
    public void Four_k_decode_stage_diagnostics_measure_real_codec_and_color_conversion(int width, int height)
    {
        using var encoder = new Vp8ScreenEncoder(36_000, 30);
        using var rawDecoder = new Vp8Codec();
        rawDecoder.InitialiseDecoder();
        var decodeMs = new List<double>();
        var convertMs = new List<double>();
        var optimizedMs = new List<double>();
        var encodeMs = new List<double>();
        var source = CreatePattern(width, height, 0);
        for (var phase = 0; phase < 2; phase++)
        {
            var sample = MeasureFrame(phase);
            output.WriteLine($"Synthetic {width}x{height} VP8 warmup={phase}; encodeMs={sample.Encode:F2}; "
                + $"nativeDecodeMs={sample.Decode:F2}; upstreamBgrMs={sample.Upstream:F2}; optimizedBgrMs={sample.Optimized:F2}");
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var privateBytes = process.PrivateMemorySize64;
        for (var phase = 2; phase < 14; phase++)
        {
            var sample = MeasureFrame(phase);
            encodeMs.Add(sample.Encode);
            decodeMs.Add(sample.Decode);
            convertMs.Add(sample.Upstream);
            optimizedMs.Add(sample.Optimized);
        }
        process.Refresh();
        output.WriteLine($"Synthetic {width}x{height} real VP8 measuredFrames=12 afterWarmup=2; "
            + $"logicalProcessors={Environment.ProcessorCount} serverGC={GCSettings.IsServerGC} latencyMode={GCSettings.LatencyMode}; "
            + $"allocatedBytes={GC.GetTotalAllocatedBytes(precise: true) - allocated}; "
            + $"GC0/1/2={GC.CollectionCount(0) - collections[0]}/{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]}; "
            + $"processCpuMs={(process.TotalProcessorTime - cpu).TotalMilliseconds:F2}; privateBytesStart/End={privateBytes}/{process.PrivateMemorySize64}; "
            + $"encode ms min/mean/max={Range(encodeMs)}; nativeDecode={Range(decodeMs)}; "
            + $"upstreamBgr={Range(convertMs)}; optimizedBgr={Range(optimizedMs)}");

        (double Encode, double Decode, double Upstream, double Optimized) MeasureFrame(int phase)
        {
            source[width * 10 + 10] = (byte)(30 + phase * 10);
            var began = Stopwatch.GetTimestamp();
            var encoded = encoder.EncodeI420(width, height, source);
            var encode = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            Assert.NotEmpty(encoded);
            began = Stopwatch.GetTimestamp();
            var decoded = rawDecoder.Decode(encoded, encoded.Length, out var decodedWidth, out var decodedHeight);
            var decode = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            var image = Assert.Single(decoded);
            Assert.Equal((uint)width, decodedWidth);
            Assert.Equal((uint)height, decodedHeight);
            began = Stopwatch.GetTimestamp();
            var bgr = PixelConverter.I420toBGR(image, width, height, out _);
            var upstream = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            Assert.Equal(width * height * 3, bgr.Length);
            began = Stopwatch.GetTimestamp();
            var optimized = I420BgrConverter.Convert(image, width, height);
            var optimizedDuration = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            Assert.True(bgr.AsSpan().SequenceEqual(optimized), "Optimized conversion changed a visible pixel.");
            return (encode, decode, upstream, optimizedDuration);
        }

        static string Range(List<double> samples) => $"{samples.Min():F2}/{samples.Average():F2}/{samples.Max():F2}ms";
    }

    [Fact]
    public void Optimized_conversion_preserves_every_possible_Y_U_V_color_exactly()
    {
        const int width = 512, height = 512;
        var source = new byte[width * height * 3 / 2];
        for (var block = 0; block < 65536; block++)
        {
            source[width * height + block] = (byte)(block >> 8);
            source[width * height * 5 / 4 + block] = (byte)block;
        }
        for (var phase = 0; phase < 64; phase++)
        {
            for (var row = 0; row < height; row += 2)
                for (var column = 0; column < width; column += 2)
                {
                    var position = row * width + column;
                    source[position] = (byte)(phase * 4);
                    source[position + 1] = (byte)(phase * 4 + 1);
                    source[position + width] = (byte)(phase * 4 + 2);
                    source[position + width + 1] = (byte)(phase * 4 + 3);
                }
            var expected = PixelConverter.I420toBGR(source, width, height, out _);
            var actual = I420BgrConverter.Convert(source, width, height);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"Color mismatch for luma group {phase}.");
        }
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(6, 4)]
    [InlineData(8, 2)]
    [InlineData(10, 6)]
    [InlineData(14, 4)]
    [InlineData(18, 10)]
    [InlineData(30, 6)]
    public void Optimized_conversion_returns_packed_visible_rows_and_rejects_incomplete_planes(int width, int height)
    {
        var source = Enumerable.Range(0, width * height * 3 / 2).Select(index => (byte)(index * 17)).ToArray();
        var original = source.ToArray();
        var expected = PixelConverter.I420toBGR(source, width, height, out var upstreamStride);
        var actual = I420BgrConverter.Convert(source, width, height);
        Assert.Equal(original, source);
        Assert.NotSame(source, actual);
        var frame = new RemoteVideoFrame(width, height, actual, RemotePixelFormat.Bgr24, TimeSpan.Zero);
        Assert.Equal(frame.ExpectedLength, actual.Length);
        for (var row = 0; row < height; row++)
            Assert.True(expected.AsSpan(row * upstreamStride, width * 3)
                .SequenceEqual(actual.AsSpan(row * width * 3, width * 3)), "Visible row differs from upstream pixels.");
        Assert.Throws<ArgumentException>(() => I420BgrConverter.Convert(source[..^1], width, height));
        Assert.Throws<ArgumentException>(() => I420BgrConverter.Convert(source, width + 1, height));
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(2160, 3840)]
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
