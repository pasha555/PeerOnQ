using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public class VideoFrameTelemetryCodecTests
{
    [Fact]
    public void Authenticated_video_envelope_round_trips_capture_time_sequence_and_vp8()
    {
        var vp8 = Enumerable.Range(0, 257).Select(value => (byte)value).ToArray();

        var envelope = VideoFrameTelemetryCodec.Encode(
            vp8,
            captureTimestampUnixMicroseconds: 1_787_561_600_123_456,
            sequenceNumber: 42);

        Assert.True(VideoFrameTelemetryCodec.TryDecode(
            envelope,
            out var captureTimestamp,
            out var sequenceNumber,
            out var decoded));
        Assert.Equal(1_787_561_600_123_456, captureTimestamp);
        Assert.Equal(42, sequenceNumber);
        Assert.Equal(vp8, decoded);
    }

    [Fact]
    public void Video_envelope_rejects_truncation_version_changes_and_invalid_lengths()
    {
        var envelope = VideoFrameTelemetryCodec.Encode([1, 2, 3], 1_787_561_600_123_456, 1);

        Assert.False(VideoFrameTelemetryCodec.TryDecode(envelope.AsSpan(0, envelope.Length - 1), out _, out _, out _));
        envelope[4]++;
        Assert.False(VideoFrameTelemetryCodec.TryDecode(envelope, out _, out _, out _));
    }
}

public class BulkDataFrameCodecTests
{
    [Fact]
    public void Ordered_bounded_fragments_reassemble_the_exact_encrypted_record()
    {
        var record = Enumerable.Range(0, 180_000).Select(value => (byte)(value % 251)).ToArray();
        var reassembler = new BulkDataFrameReassembler();
        byte[]? completed = null;

        for (var offset = 0; offset < record.Length; offset += 64 * 1024)
        {
            var count = Math.Min(64 * 1024, record.Length - offset);
            var fragment = BulkDataFrameCodec.Encode(record, recordId: 7, offset, count);
            var accepted = reassembler.TryAccept(fragment, out completed, out var error);

            Assert.Null(error);
            Assert.Equal(offset + count == record.Length, accepted);
        }

        Assert.Equal(record, completed);
    }

    [Fact]
    public void Fragment_reassembly_rejects_gaps_record_switches_and_invalid_versions()
    {
        var record = new byte[4 * 1024];
        var reassembler = new BulkDataFrameReassembler();
        var first = BulkDataFrameCodec.Encode(record, recordId: 1, fragmentOffset: 0, fragmentLength: 1024);
        Assert.False(reassembler.TryAccept(first, out _, out var firstError));
        Assert.Null(firstError);

        var gap = BulkDataFrameCodec.Encode(record, recordId: 1, fragmentOffset: 2048, fragmentLength: 1024);
        Assert.False(reassembler.TryAccept(gap, out _, out var gapError));
        Assert.Equal("invalid_bulk_fragment_order", gapError);

        var switched = BulkDataFrameCodec.Encode(record, recordId: 2, fragmentOffset: 1024, fragmentLength: 1024);
        Assert.False(reassembler.TryAccept(switched, out _, out var switchedError));
        Assert.Equal("invalid_bulk_fragment_order", switchedError);

        first[4]++;
        Assert.False(reassembler.TryAccept(first, out _, out var versionError));
        Assert.Equal("invalid_bulk_fragment", versionError);
    }
}

public class VideoFrameQueueTests
{
    private static CapturedFrame Frame(long sequence) => new()
    {
        Width = 640,
        Height = 360,
        I420 = new byte[640 * 360 * 3 / 2],
        Timestamp = TimeSpan.FromMilliseconds(sequence * 33),
        SequenceNumber = sequence,
    };

    [Fact]
    public void The_queue_never_grows_past_its_capacity()
    {
        var queue = new VideoFrameQueue(capacity: 3);

        for (var i = 0; i < 1000; i++)
        {
            queue.TryEnqueue(Frame(i));
        }

        Assert.Equal(3, queue.Count);
        Assert.Equal(1000, queue.Enqueued);
        Assert.Equal(997, queue.Dropped);
    }

    [Fact]
    public void Backpressure_keeps_the_newest_frames()
    {
        var queue = new VideoFrameQueue(capacity: 2);

        queue.TryEnqueue(Frame(1));
        queue.TryEnqueue(Frame(2));
        var accepted = queue.TryEnqueue(Frame(3));

        Assert.False(accepted);
        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal(2, first!.SequenceNumber);
        Assert.Equal(3, second!.SequenceNumber);
    }

    [Fact]
    public void Dequeue_on_an_empty_queue_reports_no_frame()
    {
        var queue = new VideoFrameQueue();

        Assert.False(queue.TryDequeue(out var frame));
        Assert.Null(frame);
    }

    [Fact]
    public void Initial_frame_stays_queued_until_media_and_secure_transport_are_ready()
    {
        var queue = new VideoFrameQueue(capacity: 1);
        queue.TryEnqueue(Frame(1));

        Assert.False(WebRtcMediaSession.TryDequeueFrameForEncoding(
            queue,
            MediaConnectionState.New,
            secureTransportEstablished: false,
            out _));
        Assert.False(WebRtcMediaSession.TryDequeueFrameForEncoding(
            queue,
            MediaConnectionState.Connected,
            secureTransportEstablished: false,
            out _));
        Assert.Equal(1, queue.Count);

        Assert.True(WebRtcMediaSession.TryDequeueFrameForEncoding(
            queue,
            MediaConnectionState.Connected,
            secureTransportEstablished: true,
            out var ready));
        Assert.Equal(1, ready!.SequenceNumber);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VideoFrameQueue(0));
    }

    [Fact]
    public void Concurrent_producers_and_consumers_stay_consistent()
    {
        var queue = new VideoFrameQueue(capacity: 4);
        var produced = 0;

        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 250; i++)
            {
                queue.TryEnqueue(Frame(i));
                Interlocked.Increment(ref produced);
                queue.TryDequeue(out _);
            }
        });

        Assert.Equal(produced, queue.Enqueued);
        Assert.True(queue.Count <= queue.Capacity);
        Assert.Equal(queue.Enqueued, queue.Dropped + queue.Dequeued + queue.Count);
    }
}

public class FrameRateLimiterTests
{
    [Fact]
    public void The_limiter_enforces_the_target_frame_rate()
    {
        var time = new ManualTimeProvider();
        var limiter = new FrameRateLimiter(targetFps: 30, time);

        Assert.True(limiter.ShouldAccept());   // first frame always passes
        Assert.False(limiter.ShouldAccept());  // 0 ms later: too soon

        time.Advance(TimeSpan.FromMilliseconds(34));
        Assert.True(limiter.ShouldAccept());

        Assert.Equal(2, limiter.Accepted);
        Assert.Equal(1, limiter.Skipped);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void Supported_frame_rates_are_kept(int fps)
    {
        Assert.Equal(fps, new FrameRateLimiter(fps).TargetFps);
    }

    [Fact]
    public void Absurd_frame_rates_are_clamped()
    {
        Assert.Equal(1, new FrameRateLimiter(0).TargetFps);
        Assert.Equal(120, new FrameRateLimiter(10_000).TargetFps);
    }
}

public class MediaStatisticsCollectorTests
{
    [Fact]
    public void Counters_reflect_what_actually_happened()
    {
        var time = new ManualTimeProvider();
        var stats = new MediaStatisticsCollector(TimeSpan.FromSeconds(2), time);

        for (var i = 0; i < 60; i++)
        {
            stats.FrameCaptured();
            stats.FrameEncoded(
                bytes: 5_000,
                width: 1280,
                height: 720,
                sourceWidth: 2560,
                sourceHeight: 1440,
                requestedWidth: 1920,
                requestedHeight: 1080);
        }

        stats.FrameDropped();

        var snapshot = stats.Snapshot();

        Assert.Equal(60, snapshot.FramesCaptured);
        Assert.Equal(60, snapshot.FramesEncoded);
        Assert.Equal(1, snapshot.FramesDropped);
        Assert.Equal(300_000, snapshot.BytesSent);
        Assert.Equal(1280, snapshot.Width);
        Assert.Equal(720, snapshot.Height);
        Assert.Equal(2560, snapshot.SourceWidth);
        Assert.Equal(1440, snapshot.SourceHeight);
        Assert.Equal(1920, snapshot.RequestedWidth);
        Assert.Equal(1080, snapshot.RequestedHeight);
        Assert.Equal(1280, snapshot.EncodedWidth);
        Assert.Equal(720, snapshot.EncodedHeight);
        Assert.Equal(30, snapshot.CurrentFps); // 60 frames inside a 2 s window
        Assert.Equal(1200, snapshot.CurrentBitrateKbps); // 300 kB over 2 s
    }

    [Fact]
    public void Decode_and_render_dimensions_are_reported_separately()
    {
        var stats = new MediaStatisticsCollector();

        stats.FrameDecoded(bytes: 2_000, width: 1600, height: 900);
        var decoded = stats.Snapshot();
        Assert.Equal(1600, decoded.DecodedWidth);
        Assert.Equal(900, decoded.DecodedHeight);
        Assert.Equal(0, decoded.RenderedWidth);

        stats.FrameRendered();
        var rendered = stats.Snapshot();
        Assert.Equal(1600, rendered.RenderedWidth);
        Assert.Equal(900, rendered.RenderedHeight);
    }

    [Fact]
    public void Per_frame_latency_distributions_report_p50_p95_and_p99()
    {
        var stats = new MediaStatisticsCollector();
        var samples = new[] { 10d, 20d, 30d, 40d, 50d };

        foreach (var sample in samples)
        {
            stats.FrameEncoded(1_000, 1280, 720, captureToEncodeLatencyMs: sample);
            stats.FrameDecoded(1_000, 1280, 720);
            stats.FrameRendered(
                sample,
                1280,
                720,
                captureToPresentLatencyMs: sample + 100,
                clockUncertaintyMs: 5);
        }
        stats.FrameEncoded(1_000, 1280, 720);
        stats.FrameDecoded(1_000, 1280, 720);
        stats.FrameRendered();

        var snapshot = stats.Snapshot();
        Assert.Equal(30, snapshot.CaptureToEncodeLatencyP50Ms);
        Assert.Equal(50, snapshot.CaptureToEncodeLatencyP95Ms);
        Assert.Equal(50, snapshot.CaptureToEncodeLatencyP99Ms);
        Assert.Equal(30, snapshot.DecodeToRenderLatencyP50Ms);
        Assert.Equal(50, snapshot.DecodeToRenderLatencyP95Ms);
        Assert.Equal(50, snapshot.DecodeToRenderLatencyP99Ms);
        Assert.Equal(130, snapshot.CaptureToPresentLatencyP50Ms);
        Assert.Equal(150, snapshot.CaptureToPresentLatencyP95Ms);
        Assert.Equal(150, snapshot.CaptureToPresentLatencyP99Ms);
        Assert.Equal(5, snapshot.FrameAgeClockUncertaintyMs);
        Assert.Equal(snapshot.CaptureToEncodeLatencyP95Ms, snapshot.CaptureToEncodeLatencyMs);
        Assert.Equal(snapshot.DecodeToRenderLatencyP95Ms, snapshot.DecodeToRenderLatencyMs);
    }

    [Fact]
    public void Input_latency_distributions_report_p50_p95_p99_and_clock_uncertainty()
    {
        var stats = new MediaStatisticsCollector();
        foreach (var sample in new[] { 5d, 10d, 15d, 20d, 25d })
            stats.InputInjected(sample, clockUncertaintyMs: 2);

        var snapshot = stats.Snapshot();
        Assert.Equal(15, snapshot.InputToInjectionLatencyP50Ms);
        Assert.Equal(25, snapshot.InputToInjectionLatencyP95Ms);
        Assert.Equal(25, snapshot.InputToInjectionLatencyP99Ms);
        Assert.Equal(2, snapshot.InputClockUncertaintyMs);
    }

    [Fact]
    public void Stale_input_latency_pressure_expires_instead_of_throttling_idle_transfers_forever()
    {
        var time = new ManualTimeProvider();
        var stats = new MediaStatisticsCollector(timeProvider: time);
        stats.InputInjected(60, clockUncertaintyMs: 2);

        time.Advance(TimeSpan.FromSeconds(6));

        var snapshot = stats.Snapshot();
        Assert.Equal(0, snapshot.InputToInjectionLatencyP95Ms);
        Assert.Equal(0, snapshot.InputClockUncertaintyMs);
    }

    [Fact]
    public void Quiet_desktop_latency_expires_without_erasing_session_totals()
    {
        var time = new ManualTimeProvider();
        var stats = new MediaStatisticsCollector(timeProvider: time);
        stats.FrameEncoded(1_000, 1920, 1080, captureToEncodeLatencyMs: 150);
        stats.FrameDecoded(1_000, 1920, 1080);
        stats.FrameRendered(80, 1920, 1080, captureToPresentLatencyMs: 250, clockUncertaintyMs: 2);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(250, stats.Snapshot().CaptureToPresentLatencyP95Ms);
        time.Advance(TimeSpan.FromMilliseconds(1));

        var snapshot = stats.Snapshot();
        Assert.Equal(0, snapshot.CaptureToEncodeLatencyP99Ms);
        Assert.Equal(0, snapshot.DecodeToRenderLatencyP95Ms);
        Assert.Equal(0, snapshot.CaptureToPresentLatencyP95Ms);
        Assert.Equal(0, snapshot.FrameAgeClockUncertaintyMs);
        Assert.Equal(1, snapshot.FramesEncoded);
        Assert.Equal(1, snapshot.FramesRendered);
        Assert.Equal(1920, snapshot.RenderedWidth);
    }

    [Fact]
    public void Sparse_fresh_samples_do_not_keep_an_old_latency_spike_alive()
    {
        var time = new ManualTimeProvider();
        var stats = new MediaStatisticsCollector(timeProvider: time);
        stats.FrameEncoded(1_000, 1920, 1080, captureToEncodeLatencyMs: 150);
        stats.FrameRendered(80, captureToPresentLatencyMs: 250, clockUncertaintyMs: 2);
        stats.InputInjected(120, 2);

        // A quiet desktop/click every few seconds must not need 120 new samples to recover.
        for (var second = 0; second < 6; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            stats.FrameEncoded(1_000, 1920, 1080, captureToEncodeLatencyMs: 8);
            stats.FrameRendered(5, captureToPresentLatencyMs: 25, clockUncertaintyMs: 1);
            stats.InputInjected(10, 1);
        }

        var snapshot = stats.Snapshot();
        Assert.Equal(8, snapshot.CaptureToEncodeLatencyP99Ms);
        Assert.Equal(5, snapshot.DecodeToRenderLatencyP99Ms);
        Assert.Equal(25, snapshot.CaptureToPresentLatencyP99Ms);
        Assert.Equal(10, snapshot.InputToInjectionLatencyP99Ms);
        Assert.Equal(1, snapshot.FrameAgeClockUncertaintyMs);
        Assert.Equal(1, snapshot.InputClockUncertaintyMs);
    }

    [Fact]
    public void Rates_decay_once_the_window_moves_on()
    {
        var time = new ManualTimeProvider();
        var stats = new MediaStatisticsCollector(TimeSpan.FromSeconds(1), time);

        stats.FrameEncoded(1_000, 1280, 720);
        Assert.True(stats.Snapshot().CurrentFps > 0);

        time.Advance(TimeSpan.FromSeconds(5));

        var snapshot = stats.Snapshot();
        Assert.Equal(0, snapshot.CurrentFps);
        Assert.Equal(0, snapshot.CurrentBitrateKbps);
        Assert.Equal(1, snapshot.FramesEncoded); // totals stay
    }

    [Fact]
    public void An_untouched_collector_reports_zeroes()
    {
        var snapshot = new MediaStatisticsCollector().Snapshot();

        Assert.Equal(0, snapshot.FramesEncoded);
        Assert.Equal(0, snapshot.CurrentFps);
        Assert.Equal(0, snapshot.BytesSent);
    }
}

/// <summary>A TimeProvider the tests can move by hand.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
