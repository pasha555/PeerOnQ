using PeerOnQ.Domain.Sessions;
using PeerOnQ.Transport.Protocol;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class FileRelayFrameCodecTests
{
    [Fact]
    public void Round_trip_keeps_session_binding_and_opaque_payload()
    {
        var sessionId = SessionId.New();
        var payload = Enumerable.Range(0, 256 * 1024).Select(index => (byte)index).ToArray();

        var frame = FileRelayFrameCodec.Encode(sessionId, payload);

        Assert.True(FileRelayFrameCodec.TryDecode(frame, out var decodedSessionId, out var decodedPayload));
        Assert.Equal(sessionId, decodedSessionId);
        Assert.Equal(payload, decodedPayload);
    }

    [Fact]
    public void Supports_the_largest_bounded_record_used_by_an_unlimited_transfer()
    {
        var sessionId = SessionId.New();
        var payload = new byte[FileRelayFrameCodec.MaxPayloadBytes];
        payload[^1] = 0xA5;

        var frame = FileRelayFrameCodec.Encode(sessionId, payload);

        Assert.True(FileRelayFrameCodec.TryDecode(frame, out var decodedSessionId, out var decodedPayload));
        Assert.Equal(sessionId, decodedSessionId);
        Assert.Equal(payload, decodedPayload);
    }

    [Fact]
    public void Rejects_malformed_or_oversized_frames()
    {
        Assert.False(FileRelayFrameCodec.TryDecode("PNQF"u8.ToArray(), out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FileRelayFrameCodec.Encode(SessionId.New(), new byte[FileRelayFrameCodec.MaxPayloadBytes + 1]));
    }
}
