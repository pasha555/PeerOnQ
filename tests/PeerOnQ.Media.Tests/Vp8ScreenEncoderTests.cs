using System.Diagnostics;
using System.Security.Cryptography;
using PeerOnQ.Media.Codecs;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using vpxmd;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.Media.Tests;

public sealed class Vp8ScreenEncoderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0x010900, true)]
    [InlineData(0x010901, false)]
    [InlineData(0x010800, false)]
    [InlineData(0x010a00, false)]
    [InlineData(0x010e00, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Only_the_reviewed_native_encoder_layout_version_is_accepted(int version, bool expected) =>
        Assert.Equal(expected, Vp8ScreenEncoder.IsSupportedEncoderVersion(version));

    [Theory]
    [InlineData(0)]
    [InlineData(0x010e00)]
    public void Unsupported_native_version_is_rejected_before_initialisation_without_poisoning_targets(int version)
    {
        var nativeVersion = version;
        var configCalls = 0;
        using var encoder = new Vp8ScreenEncoder(4_000, 30, (context, configuration) =>
        {
            configCalls++;
            return vpx_encoder.VpxCodecEncConfigSet(context, configuration);
        }, () => nativeVersion);
        using var decoder = new VpxVideoEncoder();
        encoder.SetTargetKbps(2_000);
        encoder.SetTargetFps(20);
        var source = CreateCheckerboard(160, 90, 8, 0);
        var error = Assert.Throws<PlatformNotSupportedException>(() => encoder.EncodeI420(160, 90, source));
        Assert.Contains("configuration layout", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, encoder.InitialisationCount);
        Assert.Equal(0, encoder.ReconfigurationCount);
        Assert.Equal(0, configCalls);
        Assert.Equal(2_000, encoder.TargetKbps);
        Assert.Equal((uint)0, encoder.LastSubmittedDuration);

        nativeVersion = 0x010900;
        var first = encoder.EncodeI420(160, 90, source);
        AssertKeyFrame(first);
        Assert.Single(decoder.DecodeVideo(first, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8));
        Assert.Equal(1, encoder.InitialisationCount);
        Assert.Equal(0, encoder.LastSubmittedTimestamp);
        Assert.Equal((uint)4_500, encoder.LastSubmittedDuration);
        encoder.SetTargetKbps(1_000);
        encoder.SetTargetFps(15);
        var changed = encoder.EncodeI420(160, 90, source);
        AssertKeyFrame(changed);
        Assert.Single(decoder.DecodeVideo(changed, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8));
        Assert.Equal(1, encoder.InitialisationCount);
        // Both pending targets are applied by the encoder thread with one native call.
        Assert.Equal(1, configCalls);
        Assert.Equal((uint)6_000, encoder.LastSubmittedDuration);
    }

    [Fact]
    public void Loaded_native_codec_version_build_and_exact_module_are_recorded()
    {
        var version = vpx_codec.VpxCodecVersionStr();
        var build = vpx_codec.VpxCodecBuildConfig();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.False(string.IsNullOrWhiteSpace(build));
        using var process = Process.GetCurrentProcess();
        var native = Assert.Single(process.Modules.Cast<ProcessModule>(), module =>
            module.ModuleName.StartsWith("vpxmd", StringComparison.OrdinalIgnoreCase)
            || module.ModuleName.StartsWith("libvpx", StringComparison.OrdinalIgnoreCase));
        using var stream = File.OpenRead(native.FileName);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        output.WriteLine($"Native libvpx version={version}; number={vpx_codec.VpxCodecVersion()}; build={build}");
        output.WriteLine($"Loaded native module={native.FileName}; SHA256={hash}");
    }

    [Fact]
    public void Live_bitrate_and_fps_changes_reconfigure_real_context_and_preserve_recovery()
    {
        var nativeSetCalls = 0;
        var unexpectedConfigurations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var encoder = new Vp8ScreenEncoder(4_000, 30, (context, configuration) =>
        {
            // Runs on the encoder thread: record deviations instead of throwing into libvpx's caller.
            using var timebase = configuration.GTimebase;
            if (timebase.Num != 1 || timebase.Den != 90_000) unexpectedConfigurations.Enqueue($"timebase {timebase.Num}/{timebase.Den}");
            if (configuration.RcMinQuantizer != 2 || configuration.RcMaxQuantizer != 56)
                unexpectedConfigurations.Enqueue($"quantizers {configuration.RcMinQuantizer}-{configuration.RcMaxQuantizer}");
            nativeSetCalls++;
            return vpx_encoder.VpxCodecEncConfigSet(context, configuration);
        });
        using var decoder = new VpxVideoEncoder();
        const int width = 320;
        const int height = 180;
        var frame = CreateCheckerboard(width, height, 8, 0);
        long nextTimestamp = 0;
        EncodeAndDecode(expectKeyFrame: true);
        encoder.SetTargetKbps(4_000);
        encoder.SetTargetFps(30);
        Assert.Equal(0, nativeSetCalls);
        Assert.Equal(1, encoder.InitialisationCount);

        foreach (var (fps, kbps) in new[] { (30, 3_500), (20, 3_000), (15, 2_500), (30, 4_000) })
        {
            encoder.SetTargetFps(fps);
            encoder.SetTargetKbps(kbps);
            EncodeAndDecode(expectKeyFrame: true);
            Assert.Equal((uint)(90_000 / fps), encoder.LastSubmittedDuration);
            Assert.Equal(1, encoder.InitialisationCount);
        }
        // One native reconfiguration per frame that follows a change, however many targets moved.
        Assert.Equal(4, nativeSetCalls);
        Assert.Equal(nativeSetCalls, encoder.ReconfigurationCount);
        encoder.ForceKeyFrame();
        EncodeAndDecode(expectKeyFrame: true);
        Assert.Equal(1, encoder.InitialisationCount);
        Assert.Equal(4, nativeSetCalls);
        Assert.Empty(unexpectedConfigurations);

        var resized = encoder.EncodeI420(160, 90, CreateCheckerboard(160, 90, 8, 0));
        AssertKeyFrame(resized);
        var image = decoder.DecodeVideo(resized, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8).Single();
        Assert.Equal((uint)160, image.Width);
        Assert.Equal((uint)90, image.Height);
        Assert.Equal(2, encoder.InitialisationCount);
        Assert.Equal(0, encoder.LastSubmittedTimestamp);

        void EncodeAndDecode(bool expectKeyFrame)
        {
            var encoded = encoder.EncodeI420(width, height, frame);
            if (expectKeyFrame) AssertKeyFrame(encoded);
            var image = decoder.DecodeVideo(encoded, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8).Single();
            Assert.Equal((uint)width, image.Width);
            Assert.Equal((uint)height, image.Height);
            Assert.Equal(nextTimestamp, encoder.LastSubmittedTimestamp);
            nextTimestamp += encoder.LastSubmittedDuration;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(29)]
    [InlineData(59)]
    [InlineData(119)]
    [InlineData(120)]
    public void Fixed_timebase_preserves_exact_nominal_second_and_decodable_frames(int fps)
    {
        using var encoder = new Vp8ScreenEncoder(2_000, fps);
        using var decoder = new VpxVideoEncoder();
        var source = CreateCheckerboard(96, 64, 8, 0);
        long timestamp = 0;
        for (var frame = 0; frame < fps; frame++)
        {
            var encoded = encoder.EncodeI420(96, 64, source);
            Assert.NotEmpty(encoded);
            Assert.Single(decoder.DecodeVideo(encoded, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8));
            Assert.Equal(timestamp, encoder.LastSubmittedTimestamp);
            Assert.InRange(encoder.LastSubmittedDuration, (uint)(90_000 / fps), (uint)((90_000 + fps - 1) / fps));
            timestamp += encoder.LastSubmittedDuration;
        }
        Assert.Equal(90_000, timestamp);
        Assert.Equal(1, encoder.InitialisationCount);
    }

    [Theory]
    [InlineData("result", false)]
    [InlineData("result", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("exception", false)]
    [InlineData("exception", true)]
    public void Failed_reconfiguration_disposes_real_native_state_and_restarts_with_desired_targets(
        string failure, bool changeFps)
    {
        VpxCodecCtx? rejectedContext = null;
        VpxCodecEncCfg? rejectedConfiguration = null;
        var expectedFps = changeFps ? 20 : 30;
        var expectedKbps = changeFps ? 4_000 : 2_000;
        var liveContext = false;
        uint observedKbps = 0;
        uint observedKeyFrameInterval = 0;
        // The encoder thread makes this call; record what it saw and assert on the test thread.
        using var encoder = new Vp8ScreenEncoder(4_000, 30, (context, configuration) =>
        {
            rejectedContext = context;
            rejectedConfiguration = configuration;
            liveContext = context.__Instance != nint.Zero;
            observedKbps = configuration.RcTargetBitrate;
            observedKeyFrameInterval = configuration.KfMaxDist;
            return failure switch
            {
                "result" => VpxCodecErrT.VPX_CODEC_ERROR,
                "missing" => throw new EntryPointNotFoundException("Ordinary test-only unsupported configuration API."),
                _ => throw new InvalidOperationException("Ordinary test-only configuration failure."),
            };
        });
        using var decoder = new VpxVideoEncoder();
        var source = CreateCheckerboard(160, 90, 8, 0);
        AssertKeyFrame(encoder.EncodeI420(160, 90, source));
        // Setters only record targets; even a throwing native reconfiguration cannot reach them.
        ChangeTarget();
        Assert.Null(rejectedContext);
        Assert.Equal(expectedKbps, encoder.TargetKbps);
        var recovered = encoder.EncodeI420(160, 90, source);
        Assert.True(liveContext);
        Assert.Equal((uint)expectedKbps, observedKbps);
        Assert.Equal(Vp8ScreenEncoder.KeyFrameInterval(expectedFps), observedKeyFrameInterval);
        Assert.NotNull(rejectedContext);
        Assert.NotNull(rejectedConfiguration);
        Assert.Equal(nint.Zero, rejectedContext.__Instance);
        Assert.Equal(nint.Zero, rejectedConfiguration.__Instance);
        Assert.Equal(0, encoder.ReconfigurationCount);
        AssertKeyFrame(recovered);
        Assert.Single(decoder.DecodeVideo(recovered, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8));
        Assert.Equal(2, encoder.InitialisationCount);
        Assert.Equal(0, encoder.LastSubmittedTimestamp);
        Assert.Equal((uint)(90_000 / expectedFps), encoder.LastSubmittedDuration);

        void ChangeTarget()
        {
            if (changeFps) encoder.SetTargetFps(expectedFps);
            else encoder.SetTargetKbps(expectedKbps);
        }
    }

    [Theory]
    [InlineData(1280, 720, 8)]
    [InlineData(1920, 1080, 12)]
    [InlineData(3840, 2160, 16)]
    public void Desktop_pattern_remains_sharp_after_profile_encode_and_decode(
        int width,
        int height,
        int cellSize)
    {
        var targetKbps = width >= 3840 ? 24_000 : 6_000;
        using var encoder = new Vp8ScreenEncoder(targetKbps, targetFps: 30);
        using var decoder = new VpxVideoEncoder();
        var source = CreateCheckerboard(width, height, cellSize, phase: 0);

        var encoded = encoder.EncodeI420(width, height, source);
        var decoded = decoder
            .DecodeVideo(encoded, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8)
            .Single();

        Assert.Equal((uint)width, decoded.Width);
        Assert.Equal((uint)height, decoded.Height);
        var (dark, light) = MeasureCheckerboardCenters(decoded.Sample, width, height, cellSize);
        Assert.True(
            light - dark >= 150,
            $"Decoded checkerboard contrast was only {light - dark:F1} (dark={dark:F1}, light={light:F1}).");
    }

    [Fact]
    public void Encoder_rate_control_uses_the_requested_bitrate_instead_of_the_libvpx_default()
    {
        const int width = 640;
        const int height = 360;
        const int fps = 30;
        var lowKbps = MeasureSequenceBitrate(width, height, fps, targetKbps: 300);
        var highKbps = MeasureSequenceBitrate(width, height, fps, targetKbps: 2_400);

        Assert.True(
            highKbps >= lowKbps * 1.8,
            $"VP8 target did not materially affect output (low={lowKbps:F1} kbps, high={highKbps:F1} kbps).");
        Assert.True(lowKbps < 2_000, $"Low target produced {lowKbps:F1} kbps instead of a bounded stream.");
    }

    [Fact]
    public void Dimension_and_rate_changes_restart_with_a_decodable_key_frame()
    {
        using var encoder = new Vp8ScreenEncoder(targetKbps: 4_000, targetFps: 30);
        using var decoder = new VpxVideoEncoder();

        var first = encoder.EncodeI420(640, 360, CreateCheckerboard(640, 360, 8, 0));
        AssertKeyFrame(first);
        Assert.Single(decoder.DecodeVideo(first, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8));

        encoder.SetTargetKbps(2_000);
        var rateChanged = encoder.EncodeI420(640, 360, CreateCheckerboard(640, 360, 8, 1));
        AssertKeyFrame(rateChanged);
        Assert.Single(decoder.DecodeVideo(rateChanged, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8));

        var resized = encoder.EncodeI420(320, 180, CreateCheckerboard(320, 180, 8, 0));
        AssertKeyFrame(resized);
        var image = decoder.DecodeVideo(resized, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8).Single();
        Assert.Equal((uint)320, image.Width);
        Assert.Equal((uint)180, image.Height);

        encoder.ForceKeyFrame();
        var forced = encoder.EncodeI420(320, 180, CreateCheckerboard(320, 180, 8, 1));
        AssertKeyFrame(forced);
    }

    [Fact]
    public void Picture_loss_recovery_is_throttled_and_the_next_transport_frame_is_a_key_frame()
    {
        var recovery = new Vp8LossRecoveryController();
        var intervalTicks = (long)Math.Ceiling(
            Vp8LossRecoveryController.PictureLossIndicationInterval.TotalSeconds
            * Stopwatch.Frequency);

        Assert.True(recovery.TryAcquirePictureLossIndication(timestamp: 0));
        Assert.False(recovery.TryAcquirePictureLossIndication(intervalTicks - 1));
        Assert.True(recovery.TryAcquirePictureLossIndication(intervalTicks));

        const int width = 320;
        const int height = 180;
        var source = CreateCheckerboard(width, height, cellSize: 8, phase: 0);
        using var encoder = new Vp8ScreenEncoder(targetKbps: 1_000, targetFps: 30);

        var first = WebRtcMediaSession.EncodeFrameForTransport(
            encoder, recovery, width, height, source);
        AssertKeyFrame(first);

        var interFrame = WebRtcMediaSession.EncodeFrameForTransport(
            encoder, recovery, width, height, source);
        Assert.NotEmpty(interFrame);
        Assert.Equal(1, interFrame[0] & 1);

        // Repeated PLI reports coalesce into one pending request. The encode loop consumes
        // that flag and prepares the very next RTP payload as a key frame.
        recovery.RequestKeyFrame();
        recovery.RequestKeyFrame();
        var recovered = WebRtcMediaSession.EncodeFrameForTransport(
            encoder, recovery, width, height, source);
        AssertKeyFrame(recovered);

        var following = WebRtcMediaSession.EncodeFrameForTransport(
            encoder, recovery, width, height, source);
        Assert.NotEmpty(following);
        Assert.Equal(1, following[0] & 1);
        Assert.False(recovery.ConsumeKeyFrameRequest());
    }

    [Theory]
    [InlineData(0, 2, "width")]
    [InlineData(3, 2, "width")]
    [InlineData(16_384, 2, "width")]
    [InlineData(2, 0, "height")]
    [InlineData(2, 3, "height")]
    [InlineData(2, 16_384, "height")]
    public void Invalid_dimensions_are_rejected_before_native_initialisation(
        int width,
        int height,
        string parameterName)
    {
        using var encoder = new Vp8ScreenEncoder(targetKbps: 1_000, targetFps: 30);

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => encoder.EncodeI420(width, height, []));

        Assert.Equal(parameterName, error.ParamName);
    }

    [Fact]
    public void Rejected_codec_dimension_does_not_poison_encoder_and_dispose_is_idempotent()
    {
        var encoder = new Vp8ScreenEncoder(targetKbps: 1_000, targetFps: 30);
        try
        {
            const int invalidWidth = 16_384;
            const int invalidHeight = 2;
            var correctlySizedInvalidFrame = new byte[invalidWidth * invalidHeight * 3 / 2];

            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => encoder.EncodeI420(invalidWidth, invalidHeight, correctlySizedInvalidFrame));
            Assert.Equal("width", error.ParamName);

            var encoded = encoder.EncodeI420(320, 180, CreateCheckerboard(320, 180, 8, 0));
            AssertKeyFrame(encoded);
        }
        finally
        {
            encoder.Dispose();
            encoder.Dispose();
        }
    }

    [Fact]
    public void Impractically_large_I420_frame_is_rejected_and_can_be_disposed()
    {
        var encoder = new Vp8ScreenEncoder(targetKbps: 1_000, targetFps: 30);
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(
                () => encoder.EncodeI420(16_382, 3_000, []));

            Assert.Equal("frame", error.ParamName);
        }
        finally
        {
            encoder.Dispose();
            encoder.Dispose();
        }
    }

    private static byte[] CreateCheckerboard(int width, int height, int cellSize, int phase)
    {
        var frame = new byte[width * height * 3 / 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var light = (((x / cellSize) + (y / cellSize) + phase) & 1) == 0;
                frame[(y * width) + x] = light ? (byte)235 : (byte)16;
            }
        }

        Array.Fill(frame, (byte)128, width * height, frame.Length - (width * height));
        return frame;
    }

    private static byte[] CreateBusyFrame(int width, int height, int sequence)
    {
        var frame = new byte[width * height * 3 / 2];
        var state = unchecked((uint)(0x9E3779B9 + sequence * 0x85EBCA6B));
        for (var index = 0; index < width * height; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            frame[index] = (byte)(16 + (state % 220));
        }

        Array.Fill(frame, (byte)128, width * height, frame.Length - (width * height));
        return frame;
    }

    private static double MeasureSequenceBitrate(int width, int height, int fps, int targetKbps)
    {
        const int warmupFrames = 30;
        const int measuredFrames = 90;
        using var encoder = new Vp8ScreenEncoder(targetKbps, fps);
        long totalBytes = 0;
        for (var frame = 0; frame < warmupFrames + measuredFrames; frame++)
        {
            var encoded = encoder.EncodeI420(width, height, CreateBusyFrame(width, height, frame));
            if (frame >= warmupFrames) totalBytes += encoded.Length;
        }

        return totalBytes * 8.0 * fps / measuredFrames / 1_000;
    }

    private static (double Dark, double Light) MeasureCheckerboardCenters(
        byte[] bgr,
        int width,
        int height,
        int cellSize)
    {
        long darkTotal = 0;
        long lightTotal = 0;
        var darkCount = 0;
        var lightCount = 0;
        for (var y = cellSize / 2; y < height; y += cellSize)
        {
            for (var x = cellSize / 2; x < width; x += cellSize)
            {
                var value = bgr[((y * width) + x) * 3];
                if ((((x / cellSize) + (y / cellSize)) & 1) == 0)
                {
                    lightTotal += value;
                    lightCount++;
                }
                else
                {
                    darkTotal += value;
                    darkCount++;
                }
            }
        }

        return (darkTotal / (double)darkCount, lightTotal / (double)lightCount);
    }

    private static void AssertKeyFrame(byte[] frame)
    {
        Assert.NotEmpty(frame);
        Assert.Equal(0, frame[0] & 1);
    }
}
