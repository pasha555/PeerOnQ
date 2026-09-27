using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Transport.Protocol;

/// <summary>
/// Bounded binary envelope for an opaque, session-protected file record. The signaling relay
/// validates only this envelope; the encrypted record itself is never parsed or logged there.
/// </summary>
public static class FileRelayFrameCodec
{
    private static ReadOnlySpan<byte> Magic => "PNQF"u8;
    private const byte Version = 1;
    private const int HeaderBytes = 4 + 1 + 16;

    // This is a per-record memory/backpressure bound, not a file-size or bandwidth limit.
    // It accommodates the largest authenticated collaboration record (768 KiB control metadata
    // plus a 256 KiB chunk) and its record overhead, without imposing a total-transfer cap.
    public const int MaxPayloadBytes = (1024 * 1024) + (16 * 1024);
    public const int MaxFrameBytes = HeaderBytes + MaxPayloadBytes;

    public static byte[] Encode(SessionId sessionId, ReadOnlySpan<byte> payload)
    {
        if (sessionId.Value == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        if (payload.IsEmpty || payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload));

        var frame = new byte[HeaderBytes + payload.Length];
        Magic.CopyTo(frame);
        frame[4] = Version;
        sessionId.Value.TryWriteBytes(frame.AsSpan(5, 16));
        payload.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out SessionId sessionId,
        out byte[] payload)
    {
        sessionId = default;
        payload = [];
        if (frame.Length is <= HeaderBytes or > MaxFrameBytes
            || !frame[..4].SequenceEqual(Magic)
            || frame[4] != Version)
            return false;

        var value = new Guid(frame.Slice(5, 16));
        if (value == Guid.Empty) return false;

        sessionId = new SessionId(value);
        payload = frame[HeaderBytes..].ToArray();
        return true;
    }
}
