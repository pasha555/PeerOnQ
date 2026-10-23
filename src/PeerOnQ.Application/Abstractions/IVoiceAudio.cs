using System.Buffers.Binary;

namespace PeerOnQ.Application.Abstractions;

/// <summary>
/// Microphone capture for two-way voice. It is opened only after the local user explicitly
/// enables voice for a session; a remote peer can never open this device's microphone.
/// </summary>
public interface IVoiceCaptureSource : IDisposable
{
    event EventHandler<VoicePcmFrame>? FrameArrived;
    event EventHandler? CaptureFailed;
    void Start();
    void Stop();
}

/// <summary>Plays the peer's decoded voice; the sink bounds its own buffering latency.</summary>
public interface IVoicePlaybackSink : IDisposable
{
    event EventHandler? PlaybackFailed;
    void Write(ReadOnlySpan<short> samples);
    void Clear();
}

/// <summary>One 20 ms mono frame of 48 kHz 16-bit PCM.</summary>
public sealed record VoicePcmFrame(long CapturedAtUnixMicroseconds, short[] Samples);

/// <summary>The single bounded voice format: Opus-friendly 48 kHz mono, 20 ms frames.</summary>
public static class VoiceAudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int FrameMilliseconds = 20;
    public const int SamplesPerFrame = SampleRate * FrameMilliseconds / 1000;
    public const int PcmBytesPerFrame = SamplesPerFrame * BitsPerSample / 8;

    /// <summary>RFC 6716 upper bound for one Opus packet.</summary>
    public const int MaximumEncodedBytes = 1275;
}

/// <summary>
/// Plaintext voice record carried inside the authenticated, lossy Voice secure context:
/// "PNQV", version 1, three reserved zero bytes, a positive sequence, the capture time in Unix
/// microseconds and one Opus packet. Every field is bounded before decoding.
/// </summary>
public static class VoiceFrameCodec
{
    public const int HeaderBytes = 24;
    public const int MaximumBytes = HeaderBytes + VoiceAudioFormat.MaximumEncodedBytes;

    public static byte[] Encode(long sequence, long capturedAtUnixMicroseconds, ReadOnlySpan<byte> opus)
    {
        if (sequence <= 0 || capturedAtUnixMicroseconds <= 0
            || opus.IsEmpty || opus.Length > VoiceAudioFormat.MaximumEncodedBytes)
            throw new ArgumentException("Invalid voice frame.");
        var data = new byte[HeaderBytes + opus.Length];
        "PNQV"u8.CopyTo(data);
        data[4] = 1;
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(8), sequence);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(16), capturedAtUnixMicroseconds);
        opus.CopyTo(data.AsSpan(HeaderBytes));
        return data;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> data,
        out long sequence,
        out long capturedAtUnixMicroseconds,
        out ReadOnlySpan<byte> opus)
    {
        sequence = capturedAtUnixMicroseconds = 0;
        opus = default;
        if (data.Length <= HeaderBytes || data.Length > MaximumBytes
            || !data[..4].SequenceEqual("PNQV"u8)
            || data[4] != 1 || data[5] != 0 || data[6] != 0 || data[7] != 0)
            return false;
        sequence = BinaryPrimitives.ReadInt64BigEndian(data[8..]);
        capturedAtUnixMicroseconds = BinaryPrimitives.ReadInt64BigEndian(data[16..]);
        if (sequence <= 0 || capturedAtUnixMicroseconds <= 0) return false;
        opus = data[HeaderBytes..];
        return true;
    }
}
