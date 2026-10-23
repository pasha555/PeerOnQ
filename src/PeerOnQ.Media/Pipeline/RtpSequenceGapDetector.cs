namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// Detects RTP packets that never arrived. A lost packet inside a frame already fails frame
/// authentication and requests a key frame, but a static desktop sends many one-packet P-frames:
/// losing such a frame entirely is otherwise invisible and its prediction error would persist
/// until the next periodic IDR. Reordered (older) and duplicate packets are ignored, and an SSRC
/// change (renegotiation or ICE restart) restarts the sequence space.
/// </summary>
internal sealed class RtpSequenceGapDetector
{
    /// <summary>Forward jumps above this are a sender restart, not loss.</summary>
    internal const int MaximumGap = 3000;

    private readonly Lock _gate = new();
    private uint _ssrc;
    private int _lastSequence = -1;

    /// <summary>Returns true when packets between the previous and this sequence were lost.</summary>
    public bool Observe(uint ssrc, ushort sequence)
    {
        lock (_gate)
        {
            if (_lastSequence < 0 || ssrc != _ssrc)
            {
                _ssrc = ssrc;
                _lastSequence = sequence;
                return false;
            }

            var delta = (ushort)(sequence - (ushort)_lastSequence);
            // 0 is a duplicate; the upper half of the sequence space is an older, reordered packet.
            if (delta == 0 || delta >= 0x8000) return false;
            _lastSequence = sequence;
            return delta > 1 && delta <= MaximumGap;
        }
    }
}
