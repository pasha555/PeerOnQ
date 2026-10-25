namespace PeerOnQ.Platform.Android;

public readonly record struct Vp8FrameDimensions(int Width, int Height);

/// <summary>Reads the uncompressed VP8 key-frame header before configuring Android MediaCodec.</summary>
public static class Vp8KeyFrameParser
{
    private const int HeaderBytes = 10;

    public static bool TryReadDimensions(
        ReadOnlySpan<byte> frame,
        out Vp8FrameDimensions dimensions)
    {
        dimensions = default;
        if (frame.Length < HeaderBytes
            || (frame[0] & 0x01) != 0
            || frame[3] != 0x9d
            || frame[4] != 0x01
            || frame[5] != 0x2a)
        {
            return false;
        }

        var width = (frame[6] | frame[7] << 8) & 0x3fff;
        var height = (frame[8] | frame[9] << 8) & 0x3fff;
        if (width <= 0 || height <= 0) return false;

        dimensions = new Vp8FrameDimensions(width, height);
        return true;
    }
}
