using System.Buffers.Binary;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// Splits one protected video frame into RTP payloads exactly as SIPSorcery's VP8 sender does:
/// 1200-byte fragments, each behind a one-byte VP8 payload descriptor whose S bit marks the
/// first fragment. Older viewers reassemble these packets with their unchanged framer.
/// </summary>
internal static class VideoRtpPacketizer
{
    /// <summary>SIPSorcery's RTP_MAX_PAYLOAD: the media bytes carried by one packet.</summary>
    public const int MaximumFragmentBytes = 1200;

    public const byte StartDescriptor = 0x10;
    public const byte ContinuationDescriptor = 0x00;

    public static int PacketCount(int frameLength) =>
        frameLength <= 0 ? 0 : (frameLength + MaximumFragmentBytes - 1) / MaximumFragmentBytes;

    /// <summary>Returns each fragment with its descriptor; the last one carries the marker bit.</summary>
    public static byte[][] Packetize(ReadOnlySpan<byte> frame)
    {
        var count = PacketCount(frame.Length);
        var packets = new byte[count][];
        for (var index = 0; index < count; index++)
        {
            var offset = index * MaximumFragmentBytes;
            var length = Math.Min(MaximumFragmentBytes, frame.Length - offset);
            var payload = GC.AllocateUninitializedArray<byte>(length + 1);
            payload[0] = index == 0 ? StartDescriptor : ContinuationDescriptor;
            frame.Slice(offset, length).CopyTo(payload.AsSpan(1));
            packets[index] = payload;
        }
        return packets;
    }

    /// <summary>True for the one-byte descriptors PeerOnQ and SIPSorcery senders emit.</summary>
    public static bool IsMediaDescriptor(byte descriptor) =>
        descriptor is StartDescriptor or ContinuationDescriptor;
}

/// <summary>
/// In-band retransmission record for a negotiated PeerOnQ viewer. It travels as a new RTP packet
/// on the same SSRC and payload type with a fresh sequence number, so SRTP replay protection and
/// RTCP loss accounting stay correct, and it names both the sequence number it repairs and the
/// original media packet it carries. A repaired number that differs from the original one was
/// itself a lost retransmission and is not part of any frame.
/// </summary>
internal static class VideoRetransmissionPayload
{
    /// <summary>VP8 descriptor bits X and reserved R set: never produced for media fragments.</summary>
    public const byte Tag = 0xC1;
    public const byte Version = 1;
    public const int HeaderBytes = 11;
    public const int MaximumBytes = HeaderBytes + VideoRtpPacketizer.MaximumFragmentBytes + 1;

    public static byte[] Encode(
        ushort repairedSequence,
        ushort originalSequence,
        uint originalTimestamp,
        bool originalMarker,
        ReadOnlySpan<byte> originalPayload)
    {
        if (originalPayload.IsEmpty || originalPayload.Length > VideoRtpPacketizer.MaximumFragmentBytes + 1)
            throw new ArgumentException("The original media payload has an invalid size.", nameof(originalPayload));
        var record = GC.AllocateUninitializedArray<byte>(HeaderBytes + originalPayload.Length);
        record[0] = Tag;
        record[1] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2), repairedSequence);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(4), originalSequence);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(6), originalTimestamp);
        record[10] = originalMarker ? (byte)1 : (byte)0;
        originalPayload.CopyTo(record.AsSpan(HeaderBytes));
        return record;
    }

    public static bool IsRetransmission(ReadOnlySpan<byte> payload) =>
        payload.Length > 0 && payload[0] == Tag;

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out ushort repairedSequence,
        out ushort originalSequence,
        out uint originalTimestamp,
        out bool originalMarker,
        out ReadOnlySpan<byte> originalPayload)
    {
        repairedSequence = originalSequence = 0;
        originalTimestamp = 0;
        originalMarker = false;
        originalPayload = default;
        if (payload.Length <= HeaderBytes || payload.Length > MaximumBytes
            || payload[0] != Tag || payload[1] != Version || (payload[10] & ~1) != 0)
            return false;
        repairedSequence = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        originalSequence = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        originalTimestamp = BinaryPrimitives.ReadUInt32BigEndian(payload[6..]);
        originalMarker = payload[10] == 1;
        originalPayload = payload[HeaderBytes..];
        return VideoRtpPacketizer.IsMediaDescriptor(originalPayload[0]);
    }
}
