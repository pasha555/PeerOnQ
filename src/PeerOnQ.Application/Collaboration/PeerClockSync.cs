using System.Buffers.Binary;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Application.Collaboration;

internal static class ClockSyncCodec
{
    private static readonly byte[] Magic = "PNQT"u8.ToArray();
    private const byte Version = 1;
    private const byte RequestType = 1;
    private const byte ReplyType = 2;
    private const int PrefixBytes = 6;
    private const int RequestBytes = PrefixBytes + 8 + 8;
    private const int ReplyBytes = RequestBytes + 8 + 8;

    public static byte[] EncodeRequest(ulong requestId, long localSentAtUnixMicroseconds)
    {
        if (requestId == 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(localSentAtUnixMicroseconds);

        var frame = new byte[RequestBytes];
        WritePrefix(frame, RequestType);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(PrefixBytes, 8), requestId);
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(PrefixBytes + 8, 8), localSentAtUnixMicroseconds);
        return frame;
    }

    public static byte[] EncodeReply(
        ulong requestId,
        long localSentAtUnixMicroseconds,
        long remoteReceivedAtUnixMicroseconds,
        long remoteSentAtUnixMicroseconds)
    {
        if (requestId == 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(localSentAtUnixMicroseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(remoteReceivedAtUnixMicroseconds);
        if (remoteSentAtUnixMicroseconds < remoteReceivedAtUnixMicroseconds)
            throw new ArgumentOutOfRangeException(nameof(remoteSentAtUnixMicroseconds));

        var frame = new byte[ReplyBytes];
        WritePrefix(frame, ReplyType);
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(PrefixBytes, 8), requestId);
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(PrefixBytes + 8, 8), localSentAtUnixMicroseconds);
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(PrefixBytes + 16, 8), remoteReceivedAtUnixMicroseconds);
        BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(PrefixBytes + 24, 8), remoteSentAtUnixMicroseconds);
        return frame;
    }

    public static bool TryDecodeRequest(
        ReadOnlySpan<byte> frame,
        out ulong requestId,
        out long localSentAtUnixMicroseconds)
    {
        requestId = 0;
        localSentAtUnixMicroseconds = 0;
        if (!HasPrefix(frame, RequestBytes, RequestType)) return false;

        requestId = BinaryPrimitives.ReadUInt64BigEndian(frame.Slice(PrefixBytes, 8));
        localSentAtUnixMicroseconds = BinaryPrimitives.ReadInt64BigEndian(frame.Slice(PrefixBytes + 8, 8));
        return requestId != 0 && localSentAtUnixMicroseconds > 0;
    }

    public static bool TryDecodeReply(
        ReadOnlySpan<byte> frame,
        out ulong requestId,
        out long localSentAtUnixMicroseconds,
        out long remoteReceivedAtUnixMicroseconds,
        out long remoteSentAtUnixMicroseconds)
    {
        requestId = 0;
        localSentAtUnixMicroseconds = 0;
        remoteReceivedAtUnixMicroseconds = 0;
        remoteSentAtUnixMicroseconds = 0;
        if (!HasPrefix(frame, ReplyBytes, ReplyType)) return false;

        requestId = BinaryPrimitives.ReadUInt64BigEndian(frame.Slice(PrefixBytes, 8));
        localSentAtUnixMicroseconds = BinaryPrimitives.ReadInt64BigEndian(frame.Slice(PrefixBytes + 8, 8));
        remoteReceivedAtUnixMicroseconds = BinaryPrimitives.ReadInt64BigEndian(frame.Slice(PrefixBytes + 16, 8));
        remoteSentAtUnixMicroseconds = BinaryPrimitives.ReadInt64BigEndian(frame.Slice(PrefixBytes + 24, 8));
        return requestId != 0
               && localSentAtUnixMicroseconds > 0
               && remoteReceivedAtUnixMicroseconds > 0
               && remoteSentAtUnixMicroseconds >= remoteReceivedAtUnixMicroseconds;
    }

    private static void WritePrefix(Span<byte> frame, byte type)
    {
        Magic.CopyTo(frame);
        frame[4] = Version;
        frame[5] = type;
    }

    private static bool HasPrefix(ReadOnlySpan<byte> frame, int exactLength, byte type) =>
        frame.Length == exactLength
        && frame[..4].SequenceEqual(Magic)
        && frame[4] == Version
        && frame[5] == type;
}

internal static class PeerClockEstimator
{
    private const long MaximumSampleSpanMicroseconds = 5_000_000;

    public static bool TryCalculate(
        long localSentAtUnixMicroseconds,
        long remoteReceivedAtUnixMicroseconds,
        long remoteSentAtUnixMicroseconds,
        long localReceivedAtUnixMicroseconds,
        out PeerClockEstimate estimate)
    {
        estimate = default;
        if (localSentAtUnixMicroseconds <= 0
            || remoteReceivedAtUnixMicroseconds <= 0
            || remoteSentAtUnixMicroseconds < remoteReceivedAtUnixMicroseconds
            || localReceivedAtUnixMicroseconds < localSentAtUnixMicroseconds)
        {
            return false;
        }

        var localElapsed = (decimal)localReceivedAtUnixMicroseconds - localSentAtUnixMicroseconds;
        var remoteProcessing = (decimal)remoteSentAtUnixMicroseconds - remoteReceivedAtUnixMicroseconds;
        var networkRoundTrip = localElapsed - remoteProcessing;
        if (localElapsed > MaximumSampleSpanMicroseconds || networkRoundTrip < 0)
            return false;

        var offset = (((decimal)remoteReceivedAtUnixMicroseconds - localSentAtUnixMicroseconds)
                      + ((decimal)remoteSentAtUnixMicroseconds - localReceivedAtUnixMicroseconds)) / 2;
        var uncertainty = networkRoundTrip / 2;
        if (offset is < long.MinValue or > long.MaxValue || uncertainty > long.MaxValue)
            return false;

        estimate = new PeerClockEstimate(
            decimal.ToInt64(decimal.Round(offset, 0, MidpointRounding.ToEven)),
            decimal.ToInt64(decimal.Ceiling(uncertainty)));
        return true;
    }
}
