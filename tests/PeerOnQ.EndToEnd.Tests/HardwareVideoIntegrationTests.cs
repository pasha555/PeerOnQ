using System.Diagnostics;
using System.Runtime;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Identity;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media;
using PeerOnQ.Media.Codecs;
using PeerOnQ.Media.Pipeline;
using PeerOnQ.Platform.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

public sealed class HardwareVideoFactAttribute : FactAttribute
{
    public HardwareVideoFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PEERONQ_HARDWARE_VIDEO_TEST"), "1", StringComparison.Ordinal))
            Skip = "Set PEERONQ_HARDWARE_VIDEO_TEST=1 on a Windows machine with an actual hardware H.264 encoder.";
    }
}

public sealed class HardwareVideoTheoryAttribute : TheoryAttribute
{
    public HardwareVideoTheoryAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PEERONQ_HARDWARE_VIDEO_TEST"), "1", StringComparison.Ordinal))
            Skip = "Set PEERONQ_HARDWARE_VIDEO_TEST=1 on a Windows machine with an actual hardware H.264 encoder.";
    }
}

[CollectionDefinition("Hardware video acceptance", DisableParallelization = true)]
public sealed class HardwareVideoAcceptanceCollection;

[Collection("Hardware video acceptance")]
public sealed class HardwareVideoIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public void NV12_conversion_respects_stride_and_visible_crop()
    {
        byte[] i420 = [16, 235, 81, 145, 128, 128];
        var nv12 = MediaFoundationVideoUtilities.I420ToNv12(2, 2, i420);
        Assert.Equal(i420, nv12);
        var bgr = MediaFoundationH264Decoder.Nv12ToBgr(2, 2, 2, 2, nv12);
        Assert.Equal(new byte[] { 0, 0, 0 }, bgr[..3]);
        Assert.Equal(new byte[] { 255, 255, 255 }, bgr[3..6]);
        Assert.Throws<ArgumentException>(() => MediaFoundationH264Decoder.Nv12ToBgr(2, 2, 2, 2, nv12[..^1]));
        Assert.Throws<ArgumentException>(() => MediaFoundationVideoUtilities.I420ToNv12(8192, 8192, []));
    }

    [Theory]
    [InlineData(2, 2, 2, 2)]
    [InlineData(6, 4, 8, 6)]
    [InlineData(8, 2, 8, 2)]
    [InlineData(10, 6, 12, 8)]
    [InlineData(14, 4, 16, 6)]
    [InlineData(18, 10, 20, 12)]
    [InlineData(30, 6, 32, 8)]
    public void Native_layout_converters_write_every_visible_byte_including_packed_row_edges(
        int width, int height, int stride, int bufferHeight)
    {
        var source = Enumerable.Range(0, width * height * 3 / 2).Select(index => (byte)(index * 37)).ToArray();
        var interleaved = MediaFoundationVideoUtilities.I420ToNv12(width, height, source);
        var reusable = Enumerable.Repeat((byte)253, source.Length).ToArray();
        MediaFoundationVideoUtilities.I420ToNv12(width, height, source, reusable);
        Assert.Equal(interleaved, reusable);
        var next = source.Select(value => (byte)(255 - value)).ToArray();
        MediaFoundationVideoUtilities.I420ToNv12(width, height, next, reusable);
        Assert.Equal(MediaFoundationVideoUtilities.I420ToNv12(width, height, next), reusable);
        Assert.Throws<ArgumentException>(() => MediaFoundationVideoUtilities.I420ToNv12(width, height, source, source));
        Assert.Throws<ArgumentException>(() => MediaFoundationVideoUtilities.I420ToNv12(width, height, source, new byte[source.Length - 1]));
        var luma = width * height;
        Assert.Equal(source[..luma], interleaved[..luma]);
        for (var index = 0; index < luma / 4; index++)
        {
            Assert.Equal(source[luma + index], interleaved[luma + index * 2]);
            Assert.Equal(source[luma + luma / 4 + index], interleaved[luma + index * 2 + 1]);
        }
        var padded = Enumerable.Repeat((byte)253, stride * bufferHeight * 3 / 2).ToArray();
        for (var row = 0; row < height; row++)
            interleaved.AsSpan(row * width, width).CopyTo(padded.AsSpan(row * stride));
        for (var row = 0; row < height / 2; row++)
            interleaved.AsSpan(luma + row * width, width).CopyTo(padded.AsSpan(stride * bufferHeight + row * stride));
        var original = padded.ToArray();
        var actual = MediaFoundationH264Decoder.Nv12ToBgr(width, height, stride, bufferHeight, padded);
        Assert.Equal(original, padded);
        Assert.NotSame(padded, actual);
        Assert.Equal(width * height * 3, actual.Length);
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var y = Math.Max(0, source[row * width + column] - 16);
                var offset = row / 2 * (width / 2) + column / 2;
                var u = source[luma + offset] - 128;
                var v = source[luma + luma / 4 + offset] - 128;
                var target = (row * width + column) * 3;
                Assert.Equal((byte)Math.Clamp((298 * y + 516 * u + 128) >> 8, 0, 255), actual[target]);
                Assert.Equal((byte)Math.Clamp((298 * y - 100 * u - 208 * v + 128) >> 8, 0, 255), actual[target + 1]);
                Assert.Equal((byte)Math.Clamp((298 * y + 409 * v + 128) >> 8, 0, 255), actual[target + 2]);
            }
    }

    [Fact]
    public void NV12_conversion_preserves_every_possible_Y_U_V_color_exactly()
    {
        const int width = 512, height = 512, luma = width * height;
        var source = new byte[luma * 3 / 2];
        for (var block = 0; block < luma / 4; block++)
        {
            source[luma + block * 2] = (byte)(block >> 8);
            source[luma + block * 2 + 1] = (byte)block;
        }
        for (var phase = 0; phase < 64; phase++)
        {
            var expected = new byte[luma * 3];
            for (var row = 0; row < height; row += 2)
                for (var column = 0; column < width; column += 2)
                {
                    var position = row * width + column;
                    var chroma = luma + row / 2 * width + column;
                    var u = source[chroma] - 128;
                    var v = source[chroma + 1] - 128;
                    source[position] = (byte)(phase * 4);
                    source[position + 1] = (byte)(phase * 4 + 1);
                    source[position + width] = (byte)(phase * 4 + 2);
                    source[position + width + 1] = (byte)(phase * 4 + 3);
                    WriteReferenceNv12Pixel(expected, position * 3, phase * 4, u, v);
                    WriteReferenceNv12Pixel(expected, (position + 1) * 3, phase * 4 + 1, u, v);
                    WriteReferenceNv12Pixel(expected, (position + width) * 3, phase * 4 + 2, u, v);
                    WriteReferenceNv12Pixel(expected, (position + width + 1) * 3, phase * 4 + 3, u, v);
                }
            var actual = MediaFoundationH264Decoder.Nv12ToBgr(width, height, width, height, source);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"NV12 color mismatch for luma group {phase}.");
        }
    }

    private static void WriteReferenceNv12Pixel(byte[] target, int offset, int value, int u, int v)
    {
        var y = Math.Max(0, value - 16);
        target[offset] = (byte)Math.Clamp((298 * y + 516 * u + 128) >> 8, 0, 255);
        target[offset + 1] = (byte)Math.Clamp((298 * y - 100 * u - 208 * v + 128) >> 8, 0, 255);
        target[offset + 2] = (byte)Math.Clamp((298 * y + 409 * v + 128) >> 8, 0, 255);
    }

    [Fact]
    public void Encoder_disposal_is_idempotent_and_invalid_input_never_opens_a_driver()
    {
        var encoder = new MediaFoundationH264Encoder();
        Assert.Throws<ArgumentException>(() => encoder.EncodeI420(1920, 1080, [0], 20_000, 30, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.EncodeI420(2160, 3840, [], 20_000, 30, false));
        Assert.Equal(0, encoder.InitialisationCount);
        Assert.Equal(0, encoder.Nv12ScratchAllocations);
        encoder.Dispose();
        encoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => encoder.EncodeI420(2, 2, [16, 16, 16, 16, 128, 128], 2000, 30, false));
    }

    [HardwareVideoFact]
    public void Actual_hardware_encodes_and_MF_decodes_synthetic_1080p_and_resize()
    {
        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();
        var durations = new List<double>();
        var received = 0;
        foreach (var (width, height) in new[] { (1920, 1080), (1280, 720) })
        {
            // Codec accepts source sizes. The production capture floor is tested separately.
            var image = SyntheticFrame(width, height);
            for (var index = 0; index < 4; index++)
            {
                var began = Stopwatch.GetTimestamp();
                var encoded = encoder.EncodeI420(width, height, image, 20_000, 30, forceKeyFrame: index is 0 or 2);
                durations.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                Assert.NotEmpty(encoded);
                var decoded = decoder.Decode(width, height, encoded, reset: index == 0);
                output.WriteLine($"{width}x{height} frame={index} encoded={encoded.Length} decoded={decoded.Length} encoder={encoder.Name} decoder={decoder.Name}");
                foreach (var frame in decoded)
                {
                    Assert.Equal(width, frame.Width); Assert.Equal(height, frame.Height);
                    Assert.Equal(width * height * 3, frame.Pixels.Length);
                    Assert.InRange(frame.Pixels.Span[0], (byte)100, (byte)120);
                    received++;
                }
            }
        }
        Assert.Equal(8, received);
        output.WriteLine($"MFT hardware encoder: {encoder.Name}; decoder: {decoder.Name}; decoded={received}/8; "
            + $"encode including initialization ms min/mean/max={durations.Min():F2}/{durations.Average():F2}/{durations.Max():F2}");
        encoder.Dispose(); decoder.Dispose();
        encoder.Dispose(); decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => encoder.EncodeI420(1920, 1080, SyntheticFrame(1920, 1080), 20_000, 30, false));
        Assert.Throws<ObjectDisposedException>(() => decoder.Decode(1920, 1080, [0, 0, 1, 0x65, 0x88], false));
    }

    [HardwareVideoFact]
    public void Encoder_scratch_is_reused_zeroed_and_independent_of_native_input_during_resize()
    {
        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();
        var source = SyntheticFrame(3840, 2160);
        var outputs = new List<byte[]>();
        for (var index = 0; index < 2; index++)
            outputs.Add(encoder.EncodeI420(3840, 2160, source, 4000, 30, index == 0));
        Assert.Equal(1, encoder.Nv12ScratchAllocations);
        Assert.Equal(source.Length, encoder.Nv12ScratchBytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 4; index++)
        {
            outputs.Add(encoder.EncodeI420(3840, 2160, source, 4000, 30, false));
            Assert.True(encoder.IsNv12ScratchCleared);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(allocated < source.Length * 2L,
            $"Four warmed encodes allocated {allocated} managed bytes; a new NV12 array per frame would exceed {source.Length * 2L}.");
        foreach (var encoded in outputs)
        {
            var frame = Assert.Single(decoder.Decode(3840, 2160, encoded, reset: false));
            Assert.Equal(-1, frame.Pixels.Span.IndexOfAnyExceptInRange((byte)100, (byte)125));
        }
        Assert.Throws<ArgumentException>(() => encoder.EncodeI420(3840, 2160, [0], 4000, 30, false));
        Assert.True(encoder.IsNv12ScratchCleared);
        Assert.Equal(1, encoder.Nv12ScratchAllocations);
        foreach (var (width, height) in new[] { (1920, 1080), (3840, 2160) })
        {
            var encoded = encoder.EncodeI420(width, height, SyntheticFrame(width, height), 4000, 30, true);
            var frame = Assert.Single(decoder.Decode(width, height, encoded, reset: true));
            Assert.Equal(width * height * 3, frame.Pixels.Length);
            Assert.InRange(frame.Pixels.Span[0], (byte)100, (byte)125);
            Assert.True(encoder.IsNv12ScratchCleared);
        }
        Assert.Equal(3, encoder.Nv12ScratchAllocations);
        encoder.Dispose();
        Assert.Equal(0, encoder.Nv12ScratchBytes);
        Assert.True(encoder.IsNv12ScratchCleared);
        output.WriteLine($"Actual encoder={encoder.Name}; warmed4KEncodeManagedBytes={allocated}; "
            + "scratchAllocations=3 across4K/1080p/4K; all native frames survived immediate managed zeroing; disposedScratchBytes=0.");
    }

    [HardwareVideoFact]
    public void Hardware_frame_rate_changes_keep_matched_types_timestamps_and_fresh_decoder_recovery()
    {
        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();
        using var cpuDecoder = new MediaFoundationH264Decoder(() => null);
        using var baselineEncoder = new MediaFoundationH264Encoder(retainFillerForDiagnostics: true);
        using var baselineDecoder = new MediaFoundationH264Decoder();
        using var filteredBaselineDecoder = new MediaFoundationH264Decoder();
        var formats = new (int Width, int Height, int Fps, int Bitrate)[]
        {
            (3840, 2160, 30, 4000), (3840, 2160, 30, 4000),
            (3840, 2160, 20, 3000), (3840, 2160, 20, 3000),
            (3840, 2160, 15, 2000), (3840, 2160, 15, 2000),
            (3840, 2160, 30, 4000), (3840, 2160, 30, 3000),
            (1920, 1080, 30, 3000), (1920, 1080, 30, 3000),
            (3840, 2160, 20, 3000), (3840, 2160, 20, 3000),
        };
        long previousTimestamp = -1;
        var recoveries = 0;
        var rawComparisons = 0;
        var filteredRecoveries = 0;
        var removedFrames = 0;
        for (var index = 0; index < formats.Length; index++)
        {
            var (width, height, fps, bitrate) = formats[index];
            var source = SyntheticFrame(width, height);
            Array.Fill(source, (byte)(95 + index * 3), 0, width * height);
            var recovery = index == 0 || formats[index] != formats[index - 1];
            byte[]? original = null, filtered = null, encoded = null;
            try
            {
                encoded = encoder.EncodeI420(width, height, source, bitrate, fps, recovery);
                original = baselineEncoder.EncodeI420(width, height, source, bitrate, fps, recovery);
                filtered = H264FillerDataFilter.RemoveFromNativeSample(original);
                if (original.Length > filtered.Length)
                {
                    removedFrames++;
                    Assert.True(encoder.LastFillerBytesRemoved > 0, "The public encoder did not normalize its native filler output.");
                }
                Assert.True(encoder.IsNv12ScratchCleared);
                Assert.True(encoder.NativeTypesMatchFormat(width, height, fps));
                var timestamp = Assert.IsType<long>(encoder.LastOutputSampleTime);
                var duration = Assert.IsType<long>(encoder.LastOutputSampleDuration);
                Assert.True(timestamp > previousTimestamp, "Native sample time reset during rate/size adaptation.");
                Assert.InRange(duration, 10_000_000L / fps - 1, 10_000_000L / fps + 1);
                previousTimestamp = timestamp;
                var resized = index == 0 || width != formats[index - 1].Width || height != formats[index - 1].Height;
                var frame = Assert.Single(decoder.Decode(width, height, encoded, resized));
                var cpuFrame = Assert.Single(cpuDecoder.Decode(width, height, encoded, resized));
                Assert.True(frame.Pixels.Span.SequenceEqual(cpuFrame.Pixels.Span),
                    "D3D11 output changed the exact visible CPU-decoded pixels.");
                Assert.True(decoder.HardwareAccelerated, "The actual driver did not produce hardware-device D3D11 decoder output.");
                Assert.False(cpuDecoder.HardwareAccelerated);
                var originalFrame = Assert.Single(baselineDecoder.Decode(width, height, original, resized));
                var filteredFrame = Assert.Single(filteredBaselineDecoder.Decode(width, height, filtered, resized));
                Assert.True(originalFrame.Pixels.Span.SequenceEqual(filteredFrame.Pixels.Span),
                    "Removing filler changed the decoded pixels from the exact original access unit.");
                rawComparisons++;
                Assert.Equal(width, frame.Width); Assert.Equal(height, frame.Height);
                Assert.Equal(width * height * 3, frame.Pixels.Length);
                if (recovery)
                {
                    using var fresh = new MediaFoundationH264Decoder();
                    var recovered = Assert.Single(fresh.Decode(width, height, encoded, reset: true));
                    Assert.True(frame.Pixels.Span.SequenceEqual(recovered.Pixels.Span), "Rate/resize recovery pixels differ.");
                    recoveries++;
                    using var originalFresh = new MediaFoundationH264Decoder();
                    using var filteredFresh = new MediaFoundationH264Decoder();
                    var originalRecovery = Assert.Single(originalFresh.Decode(width, height, original, reset: true));
                    var filteredRecovery = Assert.Single(filteredFresh.Decode(width, height, filtered, reset: true));
                    Assert.True(originalRecovery.Pixels.Span.SequenceEqual(filteredRecovery.Pixels.Span),
                        "Filler removal changed independent fresh-keyframe recovery pixels.");
                    filteredRecoveries++;
                }
                output.WriteLine($"Actual MFT {width}x{height} requestedFps={fps}; targetKbps={bitrate}; "
                    + $"nativeTime100ns={timestamp}; nativeDuration100ns={duration}; encodedBytes={encoded.Length}; "
                    + $"initialisations={encoder.InitialisationCount}; dynamicChanges={encoder.DynamicFrameRateChanges}; "
                    + $"dynamicFallbacks={encoder.DynamicFrameRateFallbacks}");
                output.WriteLine(HardwareVideoBitrateDiagnostics.Describe(encoder, encoded));
                output.WriteLine($"Exact native baseline raw/removed/filteredBytes={original.Length}/{original.Length - filtered.Length}/{filtered.Length}; "
                    + $"publicEncoderRaw/removed/outputBytes={encoder.LastNativeEncodedBytes}/{encoder.LastFillerBytesRemoved}/{encoder.LastEncodedOutputBytes}");
            }
            finally
            {
                if (original is not null) CryptographicOperations.ZeroMemory(original);
                if (filtered is not null && !ReferenceEquals(filtered, original)) CryptographicOperations.ZeroMemory(filtered);
                if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
                CryptographicOperations.ZeroMemory(source);
            }
        }
        Assert.Equal(7, recoveries);
        Assert.Equal(12, rawComparisons);
        Assert.Equal(7, filteredRecoveries);
        Assert.True(removedFrames > 0, "No actual driver filler was removed; raw-versus-filtered equality would be vacuous.");
        Assert.Equal(3, encoder.DynamicFrameRateChanges + encoder.DynamicFrameRateFallbacks);
        // Only initial open + two size transitions reconstruct on a driver that accepts
        // the matched rate changes. Rejections retain the explicit reconstruction path.
        Assert.Equal(3 + encoder.DynamicFrameRateFallbacks, encoder.InitialisationCount);
        Assert.Equal(3, encoder.Nv12ScratchAllocations);
        output.WriteLine($"Actual encoder={encoder.Name}; dynamicSupport={encoder.DynamicFormatChangeSupported}; "
            + $"decoded=12/12; freshRecovery={recoveries}/7; currentTypesMatched=12/12; "
            + $"originalVsFiltered={rawComparisons}/12; filteredFreshRecovery={filteredRecoveries}/7; "
            + $"actualFramesWithFillerRemoved={removedFrames}/12");
        output.WriteLine("D3D11-versus-CPU exact visible pixels=12/12, including native padded1080p, resize/return and seven fresh recoveries.");
    }

    [HardwareVideoFact]
    public void Native_H264_reference_decoding_can_continue_while_owned_color_planes_wait()
    {
        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();
        using var reference = new MediaFoundationH264Decoder();
        var source = SyntheticFrame(320, 240);
        var owned = new List<DecodedNv12Frame>();
        byte[]? firstSnapshot = null, expectedLast = null;
        try
        {
            for (var index = 0; index < 8; index++)
            {
                source[100] = (byte)(110 + index);
                var encoded = encoder.EncodeI420(320, 240, source, 4000, 30, index is 0 or 4);
                var image = Assert.Single(decoder.DecodeNv12(320, 240, encoded, index == 0));
                Assert.True(image.HasValidLayout);
                foreach (var previous in owned) Assert.NotSame(previous.Pixels, image.Pixels);
                owned.Add(image);
                firstSnapshot ??= image.Pixels.ToArray();
                Assert.Equal(firstSnapshot, owned[0].Pixels);
                expectedLast = Assert.Single(reference.Decode(320, 240, encoded, index == 0)).Pixels.ToArray();
            }
            // No color conversion on this decoder occurred until every native access unit was decoded.
            var actualLast = decoder.ConvertNv12ToBgr(owned[^1]);
            Assert.Equal(expectedLast, actualLast);
            Assert.All(actualLast, value => Assert.InRange(value, (byte)100, (byte)125));
            output.WriteLine($"Actual encoder={encoder.Name}; decoder={decoder.Name}; independently owned NV12 frames={owned.Count}; "
                + "all reference access units decoded before deferred color conversion; last pixels exactly match immediate decoding.");
        }
        finally
        {
            foreach (var image in owned) CryptographicOperations.ZeroMemory(image.Pixels);
        }
    }

    [HardwareVideoFact]
    public async Task Hardware_video_traverses_the_actual_authenticated_WebRTC_pipeline()
    {
        var soakSeconds = GetHardwareSoakSeconds();
        var sessionId = SessionId.New();
        await using var capture = new SyntheticVideoCapture();
        await using var sharer = WebRtcMediaSession.CreateSharer(sessionId, capture, MediaProfile.For(QualityProfile.Quality),
            encoderSelector: new VideoEncoderSelector(() => HardwareEncoderProbe.Supports(HardwareVideoCodec.H264), () => true),
            hardwareH264EncoderFactory: () => new MediaFoundationH264Encoder());
        await using var viewer = WebRtcMediaSession.CreateViewer(sessionId,
            h264Decoder: new MediaFoundationH264Decoder());
        var secret = RandomNumberGenerator.GetBytes(64);
        using var senderSecurity = new SessionTrafficProtector(secret, sessionId, SessionRole.Sharer);
        using var receiverSecurity = new SessionTrafficProtector(secret, sessionId, SessionRole.Viewer);
        CryptographicOperations.ZeroMemory(secret);
        sharer.SetTrafficProtector(senderSecurity);
        viewer.SetTrafficProtector(receiverSecurity);
        sharer.LocalIceCandidate += async (_, candidate) =>
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        viewer.LocalIceCandidate += async (_, candidate) =>
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        var frames = 0;
        var invalidDimensions = 0;
        var softwareFrames = 0;
        long firstFrameTimestamp = 0;
        long lastFrameTimestamp = 0;
        var first = new TaskCompletionSource<RemoteVideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sustained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.RemoteFrameReceived += (_, frame) =>
        {
            var now = Stopwatch.GetTimestamp();
            Interlocked.CompareExchange(ref firstFrameTimestamp, now, 0);
            Interlocked.Exchange(ref lastFrameTimestamp, now);
            if (frame.Width != 1920 || frame.Height != 1080)
                Interlocked.Increment(ref invalidDimensions);
            if (soakSeconds > 0 && !sharer.Encoder.IsHardware)
                Interlocked.Increment(ref softwareFrames);
            if (Interlocked.Increment(ref frames) >= 120) sustained.TrySetResult();
            first.TrySetResult(frame);
        };
        var offer = await sharer.CreateOfferAsync();
        var answer = await viewer.CreateAnswerAsync(offer);
        await sharer.ApplyRemoteAnswerAsync(answer);
        Assert.True(sharer.IsH264PayloadNegotiated);
        Assert.True(viewer.IsH264PayloadNegotiated);
        await capture.StartAsync(new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Display, "synthetic", "Synthetic hardware test", 1920, 1080),
        });
        var received = await first.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1920, received.Width); Assert.Equal(1080, received.Height);
        await sustained.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, Volatile.Read(ref invalidDimensions));
        Assert.True(sharer.Encoder.IsHardware, sharer.Encoder.Reason);
        Assert.True(sharer.GetStatistics().EncoderHardwareAccelerated);
        if (soakSeconds > 0)
        {
            // Local continuity evidence on this real encrypted hardware session. Process memory
            // includes this test's managed/native allocations; observed cadence is not a guarantee.
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var privateBytesStart = process.PrivateMemorySize64;
            var privateBytesMax = privateBytesStart;
            var soakBegan = Stopwatch.GetTimestamp();
            var framesAtSoakStart = Volatile.Read(ref frames);
            var decoderHardwareAtStart = viewer.GetStatistics().DecoderHardwareAccelerated == true;
            var gpuDecoderSamples = 0;
            var cpuDecoderSamples = 0;
            while (Stopwatch.GetElapsedTime(soakBegan) < TimeSpan.FromSeconds(soakSeconds))
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                Assert.True(Stopwatch.GetElapsedTime(Interlocked.Read(ref lastFrameTimestamp)) < TimeSpan.FromSeconds(10),
                    $"No decoded hardware frame for 10 seconds; totalFrames={Volatile.Read(ref frames)}.");
                Assert.Equal(0, Volatile.Read(ref invalidDimensions));
                Assert.Equal(0, Volatile.Read(ref softwareFrames));
                Assert.True(sharer.Encoder.IsHardware, sharer.Encoder.Reason);
                Assert.True(sharer.GetStatistics().EncoderHardwareAccelerated);
                var decoderHardware = viewer.GetStatistics().DecoderHardwareAccelerated == true;
                if (decoderHardwareAtStart) Assert.True(decoderHardware, "D3D11 decoder output fell back during the continuity soak.");
                if (decoderHardware) gpuDecoderSamples++; else cpuDecoderSamples++;
                Assert.True(sharer.IsH264PayloadNegotiated && viewer.IsH264PayloadNegotiated);
                Assert.Equal(MediaConnectionState.Connected, sharer.State);
                Assert.Equal(MediaConnectionState.Connected, viewer.State);
                process.Refresh();
                privateBytesMax = Math.Max(privateBytesMax, process.PrivateMemorySize64);
            }
            var soakElapsed = Stopwatch.GetElapsedTime(soakBegan).TotalSeconds;
            var soakFrames = Volatile.Read(ref frames) - framesAtSoakStart;
            process.Refresh();
            var privateBytesEnd = process.PrivateMemorySize64;
            privateBytesMax = Math.Max(privateBytesMax, privateBytesEnd);
            Assert.True(soakFrames > 0);
            Assert.Equal(0, Volatile.Read(ref invalidDimensions));
            Assert.Equal(0, Volatile.Read(ref softwareFrames));
            Assert.True(sharer.Encoder.IsHardware, sharer.Encoder.Reason);
            Assert.True(sharer.GetStatistics().EncoderHardwareAccelerated);
            var decoderHardwareAtEnd = viewer.GetStatistics().DecoderHardwareAccelerated == true;
            if (decoderHardwareAtStart) Assert.True(decoderHardwareAtEnd);
            output.WriteLine($"Local encrypted hardware soak seconds={soakElapsed:F2}; decodedFrames={soakFrames}; "
                + $"observed delivery fps={soakFrames / soakElapsed:F2}; "
                + $"process private bytes start/max/end={privateBytesStart}/{privateBytesMax}/{privateBytesEnd}; "
                + $"decoder hardware start/end={decoderHardwareAtStart}/{decoderHardwareAtEnd}; "
                + $"GPU/CPU decoder samples={gpuDecoderSamples}/{cpuDecoderSamples}");
        }
        var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref firstFrameTimestamp),
            Interlocked.Read(ref lastFrameTimestamp)).TotalSeconds;
        output.WriteLine($"Encrypted WebRTC decoded {frames} synthetic 1080p frames; encoder={sharer.Encoder.Describe()}; "
            + $"decoder={viewer.GetStatistics().DecoderName}; payload H264 negotiated={sharer.IsH264PayloadNegotiated}; "
            + $"first-to-last seconds={elapsed:F2}; observed delivery fps={(frames - 1) / elapsed:F2}");
    }

    [HardwareVideoTheory]
    [InlineData(3840, 2160, true)]
    [InlineData(2160, 3840, false)]
    public async Task Four_k_automatic_sessions_preserve_native_pixels_with_authenticated_timing(
        int width, int height, bool expectHardware)
        => await ObserveFourKSessionAsync(width, height, expectHardware, observeSteadyState: false);

    [HardwareVideoTheory]
    [InlineData(3840, 2160, true)]
    [InlineData(2160, 3840, false)]
    public async Task Four_k_thirty_second_observation_reports_startup_and_steady_frame_age(
        int width, int height, bool expectHardware)
        => await ObserveFourKSessionAsync(width, height, expectHardware, observeSteadyState: true);

    private async Task ObserveFourKSessionAsync(int width, int height, bool expectHardware, bool observeSteadyState)
    {
        using var senderIdentity = await TestVideoIdentity.CreateAsync("Synthetic4K sharer");
        using var viewerIdentity = await TestVideoIdentity.CreateAsync("Synthetic4K viewer");
        var sessionId = SessionId.New();
        MediaFoundationH264Encoder? hardware = null;
        var decoder = new ObservedDeferredDecoder(new MediaFoundationH264Decoder());
        await using var capture = new SyntheticVideoCapture(width, height);
        await using var sharer = WebRtcMediaSession.CreateSharer(sessionId, capture,
            MediaProfile.For(QualityProfile.Automatic),
            encoderSelector: new VideoEncoderSelector(() => HardwareEncoderProbe.Supports(HardwareVideoCodec.H264), () => true),
            hardwareH264EncoderFactory: () => hardware = new MediaFoundationH264Encoder());
        await using var viewer = WebRtcMediaSession.CreateViewer(sessionId,
            h264Decoder: decoder);
        await using var sender = new MediaCollaborationTransport(sharer, SessionPermission.ViewScreen,
            senderIdentity.Service, viewerIdentity.Fingerprint);
        await using var receiver = new MediaCollaborationTransport(viewer, SessionPermission.ViewScreen,
            viewerIdentity.Service, senderIdentity.Fingerprint);
        sharer.LocalIceCandidate += async (_, candidate) =>
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        viewer.LocalIceCandidate += async (_, candidate) =>
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        var sustained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        var invalidFrames = 0;
        var timedFrames = 0;
        var observationGate = new Lock();
        var observations = new List<FourKPresentationObservation>(1024);
        using var observationProcess = observeSteadyState ? Process.GetCurrentProcess() : null;
        var memory = new List<(long At, long PrivateBytes)>();
        void SampleMemory()
        {
            observationProcess!.Refresh();
            memory.Add((Stopwatch.GetTimestamp(), observationProcess.PrivateMemorySize64));
        }
        if (observeSteadyState) SampleMemory();
        long firstAt = 0, lastAt = 0;
        viewer.RemoteFrameReceived += (_, frame) =>
        {
            var now = Stopwatch.GetTimestamp();
            Interlocked.CompareExchange(ref firstAt, now, 0);
            Interlocked.Exchange(ref lastAt, now);
            if (frame.Width != width || frame.Height != height || frame.Pixels.Length != frame.ExpectedLength)
                Interlocked.Increment(ref invalidFrames);
            viewer.ReportFrameRendered(new PresentedVideoFrame(frame.PipelineDecodedTimestamp,
                frame.Width, frame.Height, frame.CaptureTimestampUnixMicroseconds,
                frame.CaptureSequenceNumber, frame.PeerClockEstimate));
            if (frame.PeerClockEstimate is { UncertaintyMicroseconds: >= 0 and <= 50_000 })
                Interlocked.Increment(ref timedFrames);
            if (observeSteadyState)
            {
                double? age = null;
                if (frame.PeerClockEstimate is { UncertaintyMicroseconds: >= 0 and <= 50_000 } clock
                    && frame.CaptureTimestampUnixMicroseconds > 0)
                {
                    var microseconds = (decimal)((DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10)
                        + clock.RemoteMinusLocalOffsetMicroseconds - frame.CaptureTimestampUnixMicroseconds;
                    if (microseconds is > 0 and <= 60_000_000) age = (double)(microseconds / 1000);
                }
                lock (observationGate)
                {
                    if (observations.Count < 4096)
                        observations.Add(new(now, age, frame.PeerClockEstimate?.UncertaintyMicroseconds / 1000d,
                            hardware?.InitialisationCount ?? 0));
                }
            }
            if (Interlocked.Increment(ref frames) >= 90 && Volatile.Read(ref timedFrames) >= 30)
                sustained.TrySetResult();
        };
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        await capture.StartAsync(new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Display, "synthetic4k", "Synthetic gray4K", width, height),
            Resolution = CaptureResolution.Automatic,
        });
        var offer = await sharer.CreateOfferAsync();
        var answer = await viewer.CreateAnswerAsync(offer);
        await sharer.ApplyRemoteAnswerAsync(answer);
        var initialFrames = sustained.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (observeSteadyState)
            while (!initialFrames.IsCompleted)
            {
                await Task.WhenAny(initialFrames, Task.Delay(TimeSpan.FromSeconds(1)));
                SampleMemory();
            }
        await initialFrames;
        if (observeSteadyState)
        {
            output.WriteLine($"Initial90frames rolling p95={viewer.GetStatistics().CaptureToPresentLatencyP95Ms:F2}ms; "
                + $"target100ms={(viewer.GetStatistics().CaptureToPresentLatencyP95Ms <= 100 ? "MET" : "MISSED")}");
            while (Stopwatch.GetElapsedTime(Interlocked.Read(ref firstAt)) < TimeSpan.FromSeconds(30))
            {
                SampleMemory();
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            SampleMemory();
        }
        await capture.StopAsync();
        var statistics = viewer.GetStatistics();
        Assert.True(sender.IsReady && receiver.IsReady);
        Assert.Equal(0, Volatile.Read(ref invalidFrames));
        Assert.Equal(expectHardware, sharer.Encoder.IsHardware);
        Assert.True(statistics.CaptureToPresentLatencyP95Ms > 0, "Authenticated4K timing produced no usable sample.");
        Assert.InRange(statistics.FrameAgeClockUncertaintyMs, 0, 50);
        Assert.InRange(statistics.VideoPresentationQueueDepth, 0, 1);
        Assert.Equal(width, statistics.RenderedWidth);
        Assert.Equal(height, statistics.RenderedHeight);
        Assert.All(sharer.Adaptive!.Ladder, level => Assert.Equal(1, level.DownscaleFactor));
        Assert.Equal(MediaConnectionState.Connected, sharer.State);
        Assert.Equal(MediaConnectionState.Connected, viewer.State);
        if (expectHardware)
        {
            Assert.True(decoder.NativeFrames > 0 && decoder.Conversions > 0);
            Assert.Equal(0, decoder.LegacyCalls);
        }
        var senderStatistics = sharer.GetStatistics();
        var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref firstAt), Interlocked.Read(ref lastAt)).TotalSeconds;
        output.WriteLine($"Synthetic gray {width}x{height}; actual encrypted frames={frames}; timedFrames={timedFrames}; "
            + $"encoder={sharer.Encoder.Describe()}; deliveredFps={(frames - 1) / elapsed:F2}; "
            + $"captureToPresentP95Ms={statistics.CaptureToPresentLatencyP95Ms:F2}; "
            + $"clockUncertaintyMs={statistics.FrameAgeClockUncertaintyMs:F2}; "
            + $"decodeToPresentP95Ms={statistics.DecodeToRenderLatencyP95Ms:F2}; "
            + $"pending={statistics.VideoPresentationQueueDepth}; replaced={statistics.VideoPresentationFramesReplaced}; "
            + $"deferredNativeFrames={decoder.NativeFrames}; deferredConversions={decoder.Conversions}; legacyDecodeCalls={decoder.LegacyCalls}; "
            + $"targetFps={senderStatistics.TargetFps}; targetBitrateKbps={senderStatistics.TargetBitrateKbps}; "
            + $"captureToEncodeP95Ms={senderStatistics.CaptureToEncodeLatencyP95Ms:F2}; hardwareInitialisations={hardware?.InitialisationCount ?? 0}; "
            + $"allocatedBytes={GC.GetTotalAllocatedBytes(precise: true) - allocated}; "
            + $"GC0/1/2={GC.CollectionCount(0) - collections[0]}/{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]}");
        if (observeSteadyState)
        {
            FourKPresentationObservation[] samples;
            lock (observationGate) samples = observations.ToArray();
            var first = samples[0].At;
            var last = samples[^1].At;
            LogWindow("startupFirst5s", samples.Where(sample => Stopwatch.GetElapsedTime(first, sample.At).TotalSeconds <= 5).ToArray());
            LogWindow("entireSession", samples);
            LogWindow("last5s", samples.Where(sample => Stopwatch.GetElapsedTime(sample.At, last).TotalSeconds <= 5).ToArray());

            void LogWindow(string name, FourKPresentationObservation[] window)
            {
                var ages = window.Where(sample => sample.AgeMs.HasValue).Select(sample => sample.AgeMs!.Value).Order().ToArray();
                var uncertainty = window.Where(sample => sample.UncertaintyMs.HasValue)
                    .Select(sample => sample.UncertaintyMs!.Value).DefaultIfEmpty(double.NaN).Max();
                var duration = Stopwatch.GetElapsedTime(window[0].At, window[^1].At).TotalSeconds;
                var p95 = ages.Length == 0 ? 0 : ages[(int)Math.Ceiling(ages.Length * .95) - 1];
                var privateBytes = memory.Where(sample => sample.At >= window[0].At && sample.At <= window[^1].At)
                    .Select(sample => sample.PrivateBytes).ToArray();
                output.WriteLine($"4K {name}: frames={window.Length}; authenticatedAgeSamples={ages.Length}; "
                    + $"durationSeconds={duration:F2}; fps={(window.Length - 1) / duration:F2}; p95Ms={p95:F2}; "
                    + $"clockUncertaintyMaxMs={uncertainty:F2}; hardwareInitialisations={window[^1].Initialisations}; "
                    + $"privateBytesSamples={privateBytes.Length}; privateBytesStart/Max/End="
                    + (privateBytes.Length == 0 ? "unavailable" : $"{privateBytes[0]}/{privateBytes.Max()}/{privateBytes[^1]}")
                    + $"; target100ms={(ages.Length > 0 && p95 <= 100 ? "MET" : "MISSED")}");
            }
        }
    }

    private sealed record FourKPresentationObservation(long At, double? AgeMs, double? UncertaintyMs, int Initialisations);

    [HardwareVideoFact]
    public void Actual_four_k_MF_stages_report_cold_warm_and_allocation_costs()
    {
        using var encoder = new MediaFoundationH264Encoder();
        using var decoder = new MediaFoundationH264Decoder();
        var source = SyntheticFrame(3840, 2160);
        var format = new List<double>();
        var encode = new List<double>();
        var decode = new List<double>();
        var color = new List<double>();
        for (var index = 0; index < 2; index++)
        {
            var encoded = encoder.EncodeI420(3840, 2160, source, 36_000, 30, index == 0);
            Assert.Single(decoder.Decode(3840, 2160, encoded, index == 0));
            output.WriteLine($"4K MF warmup={index} I420toNV12Ms={encoder.LastFormatConversionMilliseconds:F2} "
                + $"nativeEncodeMs={encoder.LastNativeEncodeMilliseconds:F2} nativeDecodeMs={decoder.LastNativeDecodeMilliseconds:F2} "
                + $"NV12toBGRMs={decoder.LastColorConversionMilliseconds:F2}");
        }
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        for (var index = 0; index < 12; index++)
        {
            var encoded = encoder.EncodeI420(3840, 2160, source, 36_000, 30, index == 6);
            var decoded = Assert.Single(decoder.Decode(3840, 2160, encoded, reset: false));
            Assert.Equal(3840 * 2160 * 3, decoded.Pixels.Length);
            format.Add(encoder.LastFormatConversionMilliseconds);
            encode.Add(encoder.LastNativeEncodeMilliseconds);
            decode.Add(decoder.LastNativeDecodeMilliseconds);
            color.Add(decoder.LastColorConversionMilliseconds);
        }
        output.WriteLine($"Synthetic gray4K MF samples=12 encoder={encoder.Name}; decoder={decoder.Name}; "
            + $"logicalProcessors={Environment.ProcessorCount} serverGC={GCSettings.IsServerGC} latencyMode={GCSettings.LatencyMode}; "
            + $"allocatedBytes={GC.GetTotalAllocatedBytes(precise: true) - allocated}; "
            + $"GC0/1/2={GC.CollectionCount(0) - collections[0]}/{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]}; "
            + $"I420toNV12 min/mean/max={Range(format)}; nativeEncode={Range(encode)}; nativeDecode={Range(decode)}; NV12toBGR={Range(color)}");

        static string Range(List<double> samples) => $"{samples.Min():F2}/{samples.Average():F2}/{samples.Max():F2}ms";
    }

    private static int GetHardwareSoakSeconds()
    {
        var value = Environment.GetEnvironmentVariable("PEERONQ_HARDWARE_VIDEO_SOAK_SECONDS");
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var seconds)
            || (seconds != 0 && seconds is < 30 or > 1800))
            throw new InvalidOperationException("PEERONQ_HARDWARE_VIDEO_SOAK_SECONDS must be 0 or 30..1800.");
        return seconds;
    }

    internal static byte[] SyntheticFrame(int width, int height)
    {
        var image = new byte[width * height * 3 / 2];
        Array.Fill(image, (byte)110, 0, width * height);
        Array.Fill(image, (byte)128, width * height, width * height / 2);
        return image;
    }

    private sealed class ObservedDeferredDecoder(MediaFoundationH264Decoder decoder) : IDeferredH264VideoDecoder
    {
        public string Name => decoder.Name;
        public bool HardwareAccelerated => decoder.HardwareAccelerated;
        public int NativeFrames, Conversions, LegacyCalls;
        public DecodedNv12Frame[] DecodeNv12(int width, int height, byte[] frame, bool reset)
        {
            var images = decoder.DecodeNv12(width, height, frame, reset);
            Interlocked.Add(ref NativeFrames, images.Length);
            return images;
        }
        public byte[] ConvertNv12ToBgr(DecodedNv12Frame frame)
        {
            var pixels = decoder.ConvertNv12ToBgr(frame);
            Interlocked.Increment(ref Conversions);
            return pixels;
        }
        public RemoteVideoFrame[] Decode(int width, int height, byte[] frame, bool reset)
        {
            Interlocked.Increment(ref LegacyCalls);
            return decoder.Decode(width, height, frame, reset);
        }
        public void Dispose() => decoder.Dispose();
    }

    internal sealed class SyntheticVideoCapture(int width = 1920, int height = 1080) : IScreenCaptureSource
    {
        private CancellationTokenSource? _lifetime;
        private Task? _loop;
        public bool IsCapturing { get; private set; }
        public CaptureTargetInfo? Target { get; private set; }
        public event EventHandler<CapturedFrame>? FrameArrived;
        public event EventHandler<CaptureStoppedReason>? CaptureStopped;
        public Task StartAsync(CaptureRequest request, CancellationToken cancellationToken = default)
        {
            Target = request.Target; IsCapturing = true;
            _lifetime = new CancellationTokenSource();
            _loop = Task.Run(async () =>
            {
                long sequence = 0;
                try
                {
                    // Keep source cadence independent of frame creation and timer overshoot.
                    // Per-frame Delay(33) accumulates both costs and undersupplies a 30 fps test.
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 30));
                    while (!_lifetime.IsCancellationRequested)
                    {
                        FrameArrived?.Invoke(this, new CapturedFrame
                        {
                            Width = width, Height = height, I420 = SyntheticFrame(width, height),
                            Timestamp = TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency),
                            SequenceNumber = ++sequence,
                            CaptureTimestampUnixMicroseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
                            BufferOwnershipCanTransfer = true,
                        });
                        if (!await timer.WaitForNextTickAsync(_lifetime.Token)) break;
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            });
            return Task.CompletedTask;
        }
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsCapturing = false;
            if (_lifetime is not null) await _lifetime.CancelAsync();
            if (_loop is not null) await _loop;
            CaptureStopped?.Invoke(this, CaptureStoppedReason.StoppedByUser);
        }
        public async ValueTask DisposeAsync() { await StopAsync(); _lifetime?.Dispose(); }
    }

    private sealed class TestVideoIdentity(HybridDeviceIdentityService service, VideoSecretStore store, string fingerprint) : IDisposable
    {
        public HybridDeviceIdentityService Service { get; } = service;
        public string Fingerprint { get; } = fingerprint;
        public static async Task<TestVideoIdentity> CreateAsync(string name)
        {
            using var legacy = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = legacy.ExportSubjectPublicKeyInfo();
            var store = new VideoSecretStore();
            await store.SetAsync(DeviceProvisioningService.SigningKeyName, legacy.ExportPkcs8PrivateKey());
            var identity = DeviceIdentity.Create(name) with { PublicKey = Convert.ToBase64String(publicKey) };
            var service = new HybridDeviceIdentityService(store, identity);
            await service.GetPublicIdentityAsync();
            return new(service, store, Convert.ToHexStringLower(SHA256.HashData(publicKey)));
        }
        public void Dispose() { Service.Dispose(); store.Clear(); }
    }

    private sealed class VideoSecretStore : IDeviceSecretStore
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
        public Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(name, out var value) ? value.ToArray() : null);
        public Task SetAsync(string name, byte[] secret, CancellationToken cancellationToken = default)
        { _values[name] = secret.ToArray(); return Task.CompletedTask; }
        public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
        { if (_values.Remove(name, out var value)) CryptographicOperations.ZeroMemory(value); return Task.CompletedTask; }
        public void Clear() { foreach (var value in _values.Values) CryptographicOperations.ZeroMemory(value); _values.Clear(); }
    }
}
