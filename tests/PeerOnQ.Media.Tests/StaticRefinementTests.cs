using PeerOnQ.Media.Codecs;
using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class StaticRefinementTests
{
    private static byte[] Luma(int width, int height, byte value)
    {
        var frame = new byte[width * height];
        Array.Fill(frame, value);
        return frame;
    }

    [Fact]
    public void A_first_frame_arms_a_bounded_refinement_window()
    {
        var refinement = new StaticRefinement();
        refinement.Observe(640, 480, Luma(640, 480, 128), framesPerSecond: 60, bitrateKbps: 10_000);

        // Two seconds of frames at 60 fps and 10 Mbps.
        Assert.Equal(120, refinement.Remaining);
    }

    [Theory]
    // 20 Mbit at the bitrate, between 2 and 4 seconds: refinement is bit-limited, and the window
    // must end before the 5 s safety-net IDR of both encoders.
    [InlineData(50_000, 2.0)]
    [InlineData(10_000, 2.0)]
    [InlineData(8_000, 2.5)]
    [InlineData(5_000, 4.0)]
    [InlineData(2_000, 4.0)]
    [InlineData(1_200, 4.0)]
    public void The_window_lasts_as_long_as_the_refinement_bits_take(int kbps, double seconds) =>
        Assert.Equal(seconds, StaticRefinement.Duration(kbps).TotalSeconds, 3);

    [Fact]
    public void A_slow_link_gets_a_longer_window_at_the_same_frame_rate()
    {
        var refinement = new StaticRefinement();
        refinement.Observe(640, 480, Luma(640, 480, 128), framesPerSecond: 60, bitrateKbps: 5_000);
        Assert.Equal(240, refinement.Remaining);
    }

    [Fact]
    public void The_longest_window_ends_before_the_periodic_safety_net_key_frame()
    {
        // A key frame re-arms the window; a window as long as the IDR interval would re-arm forever.
        Assert.True(StaticRefinement.MaximumDuration.TotalSeconds < Vp8ScreenEncoder.KeyFrameSafetyNetSeconds);
    }

    [Fact]
    public void Take_consumes_the_window_then_stops()
    {
        var refinement = new StaticRefinement();
        refinement.Observe(320, 240, Luma(320, 240, 128), framesPerSecond: 30, bitrateKbps: 10_000);

        var taken = 0;
        while (refinement.TryTake()) taken++;

        Assert.Equal(60, taken);
        Assert.False(refinement.TryTake());
        Assert.Equal(0, refinement.Remaining);
    }

    [Fact]
    public void An_unchanged_frame_does_not_reopen_the_window()
    {
        var refinement = new StaticRefinement();
        var still = Luma(640, 480, 128);
        refinement.Observe(640, 480, still, framesPerSecond: 60, bitrateKbps: 10_000);
        for (var i = 0; i < 60; i++) Assert.True(refinement.TryTake());
        Assert.Equal(60, refinement.Remaining);

        // The identical screen keeps refining the same window; it is not re-armed to 120.
        refinement.Observe(640, 480, still, framesPerSecond: 60, bitrateKbps: 10_000);
        Assert.Equal(60, refinement.Remaining);
    }

    [Fact]
    public void A_small_change_does_not_reopen_the_window_but_a_large_one_does()
    {
        var refinement = new StaticRefinement();
        var frame = Luma(640, 480, 128);
        refinement.Observe(640, 480, frame, framesPerSecond: 60, bitrateKbps: 10_000);
        for (var i = 0; i < 100; i++) refinement.TryTake();
        Assert.Equal(20, refinement.Remaining);

        // A caret-sized change (a handful of sampled pixels) must not re-arm.
        for (var i = 0; i < 8 * StaticRefinement.SampleStep; i++) frame[i] = 20;
        refinement.Observe(640, 480, frame, framesPerSecond: 60, bitrateKbps: 10_000);
        Assert.Equal(20, refinement.Remaining);

        // A window-sized change re-arms the full window.
        for (var i = 0; i < frame.Length; i++) frame[i] = (byte)(i % 2 == 0 ? 20 : 230);
        refinement.Observe(640, 480, frame, framesPerSecond: 60, bitrateKbps: 10_000);
        Assert.Equal(120, refinement.Remaining);
    }

    [Fact]
    public void A_resize_resets_and_re_arms()
    {
        var refinement = new StaticRefinement();
        refinement.Observe(640, 480, Luma(640, 480, 128), framesPerSecond: 60, bitrateKbps: 10_000);
        for (var i = 0; i < 60; i++) refinement.TryTake();

        refinement.Observe(1280, 720, Luma(1280, 720, 128), framesPerSecond: 60, bitrateKbps: 10_000);
        Assert.Equal(120, refinement.Remaining);
    }

    [Fact]
    public void A_key_frame_re_arms_because_an_idr_restarts_from_the_rate_control_floor()
    {
        var refinement = new StaticRefinement();
        refinement.Observe(640, 480, Luma(640, 480, 128), framesPerSecond: 60, bitrateKbps: 10_000);
        for (var i = 0; i < 115; i++) refinement.TryTake();
        Assert.Equal(5, refinement.Remaining);

        refinement.KeyFrameEncoded(framesPerSecond: 60, bitrateKbps: 10_000);
        Assert.Equal(120, refinement.Remaining);
    }

    [Fact]
    public void Reset_clears_the_window()
    {
        var refinement = new StaticRefinement();
        refinement.Observe(640, 480, Luma(640, 480, 128), framesPerSecond: 60, bitrateKbps: 10_000);
        refinement.Reset();
        Assert.Equal(0, refinement.Remaining);
        Assert.False(refinement.TryTake());
    }

    [Fact]
    public void An_undersized_luma_plane_disarms_instead_of_reading_out_of_bounds()
    {
        var refinement = new StaticRefinement();
        refinement.Observe(640, 480, Luma(640, 480, 128), framesPerSecond: 60, bitrateKbps: 10_000);
        refinement.Observe(640, 480, new byte[640 * 480 - 1], framesPerSecond: 60, bitrateKbps: 10_000);
        Assert.Equal(0, refinement.Remaining);
    }

    [Theory]
    // VP8 frame tag, 3 bytes little-endian; bit 0 = frame type (0 = key frame).
    [InlineData(VideoPayloadCodec.Vp8, new byte[] { 0x00, 0x00, 0x00 }, true)]
    [InlineData(VideoPayloadCodec.Vp8, new byte[] { 0x01, 0x00, 0x00 }, false)]
    // H.264 Annex B: NAL type 5 = IDR slice, 7 = SPS, 1 = non-IDR slice.
    [InlineData(VideoPayloadCodec.H264, new byte[] { 0, 0, 0, 1, 0x65 }, true)]
    [InlineData(VideoPayloadCodec.H264, new byte[] { 0, 0, 1, 0x67, 0, 0, 1, 0x65 }, true)]
    [InlineData(VideoPayloadCodec.H264, new byte[] { 0, 0, 0, 1, 0x61 }, false)]
    public void IsKeyFrame_detects_idr_and_vp8_key_frames(VideoPayloadCodec codec, byte[] encoded, bool expected) =>
        Assert.Equal(expected, StaticRefinement.IsKeyFrame(codec, encoded));

    [Fact]
    public void IsKeyFrame_is_false_for_an_empty_access_unit() =>
        Assert.False(StaticRefinement.IsKeyFrame(VideoPayloadCodec.H264, []));
}
