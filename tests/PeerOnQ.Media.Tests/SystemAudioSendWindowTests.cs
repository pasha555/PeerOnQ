using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class SystemAudioSendWindowTests
{
    [Fact]
    public void Unconfirmed_audio_has_a_twelve_frame_bound_and_a_dropped_frame_consumes_no_slot()
    {
        var clock = new Clock();
        var window = new SystemAudioSendWindow(clock);
        for (var sequence = 1; sequence <= 12; sequence++) Assert.Equal(AudioAdmission.Accepted, window.TryAdmit(sequence));
        Assert.Equal(AudioAdmission.Backpressured, window.TryAdmit(13));
        Assert.Equal(12, window.OutstandingFrames);
        Assert.True(SystemAudioSendWindow.MaximumOutstandingBytes < 50 * 1024);
        Assert.Equal(AudioReceiptResult.Accepted, window.Acknowledge(12));
        Assert.Equal(0, window.OutstandingFrames);
        Assert.Equal(AudioAdmission.Accepted, window.TryAdmit(13));
    }

    [Fact]
    public void Only_actual_admitted_ids_can_advance_receipts_and_old_receipts_do_not_change_state()
    {
        var window = new SystemAudioSendWindow(new Clock());
        Assert.Equal(AudioAdmission.Accepted, window.TryAdmit(1));
        Assert.Equal(AudioAdmission.Accepted, window.TryAdmit(8));
        Assert.Equal(AudioReceiptResult.Invalid, window.Acknowledge(5));
        Assert.Equal(AudioReceiptResult.Invalid, window.Acknowledge(9));
        Assert.Equal(2, window.OutstandingFrames);
        Assert.Equal(AudioReceiptResult.Accepted, window.Acknowledge(1));
        Assert.Equal(AudioReceiptResult.Stale, window.Acknowledge(1));
        Assert.Equal(1, window.OutstandingFrames);
        Assert.Equal(AudioReceiptResult.Accepted, window.Acknowledge(8));
        Assert.Equal(0, window.OutstandingFrames);
        Assert.Equal(AudioAdmission.InvalidSequence, window.TryAdmit(8));
    }

    [Fact]
    public void Quiet_missing_receipts_expire_and_a_late_receipt_cannot_resurrect_the_window()
    {
        var clock = new Clock();
        var window = new SystemAudioSendWindow(clock);
        window.TryAdmit(1);
        clock.Advance(499);
        Assert.False(window.IsExpired);
        clock.Advance(2);
        Assert.True(window.IsExpired);
        Assert.Equal(AudioReceiptResult.Expired, window.Acknowledge(1));
        Assert.Equal(AudioAdmission.Expired, window.TryAdmit(2));
        window.Close();
        Assert.Equal(0, window.OutstandingFrames);
        Assert.Equal(AudioAdmission.Expired, window.TryAdmit(3));
    }

    [Theory]
    [InlineData(1, 320)]
    [InlineData(100, 380)]
    [InlineData(450, 1000)]
    public void Receipt_deadline_tracks_measured_round_trip_with_defensive_bounds(int rttMs, int expectedDeadline)
    {
        var clock = new Clock();
        var window = new SystemAudioSendWindow(clock);
        window.TryAdmit(1);
        clock.Advance(rttMs);
        Assert.Equal(AudioReceiptResult.Accepted, window.Acknowledge(1));
        Assert.Equal(expectedDeadline, window.ReceiptDeadlineMs);
        window.TryAdmit(2);
        clock.Advance(expectedDeadline - 1);
        Assert.False(window.IsExpired);
        clock.Advance(2);
        Assert.True(window.IsExpired);
    }

    [Fact]
    public void Receipt_codec_has_an_exact_bounded_vector_and_rejects_reserved_header_changes()
    {
        var encoded = SystemAudioReceiptCodec.Encode(1);
        Assert.Equal("504E514B010000000000000000000001", Convert.ToHexString(encoded));
        Assert.True(SystemAudioReceiptCodec.TryDecode(encoded, out var sequence));
        Assert.Equal(1, sequence);
        Assert.False(SystemAudioReceiptCodec.TryDecode(encoded[..^1], out _));
        Assert.False(SystemAudioReceiptCodec.TryDecode([.. encoded, 0], out _));
        encoded[5] = 1;
        Assert.False(SystemAudioReceiptCodec.TryDecode(encoded, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemAudioReceiptCodec.Encode(0));
    }

    private sealed class Clock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(int milliseconds) => _milliseconds += milliseconds;
    }
}
