using System.Collections.Concurrent;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Codecs;
using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class LatestDecodedVideoWorkerTests
{
    [Fact]
    public async Task Deferred_NV12_color_work_keeps_native_layout_and_zeroes_replaced_owned_planes()
    {
        using var release = new ManualResetEventSlim();
        var converting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new TaskCompletionSource<DecodedVideoPresentation>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new LatestDecodedVideoWorker(frame =>
        {
            var native = Assert.IsType<DecodedNv12Frame>(frame.Nv12);
            Assert.Same(native.Pixels, frame.Planes);
            Assert.True(native.HasValidLayout);
            Assert.Equal(8, native.Stride);
            Assert.Equal(6, native.BufferHeight);
            if (frame.SequenceNumber == 1) { converting.TrySetResult(); release.Wait(); }
            return new byte[6 * 4 * 3];
        }, (frame, _) => { if (frame.SequenceNumber == 3) latest.TrySetResult(frame); });
        var active = NativeFrame(1);
        var pending = NativeFrame(2);
        try
        {
            worker.Enqueue(active);
            await converting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.Enqueue(pending);
            worker.Enqueue(NativeFrame(3));
            Assert.Equal(1, worker.PendingFrames);
            Assert.All(pending.Planes, value => Assert.Equal(0, value));
            release.Set();
            var presented = await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3000, presented.CaptureTimestampUnixMicroseconds);
            Assert.Equal(300, presented.DecodedTimestamp);
            Assert.Equal(new PeerClockEstimate(50_000, 1_000), presented.PeerClockEstimate);
            worker.Close();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(active.Planes, value => Assert.Equal(0, value));
            Assert.All(presented.Planes, value => Assert.Equal(0, value));
        }
        finally { release.Set(); }

        static DecodedVideoPresentation NativeFrame(int sequence)
        {
            var image = new DecodedNv12Frame(6, 4, 8, 6, Enumerable.Repeat((byte)sequence, 72).ToArray());
            return new(6, 4, image.Pixels, sequence * 100, sequence * 1000, sequence,
                new PeerClockEstimate(50_000, 1_000), image);
        }
    }

    [Fact]
    public async Task Replacing_decoded_planes_preserves_real_VP8_interframe_references()
    {
        using var encoder = new Vp8ScreenEncoder(4000, 30);
        using var decoder = new Vp8ScreenDecoder();
        using var reference = new Vp8ScreenDecoder();
        using var release = new ManualResetEventSlim();
        var converting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastPresented = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presented = new ConcurrentQueue<long>();
        await using var worker = new LatestDecodedVideoWorker(frame =>
        {
            if (frame.SequenceNumber == 1) { converting.TrySetResult(); release.Wait(); }
            return I420BgrConverter.Convert(frame.Planes, frame.Width, frame.Height);
        }, (frame, pixels) =>
        {
            presented.Enqueue(frame.SequenceNumber);
            if (frame.SequenceNumber == 10) lastPresented.TrySetResult(pixels);
        });
        byte[]? expectedLast = null;
        var interframes = 0;
        try
        {
            for (var sequence = 1; sequence <= 10; sequence++)
            {
                var image = new byte[320 * 240 * 3 / 2];
                Array.Fill(image, (byte)110, 0, 320 * 240);
                Array.Fill(image, (byte)128, 320 * 240, 320 * 240 / 2);
                image[100] = (byte)(110 + sequence);
                var encoded = encoder.EncodeI420(320, 240, image);
                if ((encoded[0] & 1) != 0) interframes++;
                var planes = Assert.Single(decoder.DecodeI420(encoded));
                expectedLast = Assert.Single(reference.Decode(encoded)).Sample;
                worker.Enqueue(new DecodedVideoPresentation(320, 240, planes.Sample, sequence,
                    sequence * 1000, sequence, null));
                if (sequence == 1) await converting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.True(interframes > 0);
            Assert.Equal(1, worker.PendingFrames);
            release.Set();
            Assert.Equal(expectedLast, await lastPresented.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new long[] { 1, 10 }, presented);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Slow_presentation_never_holds_the_receive_admission_gate()
    {
        using var release = new ManualResetEventSlim();
        var presenting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestPresented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = new LatestDecodedVideoWorker(_ => new byte[16], (frame, _) =>
        {
            if (frame.SequenceNumber == 1) { presenting.TrySetResult(); release.Wait(); }
            if (frame.SequenceNumber == 3) latestPresented.TrySetResult();
        });
        Task? incoming = null;
        try
        {
            worker.Enqueue(Frame(1));
            await presenting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            incoming = Task.Run(() => { worker.Enqueue(Frame(2)); worker.Enqueue(Frame(3)); });
            await incoming.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(1, worker.PendingFrames);
            release.Set();
            await latestPresented.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); if (incoming is not null) await incoming.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Slow_conversion_keeps_only_the_latest_independent_image_and_preserves_metadata()
    {
        using var release = new ManualResetEventSlim();
        var converting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<DecodedVideoPresentation>();
        await using var worker = new LatestDecodedVideoWorker(frame =>
        {
            if (frame.SequenceNumber == 1) { converting.TrySetResult(); release.Wait(); }
            return new byte[16];
        }, (frame, _) => { delivered.Enqueue(frame); if (frame.SequenceNumber == 16) finished.TrySetResult(); });
        var frames = Enumerable.Range(1, 16).Select(number => Frame(number)).ToArray();
        try
        {
            worker.Enqueue(frames[0]);
            // Generous bounds: on a loaded CI runner the first conversion can start late; the
            // dropped/latest-frame assertions below are what this test checks.
            await converting.Task.WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var frame in frames.Skip(1)) worker.Enqueue(frame);
            Assert.Equal(1, worker.PendingFrames);
            Assert.Equal(14, worker.DroppedFrames);
            Assert.All(frames.Skip(1).Take(14), frame => Assert.All(frame.Planes, value => Assert.Equal(0, value)));
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(new long[] { 1, 16 }, delivered.Select(frame => frame.SequenceNumber));
            var last = delivered.Last();
            Assert.Equal(1600, last.DecodedTimestamp);
            Assert.Equal(16000, last.CaptureTimestampUnixMicroseconds);
            Assert.Equal(new PeerClockEstimate(50_000, 1_000), last.PeerClockEstimate);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Resize_or_codec_clear_prevents_inflight_old_pixels_from_being_presented(bool resize)
    {
        using var release = new ManualResetEventSlim();
        var converting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new ConcurrentQueue<long>();
        await using var worker = new LatestDecodedVideoWorker(frame =>
        {
            if (frame.SequenceNumber == 1) { converting.TrySetResult(); release.Wait(); }
            return new byte[16];
        }, (frame, _) => { delivered.Enqueue(frame.SequenceNumber); finished.TrySetResult(); });
        try
        {
            worker.Enqueue(Frame(1));
            await converting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = Frame(2);
            worker.Enqueue(pending);
            if (!resize) worker.Clear();
            worker.Enqueue(Frame(3, resize ? 4 : 2));
            Assert.All(pending.Planes, value => Assert.Equal(0, value));
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new long[] { 3 }, delivered);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Close_immediately_zeroes_pending_planes_and_never_presents_old_or_new_frames()
    {
        using var release = new ManualResetEventSlim();
        var converting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = 0;
        await using var worker = new LatestDecodedVideoWorker(_ =>
        {
            converting.TrySetResult(); release.Wait(); return new byte[16];
        }, (_, _) => Interlocked.Increment(ref delivered));
        var active = Frame(1);
        var pending = Frame(2);
        var afterClose = Frame(3);
        try
        {
            worker.Enqueue(active);
            await converting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.Enqueue(pending);
            worker.Close();
            Assert.All(pending.Planes, value => Assert.Equal(0, value));
            Assert.Equal(0, worker.PendingFrames);
            worker.Enqueue(afterClose);
            Assert.All(afterClose.Planes, value => Assert.Equal(0, value));
            release.Set();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(active.Planes, value => Assert.Equal(0, value));
            Assert.Equal(0, Volatile.Read(ref delivered));
        }
        finally { release.Set(); }
    }

    private static DecodedVideoPresentation Frame(int sequence, int width = 2) => new(
        width, 2, Enumerable.Repeat((byte)sequence, width * 3).ToArray(),
        sequence * 100, sequence * 1000, sequence, new PeerClockEstimate(50_000, 1_000));
}
