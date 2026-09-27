using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// Real counters for the viewer's statistics panel: measured frame rate and bitrate over a
/// sliding window, never a nominal or invented value.
/// </summary>
public sealed class MediaStatisticsCollector
{
    // Feedback messages are periodic even when capture is idle. Age the measurements,
    // not just the messages, so one old stall cannot keep degrading a static desktop.
    private static readonly TimeSpan LatencyRetention = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _created;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _recentCapture = new();
    private readonly Queue<DateTimeOffset> _recentEncode = new();
    private readonly Queue<DateTimeOffset> _recentDecode = new();
    private readonly Queue<DateTimeOffset> _recentRender = new();
    private readonly Queue<(DateTimeOffset At, int Bytes)> _recentBytes = new();
    private readonly Queue<(DateTimeOffset At, double Value)> _captureToEncodeLatency = new();
    private readonly Queue<(DateTimeOffset At, double Value)> _decodeToRenderLatency = new();
    private readonly Queue<(DateTimeOffset At, double Value)> _captureToPresentLatency = new();
    private readonly Queue<(DateTimeOffset At, double Value)> _inputToInjectionLatency = new();

    private long _framesCaptured;
    private long _framesEncoded;
    private long _framesDropped;
    private long _framesRendered;
    private long _bytesSent;
    private long _bytesReceived;
    private int _width;
    private int _height;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _requestedWidth;
    private int _requestedHeight;
    private int _encodedWidth;
    private int _encodedHeight;
    private int _decodedWidth;
    private int _decodedHeight;
    private int _renderedWidth;
    private int _renderedHeight;
    private double _rttMs;
    private double _packetLossPercent;
    private double _jitterMs;
    private double _availableOutgoingBitrateKbps;
    private double _frameAgeClockUncertaintyMs;
    private double _inputClockUncertaintyMs;
    private ConnectionPath _connectionPath = ConnectionPath.UnknownNegotiating;
    private string? _relayServerId;
    private string? _relayRegion;

    public MediaStatisticsCollector(TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        _window = window ?? TimeSpan.FromSeconds(2);
        _time = timeProvider ?? TimeProvider.System;
        _created = _time.GetUtcNow();
    }

    public void FrameCaptured()
    {
        lock (_gate)
        {
            _framesCaptured++;
            Track(_recentCapture);
        }
    }

    public void FrameDropped()
    {
        lock (_gate)
        {
            _framesDropped++;
        }
    }

    public void FrameEncoded(
        int bytes,
        int width,
        int height,
        double captureToEncodeLatencyMs = 0,
        int sourceWidth = 0,
        int sourceHeight = 0,
        int requestedWidth = 0,
        int requestedHeight = 0)
    {
        lock (_gate)
        {
            _framesEncoded++;
            _bytesSent += bytes;
            _width = width;
            _height = height;
            _sourceWidth = sourceWidth > 0 ? sourceWidth : width;
            _sourceHeight = sourceHeight > 0 ? sourceHeight : height;
            _requestedWidth = requestedWidth > 0 ? requestedWidth : width;
            _requestedHeight = requestedHeight > 0 ? requestedHeight : height;
            _encodedWidth = width;
            _encodedHeight = height;
            Track(_recentEncode);
            TrackBytes(bytes);
            TrackLatency(_captureToEncodeLatency, captureToEncodeLatencyMs);
        }
    }

    public void FrameDecoded(int bytes, int width, int height)
    {
        lock (_gate)
        {
            _bytesReceived += bytes;
            _width = width;
            _height = height;
            _decodedWidth = width;
            _decodedHeight = height;
            Track(_recentDecode);
            TrackBytes(bytes);
        }
    }

    public void FrameRendered(
        double decodeToRenderLatencyMs = 0,
        int width = 0,
        int height = 0,
        double captureToPresentLatencyMs = 0,
        double clockUncertaintyMs = 0)
    {
        lock (_gate)
        {
            _framesRendered++;
            _renderedWidth = width > 0 ? width : _decodedWidth;
            _renderedHeight = height > 0 ? height : _decodedHeight;
            Track(_recentRender);
            TrackLatency(_decodeToRenderLatency, decodeToRenderLatencyMs);
            if (double.IsFinite(captureToPresentLatencyMs)
                && captureToPresentLatencyMs is > 0 and <= 60_000
                && double.IsFinite(clockUncertaintyMs)
                && clockUncertaintyMs is >= 0 and <= 60_000)
            {
                TrackLatency(_captureToPresentLatency, captureToPresentLatencyMs);
                _frameAgeClockUncertaintyMs = clockUncertaintyMs;
            }
        }
    }

    public void UpdateNetwork(
        ConnectionPath path,
        string? relayServerId,
        string? relayRegion,
        double rttMs,
        double packetLossPercent,
        double jitterMs,
        double availableOutgoingBitrateKbps)
    {
        lock (_gate)
        {
            _connectionPath = path;
            _relayServerId = path == ConnectionPath.Relayed ? relayServerId : null;
            _relayRegion = path == ConnectionPath.Relayed ? relayRegion : null;
            _rttMs = Math.Max(0, rttMs);
            _packetLossPercent = Math.Clamp(packetLossPercent, 0, 100);
            _jitterMs = Math.Max(0, jitterMs);
            _availableOutgoingBitrateKbps = Math.Max(0, availableOutgoingBitrateKbps);
        }
    }

    public void InputInjected(double latencyMs, double clockUncertaintyMs)
    {
        lock (_gate)
        {
            if (!double.IsFinite(latencyMs)
                || latencyMs is <= 0 or > 60_000
                || !double.IsFinite(clockUncertaintyMs)
                || clockUncertaintyMs is < 0 or > 60_000)
            {
                return;
            }

            TrackLatency(_inputToInjectionLatency, latencyMs);
            _inputClockUncertaintyMs = clockUncertaintyMs;
        }
    }

    public MediaStatistics Snapshot()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            Trim(now);
            ExpireLatencySamples(now);

            var seconds = _window.TotalSeconds;
            var encodeFps = Rate(_recentEncode, seconds);
            var decodeFps = Rate(_recentDecode, seconds);
            var renderFps = Rate(_recentRender, seconds);
            var captureFps = Rate(_recentCapture, seconds);
            var bytesInWindow = _recentBytes.Sum(entry => (long)entry.Bytes);

            return new MediaStatistics
            {
                FramesCaptured = _framesCaptured,
                FramesEncoded = _framesEncoded,
                FramesDropped = _framesDropped,
                FramesRendered = _framesRendered,
                BytesSent = _bytesSent,
                BytesReceived = _bytesReceived,
                CurrentFps = encodeFps > 0 ? encodeFps : renderFps > 0 ? renderFps : decodeFps,
                CurrentBitrateKbps = seconds > 0 ? bytesInWindow * 8 / seconds / 1000 : 0,
                CaptureFps = captureFps,
                EncodeFps = encodeFps,
                DecodeFps = decodeFps,
                RenderFps = renderFps,
                RttMs = _rttMs,
                PacketLossPercent = _packetLossPercent,
                JitterMs = _jitterMs,
                AvailableOutgoingBitrateKbps = _availableOutgoingBitrateKbps,
                CaptureToEncodeLatencyMs = Percentile(_captureToEncodeLatency, 0.95),
                DecodeToRenderLatencyMs = Percentile(_decodeToRenderLatency, 0.95),
                CaptureToEncodeLatencyP50Ms = Percentile(_captureToEncodeLatency, 0.50),
                CaptureToEncodeLatencyP95Ms = Percentile(_captureToEncodeLatency, 0.95),
                CaptureToEncodeLatencyP99Ms = Percentile(_captureToEncodeLatency, 0.99),
                DecodeToRenderLatencyP50Ms = Percentile(_decodeToRenderLatency, 0.50),
                DecodeToRenderLatencyP95Ms = Percentile(_decodeToRenderLatency, 0.95),
                DecodeToRenderLatencyP99Ms = Percentile(_decodeToRenderLatency, 0.99),
                CaptureToPresentLatencyP50Ms = Percentile(_captureToPresentLatency, 0.50),
                CaptureToPresentLatencyP95Ms = Percentile(_captureToPresentLatency, 0.95),
                CaptureToPresentLatencyP99Ms = Percentile(_captureToPresentLatency, 0.99),
                FrameAgeClockUncertaintyMs = _frameAgeClockUncertaintyMs,
                InputToInjectionLatencyP50Ms = Percentile(_inputToInjectionLatency, 0.50),
                InputToInjectionLatencyP95Ms = Percentile(_inputToInjectionLatency, 0.95),
                InputToInjectionLatencyP99Ms = Percentile(_inputToInjectionLatency, 0.99),
                InputClockUncertaintyMs = _inputClockUncertaintyMs,
                ConnectionPath = _connectionPath,
                RelayServerId = _relayServerId,
                RelayRegion = _relayRegion,
                Width = _width,
                Height = _height,
                SourceWidth = _sourceWidth,
                SourceHeight = _sourceHeight,
                RequestedWidth = _requestedWidth,
                RequestedHeight = _requestedHeight,
                EncodedWidth = _encodedWidth,
                EncodedHeight = _encodedHeight,
                DecodedWidth = _decodedWidth,
                DecodedHeight = _decodedHeight,
                RenderedWidth = _renderedWidth,
                RenderedHeight = _renderedHeight,
                Elapsed = now - _created,
            };
        }
    }

    private void Track(Queue<DateTimeOffset> queue)
    {
        var now = _time.GetUtcNow();
        queue.Enqueue(now);
        Trim(now);
    }

    private void TrackBytes(int bytes)
    {
        var now = _time.GetUtcNow();
        _recentBytes.Enqueue((now, bytes));
        Trim(now);
    }

    private void Trim(DateTimeOffset now)
    {
        Trim(_recentCapture, now);
        Trim(_recentEncode, now);
        Trim(_recentDecode, now);
        Trim(_recentRender, now);

        while (_recentBytes.Count > 0 && now - _recentBytes.Peek().At > _window)
            _recentBytes.Dequeue();
    }

    private void ExpireLatencySamples(DateTimeOffset now)
    {
        TrimLatency(_captureToEncodeLatency, now);
        TrimLatency(_decodeToRenderLatency, now);
        TrimLatency(_captureToPresentLatency, now);
        TrimLatency(_inputToInjectionLatency, now);
        if (_captureToPresentLatency.Count == 0) _frameAgeClockUncertaintyMs = 0;
        if (_inputToInjectionLatency.Count == 0) _inputClockUncertaintyMs = 0;
    }

    private void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() > _window) queue.Dequeue();
    }

    private static double Rate(Queue<DateTimeOffset> queue, double seconds) =>
        seconds > 0 ? queue.Count / seconds : 0;

    private void TrackLatency(Queue<(DateTimeOffset At, double Value)> samples, double value)
    {
        // Zero is the compatibility sentinel for callers without a correlated frame timestamp;
        // it is not a real latency sample and must not pull p50/p95 down.
        if (!double.IsFinite(value) || value <= 0 || value > 60_000) return;
        var now = _time.GetUtcNow();
        TrimLatency(samples, now);
        while (samples.Count >= 120) samples.Dequeue();
        samples.Enqueue((now, value));
    }

    private static void TrimLatency(Queue<(DateTimeOffset At, double Value)> samples, DateTimeOffset now)
    {
        while (samples.Count > 0 && now - samples.Peek().At > LatencyRetention)
            samples.Dequeue();
    }

    private static double Percentile(Queue<(DateTimeOffset At, double Value)> samples, double percentile)
    {
        if (samples.Count == 0) return 0;
        var ordered = samples.Select(sample => sample.Value).Order().ToArray();
        return ordered[(int)Math.Ceiling(ordered.Length * percentile) - 1];
    }
}
