using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Codecs;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// Owns a bounded number of hardware attempts. A transient driver timeout must not leave a 4K
/// session on software VP8 for its whole lifetime, but device loss still cannot create an
/// endless driver retry loop: after <see cref="MaximumHardwareAttempts"/> failures VP8 is final.
/// </summary>
internal sealed class HardwareVideoEncodingController(
    Func<IHardwareH264Encoder?>? factory,
    TimeProvider? timeProvider = null) : IDisposable
{
    internal const int MaximumHardwareAttempts = 3;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private IHardwareH264Encoder? _hardware;
    private int _failures;
    private long _retryAfterTimestamp;
    private bool _disposed;
    private bool _hardwareProducedFrame;

    internal EncoderSelection Selection { get; private set; } = new(
        VideoEncoderKind.SoftwareVp8, false, "VP8 until a compatible peer and a working hardware encoder are confirmed");

    /// <summary>Number of hardware encoder failures observed in this session.</summary>
    internal int HardwareFailures => Volatile.Read(ref _failures);

    /// <summary>
    /// True while hardware H.264 can still be used now or after its bounded retry delay. A
    /// permanent fallback means callers must not plan 4K frames for software VP8.
    /// </summary>
    internal bool IsHardwareAvailable => factory is not null && Volatile.Read(ref _failures) < MaximumHardwareAttempts;

    /// <summary>
    /// True while a failed hardware encoder is waiting for its retry. Frames in this window are
    /// software VP8, so callers must plan standard-tier frames until hardware succeeds again.
    /// </summary>
    internal bool IsAwaitingHardwareRetry => Volatile.Read(ref _failures) > 0 && !Selection.IsHardware;

    internal (VideoPayloadCodec Codec, byte[] Bytes) Encode(Vp8ScreenEncoder software, bool negotiated,
        int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame,
        CapturedPixelFormat format = CapturedPixelFormat.I420)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (negotiated && factory is not null && _failures < MaximumHardwareAttempts
            && _time.GetTimestamp() >= _retryAfterTimestamp)
        {
            try
            {
                _hardware ??= factory() ?? throw new InvalidOperationException("No usable hardware encoder.");
                // A size outside the hardware window (for example a 7680-wide strip) is not a
                // driver failure: encode that frame in VP8 without spending a retry.
                if (!_hardware.SupportsDimensions(width, height))
                {
                    // Either codec switch must start on a key frame at the receiver.
                    var switchingFromHardware = _hardwareProducedFrame;
                    _hardwareProducedFrame = false;
                    Selection = new(VideoEncoderKind.SoftwareVp8, false, "frame size outside the hardware H.264 window; using VP8");
                    return EncodeSoftware(software, width, height, frame, format, forceKeyFrame || switchingFromHardware);
                }
                // A recreated encoder follows VP8 frames, so its first picture must be a key frame.
                var keyFrameRequested = forceKeyFrame || !_hardwareProducedFrame;
                var encoded = format == CapturedPixelFormat.Nv12
                    ? _hardware.EncodeNv12(width, height, frame, bitrateKbps, frameRate, keyFrameRequested)
                    : _hardware.EncodeI420(width, height, frame, bitrateKbps, frameRate, keyFrameRequested);
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
                var failures = Interlocked.Increment(ref _failures);
                try { _hardware?.Dispose(); }
                catch (Exception) { /* Preserve a working software fallback after driver failure. */ }
                _hardware = null;
                _hardwareProducedFrame = false;
                if (failures < MaximumHardwareAttempts)
                {
                    var delay = RetryDelays[Math.Min(failures - 1, RetryDelays.Length - 1)];
                    _retryAfterTimestamp = _time.GetTimestamp()
                                           + (long)(delay.TotalSeconds * _time.TimestampFrequency);
                }
                software.ForceKeyFrame();
                Selection = new(VideoEncoderKind.SoftwareVp8, false, failures < MaximumHardwareAttempts
                    ? $"hardware H.264 failed ({ex.GetType().Name}); VP8 until retry {failures + 1}"
                    : $"hardware H.264 failed ({ex.GetType().Name}); using VP8");
            }
        }
        return EncodeSoftware(software, width, height, frame, format, forceKeyFrame);
    }

    private (VideoPayloadCodec Codec, byte[] Bytes) EncodeSoftware(Vp8ScreenEncoder software,
        int width, int height, byte[] frame, CapturedPixelFormat format, bool forceKeyFrame)
    {
        if (forceKeyFrame) software.ForceKeyFrame();
        var i420 = format == CapturedPixelFormat.Nv12 ? Nv12Planes.ToI420(width, height, frame) : frame;
        return (VideoPayloadCodec.Vp8, software.EncodeI420(width, height, i420));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hardware?.Dispose();
        _hardware = null;
    }
}
