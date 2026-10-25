using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PeerOnQ.Media.Codecs;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.Media.Tests;

/// <summary>
/// 0.9.87 field hang on LNK-363 (i5-5300U, 2C/4T): one libvpx call did not return while a lock
/// shared with the rate setters, key-frame requests and Dispose was held, so capture, quality
/// changes, picture requests and session close all blocked. These tests pin the replacement
/// design: one thread per native context, lock-free setters, a stall watchdog and a non-blocking
/// Dispose.
/// </summary>
public sealed class Vp8EncoderThreadingTests(ITestOutputHelper output)
{
    private const int Width = 320;
    private const int Height = 180;

    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 1)]
    [InlineData(4, false, 1)]
    [InlineData(6, false, 3)]
    [InlineData(8, false, 4)]
    [InlineData(16, false, 8)]
    [InlineData(32, false, 8)]
    [InlineData(16, true, 1)]
    public void Small_machines_and_replacements_after_a_stall_encode_single_threaded(
        int processors, bool afterStall, int expected) =>
        Assert.Equal(expected, Vp8ScreenEncoder.EncoderThreadsFor(processors, afterStall));

    [Fact]
    public void A_two_core_four_thread_machine_starts_a_single_threaded_encoder()
    {
        using var encoder = new Vp8ScreenEncoder(2_000, 30, null, processorCount: 4);
        AssertKeyFrame(encoder.EncodeI420(Width, Height, Checkerboard(Width, Height, 0)));
        Assert.Equal(1, encoder.GetDiagnostics().Threads);
    }

    [Fact]
    public void Setters_and_picture_requests_never_wait_for_a_stuck_native_call_and_the_encoder_is_replaced()
    {
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var block = 0;
        var logger = new ListLogger();
        var encoder = new Vp8ScreenEncoder(2_000, 30, null, logger: logger,
            stallTimeout: TimeSpan.FromMilliseconds(300),
            beforeNativeCall: operation =>
            {
                if (operation == "vpx_codec_encode" && Interlocked.Exchange(ref block, 0) == 1)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            },
            processorCount: 16);
        try
        {
            var frame = Checkerboard(Width, Height, 0);
            using var decoder = new VpxVideoEncoder();
            AssertKeyFrame(encoder.EncodeI420(Width, Height, frame));
            Assert.Equal(8, encoder.GetDiagnostics().Threads);

            Volatile.Write(ref block, 1);
            var stalled = Task.Run(() => encoder.EncodeI420(Width, Height, frame));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "The encode never reached libvpx.");

            // Everything the quality loop, capture thread and RTCP thread call returns at once.
            var elapsed = Stopwatch.StartNew();
            encoder.SetTargetKbps(1_000);
            encoder.SetTargetFps(15);
            encoder.ForceKeyFrame();
            Assert.Equal(1_000, encoder.TargetKbps);
            var busy = encoder.GetDiagnostics();
            elapsed.Stop();
            Assert.True(elapsed.ElapsedMilliseconds < 200, $"Control calls took {elapsed.ElapsedMilliseconds} ms.");
            Assert.StartsWith("busy in vpx_codec_encode", busy.State, StringComparison.Ordinal);

            // The caller gives up after the stall timeout instead of hanging with the native call.
            Assert.True(stalled.Wait(TimeSpan.FromSeconds(10)), "The stalled encode did not return.");
            Assert.Empty(stalled.Result);
            Assert.Equal(1, encoder.AbandonedEncoders);
            Assert.Contains(logger.Messages, message => message.Contains("vpx_codec_encode did not return", StringComparison.Ordinal)
                                                        && message.Contains("8 libvpx thread(s)", StringComparison.Ordinal));

            // A fresh single-threaded encoder continues with a key frame at the new targets.
            var recovered = encoder.EncodeI420(Width, Height, frame);
            AssertKeyFrame(recovered);
            Assert.Single(decoder.DecodeVideo(recovered, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8));
            Assert.Equal(1, encoder.GetDiagnostics().Threads);
            Assert.Equal(2, encoder.InitialisationCount);
            Assert.Equal((uint)(90_000 / 15), encoder.LastSubmittedDuration);
            Assert.StartsWith("idle", encoder.GetDiagnostics().State, StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            encoder.Dispose();
        }

        // The abandoned thread released its own context once its call returned.
        Assert.True(SpinWait.SpinUntil(
            () => logger.Messages.Any(message => message.Contains("abandoned VP8 encoder call", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Dispose_returns_promptly_while_a_native_call_is_stuck()
    {
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var block = 0;
        var encoder = new Vp8ScreenEncoder(2_000, 30, null,
            stallTimeout: TimeSpan.FromSeconds(30),
            beforeNativeCall: operation =>
            {
                if (operation == "vpx_codec_encode" && Interlocked.Exchange(ref block, 0) == 1)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            });
        var frame = Checkerboard(Width, Height, 0);
        AssertKeyFrame(encoder.EncodeI420(Width, Height, frame));
        Volatile.Write(ref block, 1);
        var stuck = Task.Run(() => encoder.EncodeI420(Width, Height, frame));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));

        var elapsed = Stopwatch.StartNew();
        encoder.Dispose();
        encoder.Dispose();
        elapsed.Stop();
        Assert.True(elapsed.ElapsedMilliseconds < 1_500, $"Dispose took {elapsed.ElapsedMilliseconds} ms.");
        Assert.Throws<ObjectDisposedException>(() => encoder.EncodeI420(Width, Height, frame));
        // Setters stay harmless after disposal (a late quality change must not throw).
        encoder.SetTargetKbps(500);
        encoder.ForceKeyFrame();

        release.Set();
        Assert.True(stuck.Wait(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Static_content_gets_no_periodic_key_frame_before_the_five_second_safety_net()
    {
        const int fps = 30;
        using var encoder = new Vp8ScreenEncoder(2_000, fps);
        var frame = Checkerboard(Width, Height, 0);
        var keyFrames = new List<int>();
        for (var index = 0; index <= fps * Vp8ScreenEncoder.KeyFrameSafetyNetSeconds; index++)
        {
            var encoded = encoder.EncodeI420(Width, Height, frame);
            if (encoded.Length > 0 && (encoded[0] & 1) == 0) keyFrames.Add(index);
        }

        // 0.9.87 sent a key frame every two seconds (frame 60, 120, ...).
        Assert.Equal(0, keyFrames[0]);
        Assert.DoesNotContain(keyFrames, index => index is > 0 and < fps * Vp8ScreenEncoder.KeyFrameSafetyNetSeconds);
        Assert.Equal((uint)150, Vp8ScreenEncoder.KeyFrameInterval(30));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void Rapid_rate_changes_and_picture_requests_from_other_threads_never_stall_the_encoder(int processors)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("PEERONQ_VP8_STRESS_SECONDS"), out var configured)
            ? Math.Max(1, configured)
            : 4;
        const int width = 1920;
        const int height = 1080;
        using var encoder = new Vp8ScreenEncoder(6_000, 30, null, processorCount: processors);
        using var decoder = new VpxVideoEncoder();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var frames = new[] { Checkerboard(width, height, 0), Checkerboard(width, height, 1) };
        var errors = new ConcurrentQueue<Exception>();
        var changers = Enumerable.Range(0, 3).Select(worker => Task.Run(() =>
        {
            var random = new Random(worker);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    encoder.SetTargetFps(random.Next(5, 61));
                    encoder.SetTargetKbps(random.Next(500, 12_000));
                    if (random.Next(10) == 0) encoder.ForceKeyFrame();
                    _ = encoder.GetDiagnostics();
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }

                Thread.Sleep(random.Next(0, 3));
            }
        })).ToArray();

        var encoded = 0;
        var decodedImages = 0;
        while (!stop.IsCancellationRequested)
        {
            var encodedFrame = encoder.EncodeI420(width, height, frames[encoded % 2]);
            encoded++;
            if (encodedFrame.Length > 0)
                decodedImages += decoder.DecodeVideo(encodedFrame, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8).Count();
        }

        Task.WaitAll(changers, TimeSpan.FromSeconds(10));
        var diagnostics = encoder.GetDiagnostics();
        output.WriteLine($"processors={processors} threads={diagnostics.Threads} encoded={encoded} decoded={decodedImages} " +
                         $"p50={diagnostics.EncodeP50Ms:0.0}ms p95={diagnostics.EncodeP95Ms:0.0}ms reconfigurations={encoder.ReconfigurationCount}");
        Assert.Empty(errors);
        Assert.Equal(0, encoder.AbandonedEncoders);
        Assert.True(encoded >= seconds * 5, $"Only {encoded} frames were encoded in {seconds} s.");
        Assert.True(decodedImages > 0);
        Assert.True(diagnostics.EncodeP95Ms > 0);
    }

    private static byte[] Checkerboard(int width, int height, int phase)
    {
        var frame = new byte[width * height * 3 / 2];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            frame[(y * width) + x] = (((x / 8) + (y / 8) + phase) & 1) == 0 ? (byte)235 : (byte)16;
        Array.Fill(frame, (byte)128, width * height, frame.Length - (width * height));
        return frame;
    }

    private static void AssertKeyFrame(byte[] frame)
    {
        Assert.NotEmpty(frame);
        Assert.Equal(0, frame[0] & 1);
    }

    private sealed class ListLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _messages.Enqueue(formatter(state, exception));
    }
}
