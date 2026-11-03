using PeerOnQ.Media.Codecs;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// Decides when the encode loop re-encodes a desktop that stopped changing, so the encoder spends
/// the idle link on the residual and text turns sharp ("top-off"). Change-driven capture sends
/// nothing once the screen is still, which left scrolled text at the quality of its last moving
/// frame. Re-encoding the still frame is bit-limited, not time-limited: dense 1080p text at 60 fps
/// reached 56 dB after 2 s at 10 Mbps, 52 dB after 4 s at 5 Mbps and was still rising (29 dB)
/// after 4 s at 2 Mbps (AMD hardware H.264, HardwareStaticRefinementTests). The window therefore
/// lasts as long as <see cref="RefinementMegabits"/> takes at the current bitrate.
///
/// The window is bounded because a CBR encoder keeps spending its budget on a converged frame;
/// a small change (caret, clock, pointer) does not reopen it, and a key frame does, since an
/// IDR restarts from the rate-control floor. Both encoders insert a safety-net IDR after 5 s of
/// frames, so the window stays shorter: a longer one would meet the next periodic IDR, re-arm on
/// it and keep a still desktop encoding at full bitrate forever.
/// </summary>
internal sealed class StaticRefinement
{
    /// <summary>Bits a still desktop may receive after a significant change (about 2.4 MB).</summary>
    internal const double RefinementMegabits = 20;

    /// <summary>Shortest and longest refinement window, whatever the bitrate.</summary>
    internal static readonly TimeSpan MinimumDuration = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaximumDuration =
        TimeSpan.FromSeconds(Vp8ScreenEncoder.KeyFrameSafetyNetSeconds - 1);

    /// <summary>Share of sampled luma that must change for a frame to reopen refinement.</summary>
    internal const double SignificantChangeFraction = 0.002;

    /// <summary>Luma is sampled on this grid; 1080p keeps 32,400 samples.</summary>
    internal const int SampleStep = 8;

    /// <summary>A sample changed when it moved by more than this (capture noise is smaller).</summary>
    internal const int ChangedSampleDelta = 6;

    private byte[] _samples = [];
    private int _width, _height;
    private int _remaining;

    /// <summary>Refinement frames still allowed for the current still desktop.</summary>
    internal int Remaining => _remaining;

    /// <summary>Records a captured frame's luma plane; a significant change reopens refinement.</summary>
    internal void Observe(int width, int height, ReadOnlySpan<byte> luma, int framesPerSecond, int bitrateKbps)
    {
        if (width <= 0 || height <= 0 || luma.Length < width * height)
        {
            Reset();
            return;
        }

        var columns = (width + SampleStep - 1) / SampleStep;
        var rows = (height + SampleStep - 1) / SampleStep;
        var resized = width != _width || height != _height || _samples.Length != columns * rows;
        if (resized)
        {
            _samples = new byte[columns * rows];
            _width = width;
            _height = height;
        }

        var changed = 0;
        var index = 0;
        for (var y = 0; y < height; y += SampleStep)
        {
            var row = luma.Slice(y * width, width);
            for (var x = 0; x < width; x += SampleStep, index++)
            {
                var value = row[x];
                if (Math.Abs(value - _samples[index]) > ChangedSampleDelta) changed++;
                _samples[index] = value;
            }
        }

        if (resized || changed >= Math.Max(1, (int)(_samples.Length * SignificantChangeFraction)))
            Arm(framesPerSecond, bitrateKbps);
    }

    /// <summary>An encoded key frame restarts from the rate-control floor and needs refining again.</summary>
    internal void KeyFrameEncoded(int framesPerSecond, int bitrateKbps) => Arm(framesPerSecond, bitrateKbps);

    /// <summary>The refinement window at this bitrate: <see cref="RefinementMegabits"/>, within bounds.</summary>
    internal static TimeSpan Duration(int bitrateKbps) =>
        TimeSpan.FromSeconds(Math.Clamp(
            RefinementMegabits * 1000 / Math.Max(1, bitrateKbps),
            MinimumDuration.TotalSeconds,
            MaximumDuration.TotalSeconds));

    /// <summary>Consumes one refinement frame; false once the window is spent.</summary>
    internal bool TryTake()
    {
        if (_remaining <= 0) return false;
        _remaining--;
        return true;
    }

    internal void Reset()
    {
        _remaining = 0;
        _samples = [];
        _width = _height = 0;
    }

    private void Arm(int framesPerSecond, int bitrateKbps) =>
        _remaining = (int)Math.Ceiling(Duration(bitrateKbps).TotalSeconds * Math.Clamp(framesPerSecond, 1, 120));

    /// <summary>True for an H.264 access unit with an IDR slice or a VP8 key frame.</summary>
    internal static bool IsKeyFrame(VideoPayloadCodec codec, ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty) return false;
        // VP8 frame tag: bit 0 is the frame type, 0 for a key frame (RFC 6386 9.1).
        if (codec == VideoPayloadCodec.Vp8) return (encoded[0] & 1) == 0;
        for (var index = 0; index + 3 < encoded.Length; index++)
        {
            if (encoded[index] != 0 || encoded[index + 1] != 0) continue;
            var prefix = encoded[index + 2] == 1 ? 3
                : index + 4 < encoded.Length && encoded[index + 2] == 0 && encoded[index + 3] == 1 ? 4 : 0;
            if (prefix != 0 && (encoded[index + prefix] & 31) == 5) return true;
        }
        return false;
    }
}
