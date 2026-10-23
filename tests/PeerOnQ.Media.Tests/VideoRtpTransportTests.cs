using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class VideoRtpPacketizationTests
{
    [Fact]
    public void Packets_match_the_sipsorcery_vp8_layout_older_viewers_reassemble()
    {
        var frame = Enumerable.Range(0, 2_500).Select(value => (byte)value).ToArray();

        var packets = VideoRtpPacketizer.Packetize(frame);

        Assert.Equal(3, packets.Length);
        Assert.Equal([1_201, 1_201, 101], packets.Select(packet => packet.Length));
        Assert.Equal(VideoRtpPacketizer.StartDescriptor, packets[0][0]);
        Assert.All(packets.Skip(1), packet => Assert.Equal(VideoRtpPacketizer.ContinuationDescriptor, packet[0]));
        Assert.Equal(frame, packets.SelectMany(packet => packet.Skip(1)).ToArray());
        Assert.Empty(VideoRtpPacketizer.Packetize([]));
    }

    [Fact]
    public void Retransmission_records_round_trip_and_reject_malformed_input()
    {
        var media = new byte[] { VideoRtpPacketizer.ContinuationDescriptor, 9, 8, 7 };
        var record = VideoRetransmissionPayload.Encode(65_535, 65_530, 0xDEADBEEF, true, media);

        Assert.True(VideoRetransmissionPayload.IsRetransmission(record));
        Assert.False(VideoRtpPacketizer.IsMediaDescriptor(record[0]));
        Assert.True(VideoRetransmissionPayload.TryDecode(record, out var repaired, out var original,
            out var timestamp, out var marker, out var payload));
        Assert.Equal((65_535, 65_530, 0xDEADBEEFu, true), ((int)repaired, (int)original, timestamp, marker));
        Assert.Equal(media, payload.ToArray());

        var badVersion = (byte[])record.Clone();
        badVersion[1] = 2;
        var badFlags = (byte[])record.Clone();
        badFlags[10] = 2;
        var badInner = (byte[])record.Clone();
        badInner[VideoRetransmissionPayload.HeaderBytes] = 0xC1;
        Assert.False(VideoRetransmissionPayload.TryDecode(badVersion, out _, out _, out _, out _, out _));
        Assert.False(VideoRetransmissionPayload.TryDecode(badFlags, out _, out _, out _, out _, out _));
        Assert.False(VideoRetransmissionPayload.TryDecode(badInner, out _, out _, out _, out _, out _));
        Assert.False(VideoRetransmissionPayload.TryDecode(record.AsSpan(0, VideoRetransmissionPayload.HeaderBytes), out _, out _, out _, out _, out _));
        Assert.Throws<ArgumentException>(() => VideoRetransmissionPayload.Encode(1, 1, 1, false, new byte[1_202]));
    }

    [Fact]
    public void Nack_items_pack_sixteen_following_losses_and_wrap_around()
    {
        var items = WebRtcMediaSession.PackNacks([65_534, 65_535, 0, 3, 40]);

        // 65535, 0 and 3 are 1, 2 and 5 after 65534: BLP bits 0, 1 and 4.
        Assert.Equal(
            new (ushort, ushort)[] { (65_534, 0b1_0011), (40, 0) },
            items);
    }
}

public sealed class VideoRtpFrameAssemblerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly VideoAssemblyOutput _output = new();

    private VideoRtpFrameAssembler NewAssembler() => new(_time);

    private static (ushort Sequence, uint Timestamp, bool Marker, byte[] Payload)[] Frame(
        ushort firstSequence, uint timestamp, byte fill, int bytes)
    {
        var data = Enumerable.Repeat(fill, bytes).ToArray();
        var packets = VideoRtpPacketizer.Packetize(data);
        return packets.Select((payload, index) => (
            unchecked((ushort)(firstSequence + index)), timestamp, index == packets.Length - 1, payload)).ToArray();
    }

    private void Push(VideoRtpFrameAssembler assembler, (ushort Sequence, uint Timestamp, bool Marker, byte[] Payload) packet) =>
        assembler.Push(packet.Sequence, packet.Timestamp, packet.Marker, packet.Payload, _output);

    [Fact]
    public void In_order_packets_release_whole_frames_in_order()
    {
        var assembler = NewAssembler();
        foreach (var packet in Frame(100, 9_000, 1, 3_000).Concat(Frame(103, 12_000, 2, 500)))
            Push(assembler, packet);

        Assert.Equal(2, _output.Frames.Count);
        Assert.Equal(Enumerable.Repeat((byte)1, 3_000), _output.Frames[0].Frame);
        Assert.Equal(Enumerable.Repeat((byte)2, 500), _output.Frames[1].Frame);
        Assert.Empty(_output.Nacks);
        Assert.False(_output.KeyFrameRequested);
    }

    [Fact]
    public void A_lost_packet_is_nacked_and_its_retransmission_completes_the_frame()
    {
        var assembler = NewAssembler();
        var first = Frame(65_534, 1, 3, 3_000); // wraps through sequence 0
        var second = Frame(1, 2, 4, 100);
        Push(assembler, first[0]);
        Push(assembler, first[2]);
        Push(assembler, second[0]);

        Assert.Equal([(ushort)65_535], _output.Nacks);
        Assert.Empty(_output.Frames);

        _output.Clear();
        var lost = first[1];
        var repair = VideoRetransmissionPayload.Encode(lost.Sequence, lost.Sequence, lost.Timestamp, lost.Marker, lost.Payload);
        assembler.Push(2, 2, false, repair, _output);

        Assert.Equal(2, _output.Frames.Count);
        Assert.Equal(Enumerable.Repeat((byte)3, 3_000), _output.Frames[0].Frame);
        Assert.Equal(Enumerable.Repeat((byte)4, 100), _output.Frames[1].Frame);
        Assert.Equal(1, assembler.RecoveredPackets);
        Assert.False(_output.KeyFrameRequested);
    }

    [Fact]
    public void A_lost_retransmission_number_is_repaired_without_corrupting_the_next_frame()
    {
        var assembler = NewAssembler();
        var first = Frame(10, 1, 5, 2_000);
        Push(assembler, first[0]);
        // Sequence 11 (media) lost; the sender's first repair took sequence 12 and was lost too;
        // the next frame's media then continues at 13.
        var next = Frame(13, 2, 6, 100);
        Push(assembler, next[0]);
        Assert.Equal([(ushort)11, (ushort)12], _output.Nacks);

        _output.Clear();
        var lost = first[1];
        // The repair for 12 names 12 as repaired and 11 as the original it carries.
        assembler.Push(14, 2, false,
            VideoRetransmissionPayload.Encode(12, lost.Sequence, lost.Timestamp, lost.Marker, lost.Payload), _output);

        Assert.Equal(2, _output.Frames.Count);
        Assert.Equal(Enumerable.Repeat((byte)5, 2_000), _output.Frames[0].Frame);
        Assert.Equal(Enumerable.Repeat((byte)6, 100), _output.Frames[1].Frame);
        Assert.False(_output.KeyFrameRequested);
    }

    [Fact]
    public void An_unrepaired_frame_is_skipped_after_the_hold_window_and_a_key_frame_requested()
    {
        var assembler = NewAssembler();
        var first = Frame(20, 1, 7, 2_500);
        var second = Frame(23, 2, 8, 100);
        Push(assembler, first[0]);
        Push(assembler, first[2]);
        Push(assembler, second[0]);
        Assert.Empty(_output.Frames);

        _output.Clear();
        _time.Advance(assembler.RetryInterval + TimeSpan.FromMilliseconds(1));
        assembler.Tick(_output);
        Assert.Equal([(ushort)21], _output.Nacks);

        _output.Clear();
        _time.Advance(assembler.HoldWindow);
        assembler.Tick(_output);

        Assert.True(_output.KeyFrameRequested);
        Assert.Equal(Enumerable.Repeat((byte)8, 100), Assert.Single(_output.Frames).Frame);
        Assert.Equal(1, assembler.SkippedFrames);
    }

    [Fact]
    public void A_stalled_frame_tail_is_probed_with_the_numbers_that_would_follow()
    {
        var assembler = NewAssembler();
        var frame = Frame(500, 1, 9, 2_500);
        Push(assembler, frame[0]);
        Push(assembler, frame[1]);
        Assert.Empty(_output.Nacks);

        _time.Advance(assembler.RetryInterval + TimeSpan.FromMilliseconds(1));
        assembler.Tick(_output);

        Assert.Equal(Enumerable.Range(502, 17).Select(value => (ushort)value), _output.Nacks);

        _output.Clear();
        var tail = frame[2];
        assembler.Push(503, 1, false,
            VideoRetransmissionPayload.Encode(tail.Sequence, tail.Sequence, tail.Timestamp, tail.Marker, tail.Payload), _output);
        Assert.Equal(Enumerable.Repeat((byte)9, 2_500), Assert.Single(_output.Frames).Frame);
    }

    [Fact]
    public void A_massive_gap_resets_to_a_key_frame_instead_of_flooding_nacks()
    {
        var assembler = NewAssembler();
        Push(assembler, Frame(1_000, 1, 1, 100)[0]);
        _output.Clear();

        Push(assembler, Frame(1_000 + VideoRtpFrameAssembler.MaximumOutstandingMissing + 10, 2, 2, 100)[0]);

        Assert.True(_output.KeyFrameRequested);
        Assert.Empty(_output.Nacks);
        Assert.Equal(Enumerable.Repeat((byte)2, 100), Assert.Single(_output.Frames).Frame);
    }

    [Fact]
    public void Packets_before_the_first_frame_start_and_foreign_payloads_are_ignored()
    {
        var assembler = NewAssembler();
        var frame = Frame(50, 1, 3, 2_500);
        Push(assembler, frame[1]);
        assembler.Push(52, 1, false, [0x80, 1, 2], _output);
        Assert.Empty(_output.Frames);
        Assert.Empty(_output.Nacks);

        foreach (var packet in Frame(60, 2, 4, 2_500)) Push(assembler, packet);
        Assert.Equal(Enumerable.Repeat((byte)4, 2_500), Assert.Single(_output.Frames).Frame);
    }
}

public sealed class PacedVideoRtpSenderTests
{
    private sealed class RecordingSink : IVideoRtpPacketSink
    {
        private ushort _next = 1_000;
        public List<(byte[] Payload, uint Timestamp, bool Marker, ushort Sequence)> Sent { get; } = [];
        public uint InitialTimestamp => 90_000;

        public ushort? Send(byte[] payload, uint timestamp, bool marker)
        {
            var sequence = _next++;
            Sent.Add((payload, timestamp, marker, sequence));
            return sequence;
        }
    }

    private static int Drain(PacedVideoRtpSender sender, int maximum = 10_000)
    {
        var sent = 0;
        while (sent < maximum)
        {
            if (sender.TrySendNext(out var wait))
            {
                sent++;
                continue;
            }
            if (wait == Timeout.InfiniteTimeSpan) break;
            Thread.Sleep(wait);
        }
        return sent;
    }

    [Fact]
    public void Ordinary_frames_leave_in_one_burst_with_continuous_timestamps()
    {
        var sink = new RecordingSink();
        using var sender = new PacedVideoRtpSender(sink, 6_000, startThread: false);

        sender.EnqueueFrame(new byte[6_000], 3_000);
        sender.EnqueueFrame(new byte[100], 3_000);
        for (var index = 0; index < 6; index++)
            Assert.True(sender.TrySendNext(out _));

        Assert.Equal(6, sink.Sent.Count);
        Assert.Equal([90_000u, 90_000, 90_000, 90_000, 90_000, 93_000], sink.Sent.Select(packet => packet.Timestamp));
        Assert.Equal([false, false, false, false, true, true], sink.Sent.Select(packet => packet.Marker));
        Assert.Equal(VideoRtpPacketizer.StartDescriptor, sink.Sent[0].Payload[0]);
        Assert.Equal(2, sender.FramesSent);
    }

    [Fact]
    public void A_large_frame_is_paced_after_the_burst_allowance_but_within_its_deadline()
    {
        var time = new ManualTimeProvider();
        var sink = new RecordingSink();
        using var sender = new PacedVideoRtpSender(sink, 6_000, startThread: false, timeProvider: time);
        sender.EnqueueFrame(new byte[400_000], 3_000);
        var total = VideoRtpPacketizer.PacketCount(400_000);

        var immediate = 0;
        while (sender.TrySendNext(out _)) immediate++;
        Assert.InRange(immediate, PacedVideoRtpSender.BurstPackets, PacedVideoRtpSender.BurstPackets + 1);
        Assert.False(sender.TrySendNext(out var wait));
        Assert.InRange(wait, TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(5));

        // Halfway to its deadline only part of the frame has left: it is spread, not burst.
        time.Advance(PacedVideoRtpSender.MaximumQueueDelay / 2);
        while (sender.TrySendNext(out _)) { }
        Assert.InRange(sink.Sent.Count, immediate + 1, total - 1);

        // At the deadline pacing never holds the rest back.
        time.Advance(PacedVideoRtpSender.MaximumQueueDelay / 2);
        while (sender.TrySendNext(out _)) { }
        Assert.Equal(total, sink.Sent.Count);
    }

    [Fact]
    public void Negotiated_nacks_jump_the_queue_and_repeat_the_original_for_a_lost_repair()
    {
        var sink = new RecordingSink();
        using var sender = new PacedVideoRtpSender(sink, 6_000, startThread: false) { RetransmissionEnabled = true };
        sender.EnqueueFrame(new byte[3_000], 3_000);
        Drain(sender);
        var lost = sink.Sent[1];

        sender.EnqueueFrame(new byte[100], 3_000);
        sender.OnNack(lost.Sequence, 0);
        Assert.True(sender.TrySendNext(out _));

        var repair = sink.Sent[^1];
        Assert.True(VideoRetransmissionPayload.TryDecode(repair.Payload, out var repaired, out var original,
            out var timestamp, out var marker, out var media));
        Assert.Equal((lost.Sequence, lost.Sequence, lost.Timestamp, lost.Marker), (repaired, original, timestamp, marker));
        Assert.Equal(lost.Payload, media.ToArray());
        Assert.False(repair.Marker);

        // A NACK for the repair's own number resends the same original naming that number.
        sender.OnNack(repair.Sequence, 0);
        Assert.True(sender.TrySendNext(out _));
        Assert.True(VideoRetransmissionPayload.TryDecode(sink.Sent[^1].Payload, out repaired, out original, out _, out _, out _));
        Assert.Equal((repair.Sequence, lost.Sequence), (repaired, original));
        Assert.Equal(2, sender.RetransmittedPackets);

        // Numbers never sent are not answered.
        sender.OnNack(unchecked((ushort)(sink.Sent[^1].Sequence + 100)), 0);
        Drain(sender);
        Assert.True(sender.UnansweredNacks >= 1);
    }

    [Fact]
    public void Nacks_are_ignored_unless_retransmission_was_negotiated()
    {
        var sink = new RecordingSink();
        using var sender = new PacedVideoRtpSender(sink, 6_000, startThread: false);
        sender.EnqueueFrame(new byte[100], 3_000);
        Drain(sender);

        sender.OnNack(sink.Sent[0].Sequence, 0xFFFF);

        Assert.False(sender.TrySendNext(out var wait));
        Assert.Equal(Timeout.InfiniteTimeSpan, wait);
        Assert.Equal(0, sender.NackedPackets);
    }

    [Fact]
    public void Overflow_discards_unstarted_frames_and_asks_for_a_key_frame()
    {
        var sink = new RecordingSink();
        using var sender = new PacedVideoRtpSender(sink, 6_000, startThread: false);
        var overflowed = 0;
        sender.QueueOverflowed += (_, _) => overflowed++;
        sender.EnqueueFrame(new byte[3_000], 3_000);
        Assert.True(sender.TrySendNext(out _)); // the first frame is now partly sent
        for (var index = 0; index < 4; index++)
            sender.EnqueueFrame(new byte[3 * 1024 * 1024], 3_000);

        Assert.True(overflowed >= 1);
        Assert.True(sender.OverflowedFrames >= 1);
        Assert.True(sender.QueuedBytes <= PacedVideoRtpSender.MaximumQueuedBytes);
    }

    [Fact]
    public void The_pacing_thread_delivers_queued_frames_on_its_own()
    {
        var sink = new RecordingSink();
        using (var sender = new PacedVideoRtpSender(sink, 6_000))
        {
            sender.EnqueueFrame(new byte[50_000], 3_000);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sender.FramesSent == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
            Assert.Equal(1, sender.FramesSent);
        }
        Assert.Equal(VideoRtpPacketizer.PacketCount(50_000), sink.Sent.Count);
    }
}
