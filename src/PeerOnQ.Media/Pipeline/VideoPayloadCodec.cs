using System.Buffers.Binary;
using PeerOnQ.Media.Codecs;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// This header is inside PNQE authentication, never an RTP codec declaration. Legacy peers keep
/// their original VP8 bytes. Both peers must explicitly negotiate the version before using it.
/// </summary>
internal static class VideoPayloadEnvelope
{
    internal const int MaximumPayloadBytes = VideoCodecLimits.MaximumPayloadBytes;
    internal const int HeaderBytes = 20;
    private static ReadOnlySpan<byte> Magic => "PNQC"u8;

    internal static bool ValidDimensions(int width, int height) =>
        VideoCodecLimits.ValidDimensions(width, height);

    internal static byte[] Encode(VideoPayloadCodec codec, int width, int height, byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!ValidDimensions(width, height)) throw new ArgumentOutOfRangeException(nameof(width));
        if (!IsValidAccessUnit(codec, frame, out var keyFrame)
            || frame.Length > MaximumPayloadBytes || !DimensionsMatch(codec, frame, keyFrame, width, height))
            throw new ArgumentException("An encoded video access unit is required.", nameof(frame));
        var envelope = new byte[checked(HeaderBytes + frame.Length)];
        Magic.CopyTo(envelope);
        envelope[4] = 1;
        envelope[5] = (byte)codec;
        envelope[6] = keyFrame ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(8), width);
        BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(12), height);
        BinaryPrimitives.WriteInt32BigEndian(envelope.AsSpan(16), frame.Length);
        frame.CopyTo(envelope, HeaderBytes);
        return envelope;
    }

    internal static bool TryDecode(byte[] envelope, out VideoPayloadCodec codec,
        out int width, out int height, out bool keyFrame, out byte[] frame)
    {
        codec = default;
        width = height = 0;
        keyFrame = false;
        frame = [];
        if (envelope.Length <= HeaderBytes || envelope.Length > MaximumPayloadBytes + HeaderBytes
            || !envelope.AsSpan(0, 4).SequenceEqual(Magic)
            || envelope[4] != 1 || envelope[6] > 1 || envelope[7] != 0)
            return false;
        codec = (VideoPayloadCodec)envelope[5];
        width = BinaryPrimitives.ReadInt32BigEndian(envelope.AsSpan(8));
        height = BinaryPrimitives.ReadInt32BigEndian(envelope.AsSpan(12));
        var size = BinaryPrimitives.ReadInt32BigEndian(envelope.AsSpan(16));
        if (!ValidDimensions(width, height) || size != envelope.Length - HeaderBytes
            || !IsValidAccessUnit(codec, envelope.AsSpan(HeaderBytes), out keyFrame)
            || (envelope[6] == 1) != keyFrame
            || !DimensionsMatch(codec, envelope.AsSpan(HeaderBytes), keyFrame, width, height))
            return false;
        frame = envelope.AsSpan(HeaderBytes).ToArray();
        return true;
    }

    internal static bool IsValidAccessUnit(VideoPayloadCodec codec, ReadOnlySpan<byte> frame, out bool keyFrame)
    {
        keyFrame = false;
        if (frame.IsEmpty || frame.Length > MaximumPayloadBytes) return false;
        if (codec == VideoPayloadCodec.Vp8)
        {
            if (frame.Length < 3) return false;
            keyFrame = (frame[0] & 1) == 0;
            return !keyFrame || (frame.Length >= 10 && frame.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 1, 0x2a }));
        }
        if (codec != VideoPayloadCodec.H264) return false;
        var sps = false;
        var pps = false;
        var idr = false;
        var hasSlice = false;
        var nalCount = 0;
        var position = 0;
        while (position < frame.Length)
        {
            var prefix = PrefixLength(frame, position);
            if (prefix == 0 || ++nalCount > 4096) return false;
            var start = position + prefix;
            var end = start;
            while (end < frame.Length && PrefixLength(frame, end) == 0) end++;
            if (end - start < 2) return false;
            var header = frame[start];
            if ((header & 0x80) != 0) return false;
            var type = header & 31;
            if (type == 0 || type >= 24) return false;
            sps |= type == 7;
            pps |= type == 8;
            idr |= type == 5;
            hasSlice |= type is 1 or 5;
            position = end;
        }
        keyFrame = idr && sps && pps;
        return hasSlice && (!idr || keyFrame);
    }

    private static int PrefixLength(ReadOnlySpan<byte> frame, int offset) =>
        offset + 3 <= frame.Length && frame[offset] == 0 && frame[offset + 1] == 0
            ? frame[offset + 2] == 1 ? 3
                : offset + 4 <= frame.Length && frame[offset + 2] == 0 && frame[offset + 3] == 1 ? 4 : 0
            : 0;

    private static bool DimensionsMatch(VideoPayloadCodec codec, ReadOnlySpan<byte> frame,
        bool keyFrame, int width, int height) =>
        codec != VideoPayloadCodec.Vp8 || !keyFrame
        || ((BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) & 0x3fff) == width
            && (BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2)) & 0x3fff) == height);
}
