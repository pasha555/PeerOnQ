using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Codecs;
using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class HardwareVideoCodecTests
{
    private static byte[] H264KeyFrame => [0, 0, 0, 1, 0x67, 0x42, 0, 0, 1, 0x68, 0xce, 0, 0, 1, 0x65, 0x88];

    [Fact]
    public void Authenticated_payload_envelope_preserves_codec_dimensions_and_access_unit()
    {
        var encoded = VideoPayloadEnvelope.Encode(VideoPayloadCodec.H264, 1920, 1080, H264KeyFrame);
        Assert.True(VideoPayloadEnvelope.TryDecode(encoded, out var codec, out var width,
            out var height, out var key, out var decoded));
        Assert.Equal(VideoPayloadCodec.H264, codec);
        Assert.Equal(1920, width); Assert.Equal(1080, height); Assert.True(key);
        Assert.Equal(H264KeyFrame, decoded);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("codec")]
    [InlineData("flags")]
    [InlineData("reserved")]
    [InlineData("dimensions")]
    [InlineData("length")]
    [InlineData("truncated")]
    [InlineData("false_key")]
    [InlineData("missing_parameter_sets")]
    public void Invalid_payload_never_reaches_a_decoder(string corruption)
    {
        var encoded = VideoPayloadEnvelope.Encode(VideoPayloadCodec.H264, 1920, 1080, H264KeyFrame);
        switch (corruption)
        {
            case "version": encoded[4] = 2; break;
            case "codec": encoded[5] = 255; break;
            case "flags": encoded[6] = 3; break;
            case "reserved": encoded[7] = 1; break;
            case "dimensions": BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(8), int.MaxValue); break;
            case "length": BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(16), 4); break;
            case "truncated": encoded = encoded[..^1]; break;
            case "false_key": encoded[6] = 0; break;
            case "missing_parameter_sets": encoded[VideoPayloadEnvelope.HeaderBytes + 4] = 0x61; break;
        }
        Assert.False(VideoPayloadEnvelope.TryDecode(encoded, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void Oversize_payload_and_truncated_NAL_headers_are_rejected_before_native_code()
    {
        Assert.False(VideoPayloadEnvelope.TryDecode(new byte[VideoPayloadEnvelope.MaximumPayloadBytes + 21],
            out _, out _, out _, out _, out _));
        Assert.False(VideoPayloadEnvelope.IsValidAccessUnit(VideoPayloadCodec.H264,
            [0, 0, 1, 0x65], out _));
    }

    [Fact]
    public async Task First_frame_and_codec_changes_require_a_real_key_frame()
    {
        // No peer is started: this exercises the decoder's state boundary without native injection.
        await using var session = WebRtcMediaSession.CreateViewer(SessionId.New());
        Assert.False(session.TryAcceptVideoCodec(VideoPayloadCodec.H264, false, out _));
        Assert.True(session.TryAcceptVideoCodec(VideoPayloadCodec.H264, true, out var changed));
        Assert.True(changed);
        Assert.True(session.TryAcceptVideoCodec(VideoPayloadCodec.H264, false, out changed));
        Assert.False(changed);
        Assert.False(session.TryAcceptVideoCodec(VideoPayloadCodec.Vp8, false, out _));
        Assert.True(session.TryAcceptVideoCodec(VideoPayloadCodec.Vp8, true, out changed));
        Assert.True(changed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task H264_requires_both_endpoints_and_reports_no_unused_hardware(bool viewerSupportsH264)
    {
        var decoder = viewerSupportsH264 ? new TrackingDecoder() : null;
        var factoryCalls = 0;
        await using var sharer = WebRtcMediaSession.CreateSharer(SessionId.New(), null, MediaProfile.Conservative,
            encoderSelector: new(() => true, () => true),
            hardwareH264EncoderFactory: () => { factoryCalls++; return new FailingEncoder(); });
        await using var viewer = WebRtcMediaSession.CreateViewer(sharer.SessionId, h264Decoder: decoder);
        var offer = await sharer.CreateOfferAsync();
        Assert.True(WebRtcMediaSession.HasH264Payload(offer));
        var answer = await viewer.CreateAnswerAsync(offer);
        await sharer.ApplyRemoteAnswerAsync(answer);
        Assert.Equal(viewerSupportsH264, sharer.IsH264PayloadNegotiated);
        Assert.Equal(viewerSupportsH264, viewer.IsH264PayloadNegotiated);
        Assert.Equal(0, factoryCalls);
        Assert.False(sharer.Encoder.IsHardware);
    }

    [Fact]
    public async Task Old_sharer_and_unsolicited_answer_keep_original_VP8()
    {
        await using var sharer = WebRtcMediaSession.CreateSharer(SessionId.New(), null, MediaProfile.Conservative);
        await using var viewer = WebRtcMediaSession.CreateViewer(sharer.SessionId, h264Decoder: new TrackingDecoder());
        var offer = await sharer.CreateOfferAsync();
        Assert.False(WebRtcMediaSession.HasH264Payload(offer));
        var answer = await viewer.CreateAnswerAsync(offer);
        Assert.False(WebRtcMediaSession.HasH264Payload(answer));
        await sharer.ApplyRemoteAnswerAsync(answer + "a=x-peeronq-video-payload:1;vp8,h264\r\n");
        Assert.False(sharer.IsH264PayloadNegotiated);
        Assert.False(viewer.IsH264PayloadNegotiated);
    }

    [Fact]
    public void Unknown_or_duplicated_SDP_codec_version_is_not_negotiated()
    {
        Assert.False(WebRtcMediaSession.HasH264Payload("a=x-peeronq-video-payload:2;vp8,h264\r\n"));
        Assert.False(WebRtcMediaSession.HasH264Payload("a=x-peeronq-video-payload:1;vp8,h264\r\na=x-peeronq-video-payload:1;vp8,h264\r\n"));
    }

    [Fact]
    public void Device_loss_disposes_hardware_once_and_continues_with_a_VP8_key_frame()
    {
        var hardware = new FailingEncoder();
        using var software = new Vp8ScreenEncoder(2_000, 30);
        using var controller = new HardwareVideoEncodingController(() => hardware);
        var frame = new byte[64 * 64 * 3 / 2];
        Array.Fill(frame, (byte)128);
        var first = controller.Encode(software, true, 64, 64, frame, 2_000, 30, false);
        Assert.Equal(VideoPayloadCodec.H264, first.Codec);
        Assert.True(controller.Selection.IsHardware);
        var fallback = controller.Encode(software, true, 64, 64, frame, 2_000, 30, false);
        Assert.Equal(VideoPayloadCodec.Vp8, fallback.Codec);
        Assert.True(VideoPayloadEnvelope.IsValidAccessUnit(fallback.Codec, fallback.Bytes, out var key));
        Assert.True(key);
        Assert.False(controller.Selection.IsHardware);
        Assert.Contains("failed", controller.Selection.Reason);
        _ = controller.Encode(software, true, 64, 64, frame, 2_000, 30, false);
        controller.Dispose();
        Assert.Equal(2, hardware.Calls);
        Assert.Equal(1, hardware.Disposals);
    }

    [Fact]
    public void Legacy_encoding_keeps_the_original_VP8_bytes_and_never_opens_hardware()
    {
        var factoryCalls = 0;
        using var controller = new HardwareVideoEncodingController(() => { factoryCalls++; return new FailingEncoder(); });
        using var legacy = new Vp8ScreenEncoder(2_000, 30);
        using var current = new Vp8ScreenEncoder(2_000, 30);
        var frame = new byte[64 * 64 * 3 / 2];
        Array.Fill(frame, (byte)128);
        var expected = legacy.EncodeI420(64, 64, frame);
        var actual = controller.Encode(current, false, 64, 64, frame, 2_000, 30, false);
        Assert.Equal(VideoPayloadCodec.Vp8, actual.Codec);
        Assert.Equal(expected, actual.Bytes);
        Assert.Equal(0, factoryCalls);
        Assert.False(controller.Selection.IsHardware);
        var framed = VideoPayloadEnvelope.Encode(actual.Codec, 64, 64, actual.Bytes);
        BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(8), 128);
        Assert.False(VideoPayloadEnvelope.TryDecode(framed, out _, out _, out _, out _, out _));
    }

    [Fact]
    public async Task A_session_disposes_its_native_decoder_once()
    {
        var decoder = new TrackingDecoder();
        var viewer = WebRtcMediaSession.CreateViewer(SessionId.New(), h264Decoder: decoder);
        await viewer.DisposeAsync();
        await viewer.DisposeAsync();
        Assert.Equal(1, decoder.Disposals);
    }

    [Theory]
    [InlineData(2, 2, 2, 2, 6, true)]
    [InlineData(6, 4, 8, 6, 72, true)]
    [InlineData(2, 2, 1, 2, 6, false)]
    [InlineData(2, 2, 3, 2, 9, false)]
    [InlineData(2, 2, 2, 3, 9, false)]
    [InlineData(2, 2, 2, 2, 5, false)]
    [InlineData(2, 2, 2, 68, 204, false)]
    [InlineData(3, 2, 4, 2, 12, false)]
    public void Deferred_NV12_layout_requires_complete_bounded_even_native_planes(
        int width, int height, int stride, int bufferHeight, int bytes, bool valid)
    {
        var image = new DecodedNv12Frame(width, height, stride, bufferHeight, new byte[bytes]);
        Assert.Equal(valid, image.HasValidLayout);
    }

    private sealed class FailingEncoder : IHardwareH264Encoder
    {
        public string Name => "test hardware";
        public int Calls, Disposals;
        public byte[] EncodeI420(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame) =>
            ++Calls == 1 ? H264KeyFrame : throw new COMException("device removed", unchecked((int)0x887a0005));
        public void Dispose() => Disposals++;
    }

    private sealed class TrackingDecoder : IH264VideoDecoder
    {
        public string Name => "test decoder";
        public int Disposals;
        public RemoteVideoFrame[] Decode(int width, int height, byte[] frame, bool reset) => [];
        public void Dispose() => Disposals++;
    }
}
