using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders.Codecs;

namespace PeerOnQ.Media.Codecs;

/// <summary>
/// Keeps the existing libvpx decoder and BGR output, but bounds and parallelizes the costly
/// full-screen color conversion instead of running it on a single RTP receive thread.
/// </summary>
internal sealed class Vp8ScreenDecoder : IDisposable
{
    private readonly Lock _gate = new();
    private Vp8Codec? _decoder;
    private bool _disposed;

    public VideoSample[] Decode(byte[] frame)
    {
        lock (_gate)
            return DecodeI420(frame).Select(image => new VideoSample
            {
                Width = image.Width,
                Height = image.Height,
                Sample = I420BgrConverter.Convert(image.Sample, checked((int)image.Width), checked((int)image.Height)),
            }).ToArray();
    }

    /// <summary>
    /// Native reference decoding remains ordered. The pinned codec returns independent managed
    /// I420 planes; a viewer may replace an obsolete decoded image before color conversion.
    /// </summary>
    public VideoSample[] DecodeI420(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_decoder is null)
            {
                var decoder = new Vp8Codec();
                try
                {
                    decoder.InitialiseDecoder();
                    _decoder = decoder;
                }
                catch
                {
                    decoder.Dispose();
                    throw;
                }
            }

            var decoded = _decoder.Decode(frame, frame.Length, out var width, out var height);
            if (decoded is null) return [];

            if (!VideoCodecLimits.ValidDimensions(checked((int)width), checked((int)height)))
                throw new InvalidOperationException("The VP8 decoder returned unbounded image dimensions.");
            var samples = new VideoSample[decoded.Count];
            for (var index = 0; index < decoded.Count; index++)
            {
                samples[index] = new VideoSample
                {
                    Width = width,
                    Height = height,
                    Sample = decoded[index],
                };
            }
            return samples;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _decoder?.Dispose();
            _decoder = null;
        }
    }
}
