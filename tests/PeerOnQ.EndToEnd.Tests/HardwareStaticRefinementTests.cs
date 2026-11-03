using PeerOnQ.Platform.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>
/// Proves the static-refinement benefit on the real encoder: a change-driven desktop sends one
/// frame while scrolling, and without refinement its text stays at that moving frame's quality.
/// Re-encoding the still frame lets a CBR encoder spend the idle link on the residual.
/// </summary>
[Collection("Hardware video acceptance")]
public sealed class HardwareStaticRefinementTests(ITestOutputHelper output)
{
    [HardwareVideoTheory]
    // Dense random glyphs are the worst case; at 2 Mbps the window is bit-limited.
    [InlineData(5_000, 20)]
    [InlineData(2_000, 10)]
    public void Re_encoding_a_still_desktop_for_the_production_window_sharpens_its_text(int kbps, double minimumGainDb)
    {
        const int width = 1920, height = 1080, fps = 60;
        Assert.True(PeerOnQ.Platform.Windows.ProcessPowerThrottling.OptOut());
        var page = TextPage(width, height * 2);
        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();
        var i420 = new byte[width * height * 3 / 2];
        Array.Fill(i420, (byte)128, width * height, width * height / 2);

        // Scroll for a second, as the capture source delivers moving frames.
        var first = true;
        for (var frame = 0; frame < fps; frame++)
        {
            Buffer.BlockCopy(page, frame * 9 % height * width, i420, 0, width * height);
            var encoded = encoder.EncodeNv12(width, height, MediaFoundationVideoUtilities.I420ToNv12(width, height, i420), kbps, fps, false);
            decoder.Decode(width, height, encoded, reset: first);
            first = false;
        }

        // The screen stops: this is the frame the viewer sees without refinement.
        var still = MediaFoundationVideoUtilities.I420ToNv12(width, height, i420);
        var reference = MediaFoundationH264Decoder.Nv12ToBgr(width, height, width, height, still);
        var moving = Psnr(reference, Decode(decoder, encoder, width, height, still, kbps, fps));

        // The production policy decides how long the still frame is re-encoded at this bitrate,
        // and re-arms after a key frame (the encoder's 5 s safety-net IDR) as the encode loop does.
        var refinement = new PeerOnQ.Media.Pipeline.StaticRefinement();
        refinement.Observe(width, height, still, fps, kbps);
        var refined = moving;
        var frames = 0;
        var keyFrames = 0;
        while (refinement.TryTake())
        {
            var encoded = encoder.EncodeNv12(width, height, still, kbps, fps, forceKeyFrame: false);
            refined = Psnr(reference, Assert.Single(decoder.Decode(width, height, encoded, reset: false)).Pixels.Span);
            if (PeerOnQ.Media.Pipeline.StaticRefinement.IsKeyFrame(PeerOnQ.Media.Codecs.VideoPayloadCodec.H264, encoded))
            {
                refinement.KeyFrameEncoded(fps, kbps);
                keyFrames++;
            }
            frames++;
        }
        // The window ends: no endless IDR, refine, IDR cycle on a still desktop.
        Assert.True(frames < fps * 10, $"refinement did not end: {frames} frames");
        Assert.True(MediaFoundationH264Encoder.KeyFrameIntervalSeconds
                    > PeerOnQ.Media.Pipeline.StaticRefinement.MaximumDuration.TotalSeconds);

        output.WriteLine($"1080p dense text at {kbps} kbps / {fps} fps: moving frame {moving:0.0} dB, "
            + $"after {frames} refinement frames ({frames / (double)fps:0.0} s, {keyFrames} key frames) {refined:0.0} dB");
        Assert.True(refined > moving + minimumGainDb,
            $"refinement gained only {refined - moving:0.0} dB (from {moving:0.0} to {refined:0.0})");
    }

    private static ReadOnlySpan<byte> Decode(
        MediaFoundationH264Decoder decoder, MediaFoundationH264Encoder encoder,
        int width, int height, byte[] nv12, int kbps, int fps)
    {
        var encoded = encoder.EncodeNv12(width, height, nv12, kbps, fps, forceKeyFrame: false);
        return Assert.Single(decoder.Decode(width, height, encoded, reset: false)).Pixels.ToArray();
    }

    private static double Psnr(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        double sum = 0;
        for (var i = 0; i < expected.Length; i++) { double d = expected[i] - actual[i]; sum += d * d; }
        var mse = sum / expected.Length;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static byte[] TextPage(int width, int height)
    {
        var page = new byte[width * height];
        Array.Fill(page, (byte)235);
        var random = new Random(42);
        for (var line = 8; line + 12 < height; line += 20)
            for (var x = 24 + random.Next(0, 40); x + 7 < width - 24; x += 8)
                for (var y = 0; y < 12; y++)
                {
                    var bits = random.Next(0, 128);
                    for (var column = 0; column < 7; column++)
                        if ((bits >> column & 1) != 0) page[(line + y) * width + x + column] = 40;
                }
        return page;
    }
}
