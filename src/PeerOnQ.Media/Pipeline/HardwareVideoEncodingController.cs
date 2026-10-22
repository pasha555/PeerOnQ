using PeerOnQ.Media.Codecs;

namespace PeerOnQ.Media.Pipeline;

/// <summary>Owns one hardware attempt; device loss cannot create an endless driver retry loop.</summary>
internal sealed class HardwareVideoEncodingController(Func<IHardwareH264Encoder?>? factory) : IDisposable
{
    private IHardwareH264Encoder? _hardware;
    private bool _failed;
    private bool _disposed;
    private bool _hardwareProducedFrame;

    internal EncoderSelection Selection { get; private set; } = new(
        VideoEncoderKind.SoftwareVp8, false, "VP8 until a compatible peer and a working hardware encoder are confirmed");

    internal (VideoPayloadCodec Codec, byte[] Bytes) Encode(Vp8ScreenEncoder software, bool negotiated,
        int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (negotiated && !_failed && factory is not null)
        {
            try
            {
                _hardware ??= factory() ?? throw new InvalidOperationException("No usable hardware encoder.");
                var encoded = _hardware.EncodeI420(width, height, frame, bitrateKbps, frameRate, forceKeyFrame);
                if (encoded.Length == 0) return (VideoPayloadCodec.H264, encoded);
                if (!VideoPayloadEnvelope.IsValidAccessUnit(VideoPayloadCodec.H264, encoded, out var keyFrame)
                    || (!_hardwareProducedFrame && !keyFrame))
                    throw new InvalidOperationException("The hardware encoder returned an invalid H.264 access unit.");
                _hardwareProducedFrame = true;
                Selection = new(VideoEncoderKind.HardwareH264, true, _hardware.Name);
                return (VideoPayloadCodec.H264, encoded);
            }
            catch (Exception ex)
            {
                _failed = true;
                try { _hardware?.Dispose(); }
                catch (Exception) { /* Preserve a working software fallback after driver failure. */ }
                _hardware = null;
                software.ForceKeyFrame();
                Selection = new(VideoEncoderKind.SoftwareVp8, false, $"hardware H.264 failed ({ex.GetType().Name}); using VP8");
            }
        }
        if (forceKeyFrame) software.ForceKeyFrame();
        return (VideoPayloadCodec.Vp8, software.EncodeI420(width, height, frame));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hardware?.Dispose();
        _hardware = null;
    }
}
