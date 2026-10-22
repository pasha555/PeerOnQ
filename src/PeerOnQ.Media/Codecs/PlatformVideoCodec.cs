using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Media.Codecs;

public enum VideoPayloadCodec : byte
{
    Vp8 = 1,
    H264 = 2,
}

public static class VideoCodecLimits
{
    public const int MaximumDimension = 8192;
    public const int MaximumPixels = 16 * 1024 * 1024;
    public const int MaximumPayloadBytes = 16 * 1024 * 1024;

    public static bool ValidDimensions(int width, int height) =>
        width > 0 && height > 0 && width <= MaximumDimension && height <= MaximumDimension
        && (width & 1) == 0 && (height & 1) == 0 && (long)width * height <= MaximumPixels;
}

/// <summary>A hardware-only implementation. Throwing permanently selects VP8 for this session.</summary>
public interface IHardwareH264Encoder : IDisposable
{
    string Name { get; }
    byte[] EncodeI420(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame);
}

/// <summary>Decodes authenticated, bounded Annex B access units to owned BGR24 frames.</summary>
public interface IH264VideoDecoder : IDisposable
{
    string Name { get; }
    /// <summary>True only after receiving verified hardware-device decoder output; defaults to CPU.</summary>
    bool HardwareAccelerated => false;
    RemoteVideoFrame[] Decode(int width, int height, byte[] frame, bool reset);
}

/// <summary>An independently owned decoded NV12 image, including its validated native row layout.</summary>
public sealed record DecodedNv12Frame(int Width, int Height, int Stride, int BufferHeight, byte[] Pixels)
{
    public bool HasValidLayout => VideoCodecLimits.ValidDimensions(Width, Height)
        && Stride >= Width && Stride <= VideoCodecLimits.MaximumDimension + 64 && (Stride & 1) == 0
        && BufferHeight >= Height && BufferHeight <= Height + 64 && (BufferHeight & 1) == 0
        && Pixels is not null && Pixels.LongLength >= (long)Stride * BufferHeight * 3 / 2
        && Pixels.LongLength <= (long)VideoCodecLimits.MaximumPixels * 3 / 2 + 1024 * 1024;
}

/// <summary>
/// Keeps native reference decoding ordered while allowing stateless color conversion on a bounded worker.
/// Returned pixels must be independent of decoder state and may be cleared without returning to the decoder.
/// </summary>
public interface IDeferredH264VideoDecoder : IH264VideoDecoder
{
    DecodedNv12Frame[] DecodeNv12(int width, int height, byte[] frame, bool reset);
    byte[] ConvertNv12ToBgr(DecodedNv12Frame frame);
}
