using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Codecs;

namespace PeerOnQ.Media;

/// <summary>
/// Video watchdog. A frozen picture must never be silent.
///
/// Viewer: while media is connected and nothing has been decoded for <see cref="VideoStallAfter"/>,
/// it asks the sharer for a picture with an explicit PLI, backing off to one request per
/// <see cref="MaximumStallRequestInterval"/>, and logs when video stops and resumes. A change-driven
/// capture re-emits its last picture for a PLI, so a static desktop costs one key frame per 10 s.
///
/// Sharer: a PLI that finds capture idle for <see cref="CaptureIdleBeforeRestart"/> is logged with the
/// capture and encode ages; when the requested re-emit still produces nothing, the capture of the
/// same display is restarted (at most once per <see cref="MinimumCaptureRestartInterval"/>).
/// </summary>
public sealed partial class WebRtcMediaSession
{
    private static readonly TimeSpan VideoStallAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumStallRequestInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CaptureIdleBeforeRestart = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CaptureRefreshGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumCaptureRestartInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan SharerEncodeStallAfter = TimeSpan.FromSeconds(3);

    private Timer? _videoWatchdogTimer;
    private long _watchdogBytesReceived = -1;
    private long _lastVideoProgressTimestamp;
    private long _nextStallRequestTimestamp;
    private int _stallRequests;
    private long _stallStartedTimestamp;
    private long _lastCaptureTimestamp;
    private long _lastEncodedTimestamp;
    private long _lastCaptureRestartTimestamp;
    private int _captureRestartPending;
    private long _firstUnencodedCaptureTimestamp;
    private long _sharerEncodeStallTimestamp;
    private long _mediaConnectedTimestamp;
    private int _firstFrameEncoded;

    /// <summary>Viewer: seconds without a decoded frame while connected, once that counts as a stall.</summary>
    private double VideoStalledSeconds
    {
        get
        {
            var started = Volatile.Read(ref _stallStartedTimestamp);
            return started == 0 ? 0 : Stopwatch.GetElapsedTime(Volatile.Read(ref _lastVideoProgressTimestamp)).TotalSeconds;
        }
    }

    private void InitializeVideoWatchdog()
    {
        _videoWatchdogTimer = Role == SessionRole.Viewer
            ? new Timer(_ => CheckVideoStall(Stopwatch.GetTimestamp()), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1))
            : new Timer(_ => CheckSharerEncoding(Stopwatch.GetTimestamp()), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void CloseVideoWatchdog() => Interlocked.Exchange(ref _videoWatchdogTimer, null)?.Dispose();

    /// <summary>
    /// Sharer: a captured frame that no encode attempt has taken for <see cref="SharerEncodeStallAfter"/>
    /// is a local stall, not a network loss. It is logged once with the gate that holds it and
    /// never ends the session or starts an ICE restart; the encoder's own watchdog replaces a
    /// stuck native encoder.
    /// </summary>
    internal void CheckSharerEncoding(long now)
    {
        try
        {
            var pendingSince = Volatile.Read(ref _firstUnencodedCaptureTimestamp);
            var stalledSince = Volatile.Read(ref _sharerEncodeStallTimestamp);
            if (pendingSince == 0)
            {
                if (stalledSince != 0 && Interlocked.CompareExchange(ref _sharerEncodeStallTimestamp, 0, stalledSince) == stalledSince)
                    LogSharerEncodingResumed(_log, SessionId, Stopwatch.GetElapsedTime(stalledSince, now).TotalSeconds);
                return;
            }

            // Before media first connects the gate is closed on purpose (the initial frame waits).
            var connectedAt = Volatile.Read(ref _mediaConnectedTimestamp);
            if (connectedAt == 0) return;
            var waiting = Stopwatch.GetElapsedTime(Math.Max(pendingSince, connectedAt), now);
            if (waiting < SharerEncodeStallAfter || stalledSince != 0) return;
            if (Interlocked.CompareExchange(ref _sharerEncodeStallTimestamp, pendingSince, 0) != 0) return;
            LogSharerVideoStalled(_log, SessionId, waiting.TotalSeconds, _statistics.Snapshot().FramesCaptured,
                DescribeEncodeGate());
        }
        catch (Exception ex)
        {
            LogVideoAssemblyError(_log, ex);
        }
    }

    /// <summary>
    /// For the process stall watchdog: the longer of a native encoder call in progress and a
    /// captured frame waiting for an encode attempt after media connected.
    /// </summary>
    public TimeSpan? VideoPipelineStalledFor
    {
        get
        {
            if (Role != SessionRole.Sharer || _lifetime.IsCancellationRequested) return null;
            TimeSpan? stalled = null;
            if (_encoder?.TryGetBusy(out _, out var busy) == true) stalled = busy;
            var pendingSince = Volatile.Read(ref _firstUnencodedCaptureTimestamp);
            var connectedAt = Volatile.Read(ref _mediaConnectedTimestamp);
            if (pendingSince != 0 && connectedAt != 0)
            {
                var waiting = Stopwatch.GetElapsedTime(Math.Max(pendingSince, connectedAt));
                if (stalled is null || waiting > stalled) stalled = waiting;
            }
            return stalled;
        }
    }

    /// <summary>Why queued frames are not being encoded, in the order the encode loop checks.</summary>
    internal string DescribeEncodeGate()
    {
        if (_lifetime.IsCancellationRequested) return "session closing";
        var protector = Volatile.Read(ref _trafficProtector);
        if (protector is null || !protector.IsEstablished) return "secure session (protector) not established";
        if (State != MediaConnectionState.Connected) return $"media state {State}";
        if (_encoder?.TryGetBusy(out var operation, out var busy) == true)
            return FormattableString.Invariant($"encoder busy in {operation} for {busy.TotalSeconds:0.0} s");
        if (_encoder?.AbandonedEncoders is > 0 and var abandoned)
            return $"encoder restarted after {abandoned} stall(s)";
        if (_encodeLoop is { IsCompleted: true }) return "encode loop stopped";
        return Encoder.IsHardware ? "hardware encoder produced no output" : "encode loop has not taken the frame";
    }

    private void NoteFrameCaptured()
    {
        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastCaptureTimestamp, now);
        Interlocked.CompareExchange(ref _firstUnencodedCaptureTimestamp, now, 0);
    }

    /// <summary>An encode attempt finished (a native drop or an empty result still counts).</summary>
    private void NoteEncodeAttempted() => Volatile.Write(ref _firstUnencodedCaptureTimestamp, 0);

    private void NoteFrameEncoded(VideoPayloadCodec codec, int width, int height, int bytes)
    {
        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastEncodedTimestamp, now);
        if (Interlocked.Exchange(ref _firstFrameEncoded, 1) == 0)
            LogFirstFrameEncoded(_log, SessionId, codec, width, height, bytes,
                AgeSeconds(Volatile.Read(ref _mediaConnectedTimestamp), now) * 1000);
    }

    [LoggerMessage(1448, LogLevel.Information,
        "Media {SessionId} first encoded frame: {Codec} {Width}x{Height}, {Bytes} bytes, {Milliseconds:0} ms after media connected")]
    private static partial void LogFirstFrameEncoded(ILogger logger, SessionId sessionId, VideoPayloadCodec codec,
        int width, int height, int bytes, double milliseconds);

    [LoggerMessage(1449, LogLevel.Warning,
        "Media {SessionId} sharer video stalled (encoder): frames queued but none encoded for {Seconds:0.0} s ({Captured} captured so far); gate: {Gate}")]
    private static partial void LogSharerVideoStalled(ILogger logger, SessionId sessionId, double seconds, long captured, string gate);

    [LoggerMessage(1450, LogLevel.Information, "Media {SessionId} sharer encoding resumed after {Seconds:0.0} s")]
    private static partial void LogSharerEncodingResumed(ILogger logger, SessionId sessionId, double seconds);

    internal void CheckVideoStall(long now)
    {
        try
        {
            var bytes = _statistics.Snapshot().BytesReceived;
            var connected = State == MediaConnectionState.Connected
                            && Volatile.Read(ref _trafficProtector) is { IsEstablished: true };
            if (!connected || bytes != Volatile.Read(ref _watchdogBytesReceived))
            {
                // Progress (or nothing to expect yet) restarts the clock.
                Volatile.Write(ref _watchdogBytesReceived, bytes);
                Volatile.Write(ref _lastVideoProgressTimestamp, now);
                var stalledSince = Interlocked.Exchange(ref _stallStartedTimestamp, 0);
                if (stalledSince != 0 && connected)
                    LogVideoResumed(_log, SessionId, Stopwatch.GetElapsedTime(stalledSince, now).TotalSeconds, _stallRequests);
                _stallRequests = 0;
                _nextStallRequestTimestamp = 0;
                return;
            }

            var silent = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastVideoProgressTimestamp), now);
            if (silent < VideoStallAfter) return;
            if (Interlocked.CompareExchange(ref _stallStartedTimestamp, Volatile.Read(ref _lastVideoProgressTimestamp), 0) == 0)
                LogVideoStalled(_log, SessionId, silent.TotalSeconds, _statistics.Snapshot().FramesRendered);
            if (now < _nextStallRequestTimestamp) return;

            _stallRequests++;
            RequestPictureLossIndication(explicitRequest: true);
            var backoff = TimeSpan.FromSeconds(Math.Min(
                MaximumStallRequestInterval.TotalSeconds,
                VideoStallAfter.TotalSeconds * Math.Pow(2, Math.Min(_stallRequests - 1, 4))));
            _nextStallRequestTimestamp = now + (long)(backoff.TotalSeconds * Stopwatch.Frequency);
            LogVideoStallRequest(_log, SessionId, _stallRequests, silent.TotalSeconds);
        }
        catch (Exception ex)
        {
            LogVideoAssemblyError(_log, ex);
        }
    }

    /// <summary>Sharer: the viewer asked for a picture (RTCP PLI).</summary>
    private void OnViewerPictureRequest()
    {
        var requestedAt = Stopwatch.GetTimestamp();
        var lastCapture = Volatile.Read(ref _lastCaptureTimestamp);
        RequestEncoderKeyFrame();
        if (State != MediaConnectionState.Connected || lastCapture == 0
            || Stopwatch.GetElapsedTime(lastCapture, requestedAt) < CaptureIdleBeforeRestart
            || _capture is not IAdaptiveCaptureSource adaptive
            || Interlocked.Exchange(ref _captureRestartPending, 1) == 1)
            return;

        LogSharerPictureRequestWhileIdle(_log, SessionId,
            Stopwatch.GetElapsedTime(lastCapture, requestedAt).TotalSeconds,
            AgeSeconds(Volatile.Read(ref _lastEncodedTimestamp), requestedAt));
        _ = Task.Run(async () =>
        {
            try
            {
                // A live capture re-emits its canvas for the key-frame request within one frame.
                await Task.Delay(CaptureRefreshGrace, _lifetime.Token);
                if (Volatile.Read(ref _lastCaptureTimestamp) > requestedAt
                    || _capture.Target is not { } target
                    || Stopwatch.GetElapsedTime(Volatile.Read(ref _lastCaptureRestartTimestamp)) < MinimumCaptureRestartInterval)
                    return;

                Volatile.Write(ref _lastCaptureRestartTimestamp, Stopwatch.GetTimestamp());
                LogCaptureRestartForStall(_log, SessionId, target.DisplayName);
                await adaptive.SwitchTargetAsync(target, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                // Session ending.
            }
            catch (Exception ex)
            {
                LogCaptureRestartFailed(_log, SessionId, ex);
            }
            finally
            {
                Volatile.Write(ref _captureRestartPending, 0);
            }
        });
    }

    private static double AgeSeconds(long timestamp, long now) =>
        timestamp == 0 ? -1 : Stopwatch.GetElapsedTime(timestamp, now).TotalSeconds;

    [LoggerMessage(1411, LogLevel.Warning, "Media {SessionId} video stalled: no frame decoded for {Seconds:0.0} s ({Rendered} rendered so far)")]
    private static partial void LogVideoStalled(ILogger logger, SessionId sessionId, double seconds, long rendered);

    [LoggerMessage(1412, LogLevel.Information, "Media {SessionId} requested a picture from the sharer (attempt {Attempt}, {Seconds:0.0} s without video)")]
    private static partial void LogVideoStallRequest(ILogger logger, SessionId sessionId, int attempt, double seconds);

    [LoggerMessage(1413, LogLevel.Information, "Media {SessionId} video resumed after {Seconds:0.0} s and {Requests} picture requests")]
    private static partial void LogVideoResumed(ILogger logger, SessionId sessionId, double seconds, int requests);

    [LoggerMessage(1414, LogLevel.Warning, "Media {SessionId} viewer asked for a picture; last frame captured {CaptureAgeSeconds:0.0} s and encoded {EncodeAgeSeconds:0.0} s ago")]
    private static partial void LogSharerPictureRequestWhileIdle(ILogger logger, SessionId sessionId, double captureAgeSeconds, double encodeAgeSeconds);

    [LoggerMessage(1415, LogLevel.Warning, "Media {SessionId} capture produced nothing for a picture request; restarting capture of {Display}")]
    private static partial void LogCaptureRestartForStall(ILogger logger, SessionId sessionId, string display);

    [LoggerMessage(1416, LogLevel.Warning, "Media {SessionId} capture restart after a stall failed")]
    private static partial void LogCaptureRestartFailed(ILogger logger, SessionId sessionId, Exception exception);
}
