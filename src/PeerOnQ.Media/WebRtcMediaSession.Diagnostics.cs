using Microsoft.Extensions.Logging;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Pipeline;

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
            if (!_log.IsEnabled(LogLevel.Information) || _lifetime.IsCancellationRequested) return;
            // The sharer reports in every state: on 0.9.87 LNK-363 lost media before the first
            // connected tick and its stalled encoder left no summary at all.
            if (Role != SessionRole.Sharer && State != MediaConnectionState.Connected) return;
            var stats = GetStatistics();
            var level = Adaptive?.Current.ToString() ?? "n/a";
            var viewer = GetFreshRemoteQualityFeedback();
            static string I(FormattableString text) => FormattableString.Invariant(text);
            var vp8 = _encoder?.GetDiagnostics();
            var summary = Role == SessionRole.Sharer
                ? I($"state={State} encoder={stats.EncoderName} h264={IsH264PayloadNegotiated} rtx={IsVideoRetransmissionNegotiated} ") +
                  (vp8 is { } software
                      ? I($"vp8Encode p50={software.EncodeP50Ms:0.0}ms p95={software.EncodeP95Ms:0.0}ms threads={software.Threads} vp8State=\"{software.State}\" ")
                      : string.Empty) +
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

    /// <summary>The policy reason, with the measured frame age behind a frame-age decision.</summary>
    private static string DescribeQualityReason(AdaptiveQualityController? adaptive)
    {
        if (adaptive is null) return "initial";
        var reason = adaptive.LastDecision.ReasonCode;
        if (adaptive.LastSample is not { } sample) return reason;
        return reason switch
        {
            "viewer_frame_age" => FormattableString.Invariant(
                $"{reason}: p95 {sample.ReceiverCaptureToPresentLatencyP95Ms:0} ms > target {sample.ReceiverCaptureToPresentTargetMs:0} ms + transit, clock uncertainty {sample.ReceiverFrameAgeClockUncertaintyMs:0} ms, loss {sample.PacketLossFraction * 100:0.#}%"),
            "viewer_render_latency" => FormattableString.Invariant(
                $"{reason}: decode-to-render p95 {sample.ReceiverDecodeToRenderLatencyP95Ms:0} ms > max({ConnectionPolicyProvider.DegradeDecodeToRenderLatencyP95Ms:0} ms, {ConnectionPolicyProvider.RenderLatencyFrameIntervals:0} frame intervals) while render {sample.ReceiverRenderFps:0.#} fps < {ConnectionPolicyProvider.RenderLatencyRenderToDecodeRatio:0.##} x decode {sample.ReceiverDecodeFps:0.#} fps"),
            "viewer_input_latency" => FormattableString.Invariant(
                $"{reason}: input-to-injection p95 {sample.ReceiverInputToInjectionLatencyP95Ms:0} ms > {ConnectionPolicyProvider.TargetInputToInjectionP95Ms:0} ms + transit, clock uncertainty {sample.ReceiverInputClockUncertaintyMs:0} ms"),
            "viewer_render_backpressure" => FormattableString.Invariant(
                $"{reason}: render {sample.ReceiverRenderFps:0.#} fps < {ConnectionPolicyProvider.MinimumRenderToDecodeRatio:0.##} x decode {sample.ReceiverDecodeFps:0.#} fps"),
            _ => reason,
        };
    }

    /// <summary>Sharer: a new capture source starts from the start level, not an inherited low one.</summary>
    public void RestartQuality()
    {
        if (Role != SessionRole.Sharer || Adaptive is not { } adaptive) return;
        adaptive.Reset();
        RequestEncoderKeyFrame();
    }

    [LoggerMessage(1401, LogLevel.Information, "Media {SessionId} {Role}: {Summary}")]
    private static partial void LogMediaSummaryLine(ILogger logger, SessionId sessionId, SessionRole role, string summary);
}
