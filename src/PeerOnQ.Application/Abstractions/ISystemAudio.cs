using System.Buffers.Binary;

namespace PeerOnQ.Application.Abstractions;

/// <summary>System output only: microphone capture is never part of this interface.</summary>
public interface ISystemAudioCaptureSource : IDisposable
{
    event EventHandler<SystemAudioFrame>? FrameArrived;
    event EventHandler? CaptureFailed;
    void Start();
    void Stop();
}

public interface ISystemAudioPlaybackSink : IDisposable
{
    event EventHandler? PlaybackFailed { add { } remove { } }
    void Write(SystemAudioFrame frame);
    void Clear();
}

public sealed record SystemAudioFrame(long CapturedAtUnixMicroseconds, long Sequence, byte[] Pcm);

/// <summary>A single, bounded desktop-quality format; no peer-selected allocation or codec.</summary>
public static class SystemAudioFrameCodec
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int FrameMilliseconds = 20;
    public const int SamplesPerChannel = SampleRate * FrameMilliseconds / 1000;
    public const int PcmBytes = SamplesPerChannel * Channels * BitsPerSample / 8;
    public const int HeaderBytes = 32;
    public const int EncodedBytes = HeaderBytes + PcmBytes;

    public static byte[] Encode(SystemAudioFrame frame)
    {
        if (frame.CapturedAtUnixMicroseconds <= 0 || frame.Sequence <= 0 || frame.Pcm.Length != PcmBytes)
            throw new ArgumentException("Invalid system audio frame.", nameof(frame));
        var data = new byte[EncodedBytes];
        "PNQA"u8.CopyTo(data);
        data[4] = 1;
        data[5] = Channels;
        data[6] = BitsPerSample;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), SampleRate);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), SamplesPerChannel);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(16), frame.CapturedAtUnixMicroseconds);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(24), frame.Sequence);
        frame.Pcm.CopyTo(data, HeaderBytes);
        return data;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out SystemAudioFrame? frame)
    {
        frame = null;
        if (!TryReadHeader(data, out var timestamp, out var sequence)) return false;
        frame = new SystemAudioFrame(timestamp, sequence, data[HeaderBytes..].ToArray());
        return true;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> data, out long timestamp, out long sequence)
    {
        timestamp = sequence = 0;
        if (data.Length != EncodedBytes || !data[..4].SequenceEqual("PNQA"u8)
            || data[4] != 1 || data[5] != Channels || data[6] != BitsPerSample || data[7] != 0
            || BinaryPrimitives.ReadInt32BigEndian(data[8..]) != SampleRate
            || BinaryPrimitives.ReadInt32BigEndian(data[12..]) != SamplesPerChannel)
            return false;
        timestamp = BinaryPrimitives.ReadInt64BigEndian(data[16..]);
        sequence = BinaryPrimitives.ReadInt64BigEndian(data[24..]);
        if (timestamp <= 0 || sequence <= 0) return false;
        return true;
    }
}

/// <summary>Cumulative delivery metadata on the reverse, direction-bound PNQE Audio context.</summary>
public static class SystemAudioReceiptCodec
{
    public const int EncodedBytes = 16;
    public static byte[] Encode(long sequence)
    {
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        var receipt = new byte[EncodedBytes];
        "PNQK"u8.CopyTo(receipt);
        receipt[4] = 1;
        BinaryPrimitives.WriteInt64BigEndian(receipt.AsSpan(8), sequence);
        return receipt;
    }

    public static bool TryDecode(ReadOnlySpan<byte> receipt, out long sequence)
    {
        sequence = 0;
        if (receipt.Length != EncodedBytes || !receipt[..4].SequenceEqual("PNQK"u8)
            || receipt[4] != 1 || receipt[5] != 0 || receipt[6] != 0 || receipt[7] != 0) return false;
        sequence = BinaryPrimitives.ReadInt64BigEndian(receipt[8..]);
        return sequence > 0;
    }
}
