using System.Buffers.Binary;

namespace PeerOnQ.Media.Pipeline;

internal static class VideoFrameTelemetryCodec
{
    private static readonly byte[] Magic = "PNQV"u8.ToArray();
    private const byte Version = 1;
    private const int HeaderBytes = 4 + 1 + 8 + 8 + 4;

    public static bool LooksLikeEnvelope(ReadOnlySpan<byte> frame) =>
        frame.Length >= Magic.Length && frame[..Magic.Length].SequenceEqual(Magic);

    public static byte[] Encode(
        ReadOnlySpan<byte> encodedFrame,
        long captureTimestampUnixMicroseconds,
        long sequenceNumber)
    {
        if (encodedFrame.IsEmpty) throw new ArgumentException("An encoded frame is required.", nameof(encodedFrame));
        if (encodedFrame.Length > int.MaxValue - HeaderBytes)
            throw new ArgumentOutOfRangeException(nameof(encodedFrame));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureTimestampUnixMicroseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceNumber);

        var envelope = new byte[HeaderBytes + encodedFrame.Length];
        Magic.CopyTo(envelope, 0);
        envelope[4] = Version;
        BinaryPrimitives.WriteInt64BigEndian(envelope.AsSpan(5, 8), captureTimestampUnixMicroseconds);
        BinaryPrimitives.WriteInt64BigEndian(envelope.AsSpan(13, 8), sequenceNumber);
        BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(21, 4), encodedFrame.Length);
        encodedFrame.CopyTo(envelope.AsSpan(HeaderBytes));
        return envelope;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> envelope,
        out long captureTimestampUnixMicroseconds,
        out long sequenceNumber,
        out byte[] encodedFrame)
    {
        captureTimestampUnixMicroseconds = 0;
        sequenceNumber = 0;
        encodedFrame = [];
        if (envelope.Length <= HeaderBytes
            || !envelope[..4].SequenceEqual(Magic)
            || envelope[4] != Version)
        {
            return false;
        }

        captureTimestampUnixMicroseconds = BinaryPrimitives.ReadInt64BigEndian(envelope.Slice(5, 8));
        sequenceNumber = BinaryPrimitives.ReadInt64BigEndian(envelope.Slice(13, 8));
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(envelope.Slice(21, 4));
        if (captureTimestampUnixMicroseconds <= 0
            || sequenceNumber < 0
            || payloadLength <= 0
            || payloadLength != envelope.Length - HeaderBytes)
        {
            captureTimestampUnixMicroseconds = 0;
            sequenceNumber = 0;
            return false;
        }

        encodedFrame = envelope[HeaderBytes..].ToArray();
        return true;
    }
}
