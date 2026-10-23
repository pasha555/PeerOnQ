using Microsoft.Extensions.Logging;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Media;

/// <summary>
/// A compact media summary every 10 s while connected, on both roles. Field reports such as
/// "the viewer shows nothing" are otherwise unanswerable from logs: this records whether frames
/// were captured, encoded, received, decoded and rendered, with which codec and path, and why the
/// sharer last changed quality. It holds no screen content, keys or peer identifiers.
/// </summary>
public sealed partial class WebRtcMediaSession
{
    private static readonly TimeSpan MediaSummaryInterval = TimeSpan.FromSeconds(10);
    private Timer? _mediaSummaryTimer;

    private void InitializeMediaSummary() =>
        _mediaSummaryTimer = new Timer(_ => LogMediaSummary(), null, MediaSummaryInterval, MediaSummaryInterval);

    private void LogMediaSummary()
    {
        try
        {
            if (State != MediaConnectionState.Connected || !_log.IsEnabled(LogLevel.Information)) return;
            var stats = GetStatistics();
            var level = Adaptive?.Current.ToString() ?? "n/a";
            var viewer = GetFreshRemoteQualityFeedback();
            static string I(FormattableString text) => FormattableString.Invariant(text);
            var summary = Role == SessionRole.Sharer
                ? I($"encoder={stats.EncoderName} h264={IsH264PayloadNegotiated} rtx={IsVideoRetransmissionNegotiated} ") +
                  I($"{stats.EncodedWidth}x{stats.EncodedHeight} capture={stats.CaptureFps:0.#}fps encode={stats.EncodeFps:0.#}fps ") +
                  I($"{stats.CurrentBitrateKbps:0}kbps encoded={stats.FramesEncoded} dropped={stats.FramesDropped} ") +
                  I($"level={level} reason={Adaptive?.LastDecision.ReasonCode} rtt={stats.RttMs:0}ms loss={stats.PacketLossPercent:0.#}% ") +
                  I($"jitter={stats.JitterMs:0}ms viewerDecode={(viewer?.DecodeFps ?? -1):0.#}fps viewerRender={(viewer?.RenderFps ?? -1):0.#}fps ") +
                  I($"retransmitted={stats.VideoPacketsRetransmitted} path={stats.ConnectionPath}")
                : I($"decoder={stats.DecoderName} h264={IsH264PayloadNegotiated} rtx={IsVideoRetransmissionNegotiated} ") +
                  I($"{stats.RenderedWidth}x{stats.RenderedHeight} decode={stats.DecodeFps:0.#}fps render={stats.RenderFps:0.#}fps ") +
                  I($"{stats.CurrentBitrateKbps:0}kbps rendered={stats.FramesRendered} recovered={stats.VideoPacketsRecovered} ") +
                  I($"skipped={VideoFramesSkipped} frameAgeP95={stats.CaptureToPresentLatencyP95Ms:0}ms ") +
                  I($"queue={stats.VideoPresentationQueueDepth} path={stats.ConnectionPath}");
            LogMediaSummaryLine(_log, SessionId, Role, summary);
        }
        catch (Exception ex)
        {
            LogVideoAssemblyError(_log, ex);
        }
    }

    private void CloseMediaSummary() => Interlocked.Exchange(ref _mediaSummaryTimer, null)?.Dispose();

    [LoggerMessage(1401, LogLevel.Information, "Media {SessionId} {Role}: {Summary}")]
    private static partial void LogMediaSummaryLine(ILogger logger, SessionId sessionId, SessionRole role, string summary);
}
