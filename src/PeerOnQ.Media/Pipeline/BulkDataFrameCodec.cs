using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// Bounded transport-only fragmentation for encrypted bulk records. Fragment metadata is not
/// trusted; the reassembled PNQE record still requires its normal AEAD verification.
/// </summary>
internal static class BulkDataFrameCodec
{
    internal const int MaximumFragmentPayloadBytes = 64 * 1024;
    internal const int MaximumRecordBytes = 1024 * 1024;

    private const int HeaderBytes = 21;
    private const byte Version = 1;
    private static ReadOnlySpan<byte> Magic => "PNQB"u8;

    internal static byte[] Encode(
        ReadOnlySpan<byte> record,
        ulong recordId,
        int fragmentOffset,
        int fragmentLength)
    {
        if (recordId == 0) throw new ArgumentOutOfRangeException(nameof(recordId));
        if (record.Length is <= 0 or > MaximumRecordBytes)
            throw new ArgumentOutOfRangeException(nameof(record));
        if (fragmentOffset < 0
            || fragmentLength is <= 0 or > MaximumFragmentPayloadBytes
            || fragmentOffset > record.Length - fragmentLength)
        {
            throw new ArgumentOutOfRangeException(nameof(fragmentLength));
        }

        var frame = new byte[HeaderBytes + fragmentLength];
        Magic.CopyTo(frame);
        frame[4] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(5, 8), recordId);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(13, 4), record.Length);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(17, 4), fragmentOffset);
        record.Slice(fragmentOffset, fragmentLength).CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out ulong recordId,
        out int totalLength,
        out int fragmentOffset,
        out ReadOnlySpan<byte> fragment)
    {
        recordId = 0;
        totalLength = 0;
        fragmentOffset = 0;
        fragment = default;
        if (frame.Length <= HeaderBytes
            || !frame[..4].SequenceEqual(Magic)
            || frame[4] != Version)
        {
            return false;
        }

        recordId = BinaryPrimitives.ReadUInt64BigEndian(frame.Slice(5, 8));
        totalLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(13, 4));
        fragmentOffset = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(17, 4));
        fragment = frame[HeaderBytes..];
        return recordId != 0
               && totalLength is > 0 and <= MaximumRecordBytes
               && fragment.Length is > 0 and <= MaximumFragmentPayloadBytes
               && fragmentOffset >= 0
               && fragmentOffset <= totalLength - fragment.Length;
    }
}

internal sealed class BulkDataFrameReassembler(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MaximumAssemblyAge = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private byte[]? _record;
    private ulong _recordId;
    private int _nextOffset;
    private long _startedAt;

    internal bool TryAccept(
        ReadOnlySpan<byte> frame,
        out byte[]? completedRecord,
        out string? error)
    {
        completedRecord = null;
        error = null;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_record is not null
                && _time.GetElapsedTime(_startedAt, now) > MaximumAssemblyAge)
            {
                Reset(clearBuffer: true);
                error = "bulk_fragment_timeout";
                return false;
            }

            if (!BulkDataFrameCodec.TryDecode(
                    frame,
                    out var recordId,
                    out var totalLength,
                    out var fragmentOffset,
                    out var fragment))
            {
                Reset(clearBuffer: true);
                error = "invalid_bulk_fragment";
                return false;
            }

            if (_record is null)
            {
                if (fragmentOffset != 0)
                {
                    error = "invalid_bulk_fragment_order";
                    return false;
                }

                _record = new byte[totalLength];
                _recordId = recordId;
                _nextOffset = 0;
                _startedAt = now;
            }

            if (_recordId != recordId
                || _record.Length != totalLength
                || fragmentOffset != _nextOffset)
            {
                Reset(clearBuffer: true);
                error = "invalid_bulk_fragment_order";
                return false;
            }

            fragment.CopyTo(_record.AsSpan(fragmentOffset));
            _nextOffset += fragment.Length;
            if (_nextOffset != _record.Length) return false;

            completedRecord = _record;
            Reset(clearBuffer: false);
            return true;
        }
    }

    internal void Clear()
    {
        lock (_gate) Reset(clearBuffer: true);
    }

    private void Reset(bool clearBuffer)
    {
        if (clearBuffer && _record is not null)
            CryptographicOperations.ZeroMemory(_record);
        _record = null;
        _recordId = 0;
        _nextOffset = 0;
        _startedAt = 0;
    }
}
