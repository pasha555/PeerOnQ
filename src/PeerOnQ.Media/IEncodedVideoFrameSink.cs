using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Media;

/// <summary>An authenticated VP8 frame ready for a platform-native decoder.</summary>
public readonly record struct AuthenticatedVp8Frame(
    ReadOnlyMemory<byte> EncodedBytes,
    long CaptureTimestampUnixMicroseconds,
    long CaptureSequenceNumber,
    PeerClockEstimate? PeerClockEstimate);

/// <summary>A native decoder presentation correlated to the authenticated input frame.</summary>
public readonly record struct EncodedVideoFramePresentation(
    PresentedVideoFrame Frame,
    int EncodedBytes);

/// <summary>
/// Optional platform decoder boundary for viewers that cannot load the desktop libvpx binary.
/// Implementations must copy or synchronously queue <see cref="AuthenticatedVp8Frame.EncodedBytes"/>
/// before <see cref="TrySubmit"/> returns; the media layer zeroes that buffer immediately after.
/// </summary>
public interface IEncodedVideoFrameSink : IDisposable
{
    event EventHandler<EncodedVideoFramePresentation>? FramePresented;

    /// <returns>False when the frame was not accepted and the sender should produce a key frame.</returns>
    bool TrySubmit(AuthenticatedVp8Frame frame);
}
