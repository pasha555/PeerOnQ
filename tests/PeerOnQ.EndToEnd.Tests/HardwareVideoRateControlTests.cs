using PeerOnQ.Platform.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

/// <summary>
/// Measures whether the actual hardware encoder honours the adaptive ladder's bitrate targets on
/// scrolling text-like screen content. A target the encoder ignores cannot relieve a congested path.
/// </summary>
[Collection("Hardware video acceptance")]
public sealed class HardwareVideoRateControlTests(ITestOutputHelper output)
{
    private const double MaximumTargetRatio = 1.25;

    [HardwareVideoFact]
    public void Actual_hardware_output_follows_dynamic_ladder_bitrate_targets_on_scrolling_text()
    {
        const int width = 1920, height = 1080;
        var page = TextLikePage(width, height * 3);
        // The production ladder for the 60 fps / 10 Mbps profile, stepped dynamically as in the field log.
        var phases = new (int Kbps, int Fps, int Frames)[]
        {
            (10_000, 60, 240), (7_500, 40, 120), (5_000, 30, 90), (3_333, 30, 90), (2_000, 20, 80), (10_000, 60, 240),
        };
        using var encoder = new MediaFoundationH264Encoder();
        var frame = new byte[width * height * 3 / 2];
        Array.Fill(frame, (byte)128, width * height, width * height / 2);
        var scroll = 0;
        var results = new List<(int Kbps, double Actual, double Ratio)>();
        foreach (var (kbps, fps, frames) in phases)
            results.Add(Measure(encoder, kbps, fps, frames, fresh: false));

        using (var fresh = new MediaFoundationH264Encoder())
            results.Add(Measure(fresh, 2_000, 20, 80, fresh: true));

        output.WriteLine("-- bitrate-only changes at 60 fps");
        using (var bitrateOnly = new MediaFoundationH264Encoder())
            foreach (var kbps in new[] { 10_000, 2_000, 5_000, 10_000 })
                results.Add(Measure(bitrateOnly, kbps, 60, 120, fresh: false));

        output.WriteLine("-- frame-rate-only changes at 10 Mbps");
        using (var rateOnly = new MediaFoundationH264Encoder())
            foreach (var fps in new[] { 60, 30, 60 })
                results.Add(Measure(rateOnly, 10_000, fps, fps * 2, fresh: false));

        // Before every target change recreated the MFT, the AMD driver sent 1.26-2.91x its target here.
        Assert.All(results, result => Assert.True(result.Ratio is > 0 and <= MaximumTargetRatio,
            $"{result.Actual:0} kbps for a {result.Kbps} kbps target"));

        (int, double, double) Measure(MediaFoundationH264Encoder target, int kbps, int fps, int frames, bool fresh)
        {
            long bytes = 0;
            var counted = 0;
            var largest = 0;
            var transitionMs = 0.0;
            for (var index = 0; index < frames; index++)
            {
                // About one wheel notch per frame: the motion the 0.9.90 relay session carried.
                scroll = (scroll + 9) % (height * 2);
                Buffer.BlockCopy(page, scroll * width, frame, 0, width * height);
                var began = System.Diagnostics.Stopwatch.GetTimestamp();
                var encoded = target.EncodeI420(width, height, frame, kbps, fps, forceKeyFrame: false);
                if (index == 0) transitionMs = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                // Rate control settles within half a second of a target change.
                if (index < fps / 2) continue;
                bytes += encoded.Length;
                largest = Math.Max(largest, encoded.Length);
                counted++;
            }
            var actual = bytes * 8.0 * fps / counted / 1000;
            output.WriteLine($"{(fresh ? "fresh" : "dynamic")} target={kbps} kbps @ {fps} fps: actual={actual:0} kbps "
                + $"ratio={actual / kbps:0.00} meanFrame={bytes / counted} B largestFrame={largest} B "
                + $"budgetFrame={kbps * 1000 / 8 / fps} B transitionEncode={transitionMs:0.0} ms "
                + $"initialisations={target.InitialisationCount} encoder={target.Name}");
            return (kbps, actual, actual / kbps);
        }
    }

    [HardwareVideoFact]
    public void Production_quality_changes_switch_to_a_prepared_mft_without_stalling_the_encode_loop()
    {
        const int width = 1920, height = 1080;
        // Recreating the MFT in place held the loop for 150-230 ms at every change.
        const double maximumEncodeMs = 60;
        var page = TextLikePage(width, height * 3);
        var phases = new (int Kbps, int Fps)[] { (10_000, 60), (5_000, 30), (2_000, 20), (10_000, 60) };
        var frame = new byte[width * height * 3 / 2];
        Array.Fill(frame, (byte)128, width * height, width * height / 2);
        using var encoder = new PreparedTargetH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();
        var scroll = 0;
        var first = true;
        var slowest = 0.0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var due = TimeSpan.Zero;
        for (var phase = 0; phase < phases.Length; phase++)
        {
            var (kbps, fps) = phases[phase];
            var interval = TimeSpan.FromSeconds(1.0 / fps);
            int? switchedAt = null;
            long bytes = 0;
            var counted = 0;
            for (var index = 0; index < fps * 5 / 2; index++)
            {
                // Real-time pacing, as capture delivers frames: the replacement is created meanwhile.
                due += interval;
                while (clock.Elapsed < due)
                    if (due - clock.Elapsed > TimeSpan.FromMilliseconds(2)) Thread.Sleep(1);
                scroll = (scroll + 9) % (height * 2);
                Buffer.BlockCopy(page, scroll * width, frame, 0, width * height);
                var began = System.Diagnostics.Stopwatch.GetTimestamp();
                var encoded = encoder.EncodeI420(width, height, frame, kbps, fps, forceKeyFrame: false);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                if (!first) slowest = Math.Max(slowest, elapsed);
                Assert.True(first || elapsed < maximumEncodeMs,
                    $"Encode stalled {elapsed:0} ms at {kbps} kbps / {fps} fps frame {index}.");
                // One continuous decoder: the replacement's key frame needs no viewer reset.
                var decoded = Assert.Single(decoder.Decode(width, height, encoded, reset: first));
                Assert.Equal(width * height * 3, decoded.Pixels.Length);
                first = false;
                if (switchedAt is null && encoder.Switches == phase) switchedAt = index;
                if (switchedAt is not { } at || index < at + fps / 2) continue;
                bytes += encoded.Length;
                counted++;
            }
            Assert.True(counted > 0, $"The {kbps} kbps / {fps} fps target was never applied.");
            var actual = bytes * 8.0 * fps / counted / 1000;
            output.WriteLine($"prepared target={kbps} kbps @ {fps} fps: actual={actual:0} kbps ratio={actual / kbps:0.00} "
                + $"switchedAfterFrames={switchedAt} slowestEncodeSoFar={slowest:0.0} ms encoder={encoder.Name}");
            Assert.InRange(actual / kbps, 0.01, MaximumTargetRatio);
        }
        Assert.Equal(phases.Length - 1, encoder.Switches);
        Assert.Equal(0, encoder.InPlaceFallbacks);
    }

    /// <summary>Deterministic dark glyph runs on a light page, the hardest common desktop content.</summary>
    private static byte[] TextLikePage(int width, int height)
    {
        var page = new byte[width * height];
        Array.Fill(page, (byte)235);
        var random = new Random(42);
        for (var line = 8; line + 12 < height; line += 20)
        {
            var x = 24 + random.Next(0, 40);
            var end = width - 24 - random.Next(0, 600);
            while (x + 7 < end)
            {
                if (random.Next(0, 6) == 0) { x += 7; continue; } // word gap
                for (var y = 0; y < 12; y++)
                {
                    var bits = random.Next(0, 128);
                    for (var column = 0; column < 7; column++)
                        if ((bits >> column & 1) != 0) page[(line + y) * width + x + column] = 40;
                }
                x += 8;
            }
        }
        return page;
    }
}
