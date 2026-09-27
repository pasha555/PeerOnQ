using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders.Codecs;

namespace PeerOnQ.Media.Codecs;

/// <summary>
/// Keeps the existing libvpx decoder and BGR output, but bounds and parallelizes the costly
/// full-screen color conversion instead of running it on a single RTP receive thread.
/// </summary>
internal sealed class Vp8ScreenDecoder : IDisposable
{
    private static readonly int ConversionParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    private readonly Lock _gate = new();
    private Vp8Codec? _decoder;
    private bool _disposed;

    static Vp8ScreenDecoder()
    {
        // The pinned PixelConverter caches ParallelOptions in an ordinary Dictionary. Populate
        // both keys before any session can enter this decoder, so concurrent sessions only read
        // that cache. The conversion itself must not take a process-wide lock.
        byte[] pixel = [16, 16, 16, 16, 128, 128];
        _ = PixelConverter.I420toBGR(pixel, 2, 2, out _, dop: 1);
        _ = PixelConverter.I420toBGR(pixel, 2, 2, out _, dop: ConversionParallelism);
    }

    public VideoSample[] Decode(byte[] frame)
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

            var samples = new VideoSample[decoded.Count];
            // Small frames are cheaper than scheduling worker tasks. Reserve parallel conversion
            // for full-HD and larger frames so thumbnail/low-bandwidth sessions do not contend
            // with the interactive input lane for thread-pool workers.
            var parallelism = (long)width * height >= 1920 * 1080 ? ConversionParallelism : 1;
            for (var index = 0; index < decoded.Count; index++)
            {
                samples[index] = new VideoSample
                {
                    Width = width,
                    Height = height,
                    Sample = PixelConverter.I420toBGR(
                        decoded[index], checked((int)width), checked((int)height), out _, parallelism),
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
