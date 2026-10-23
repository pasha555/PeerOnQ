namespace PeerOnQ.Media.Pipeline;

/// <summary>One bounded, coherent receiver report from the currently connected media path.</summary>
internal sealed class RtcpNetworkFeedback(TimeProvider? timeProvider = null)
{
    // Receiver reports can be several seconds apart. Allow two ordinary five-second intervals,
    // but never reuse one old loss/jitter observation indefinitely after reports stop arriving.
    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The path minimum is kept per bucket (LEDBAT-style base-delay history), so a lasting
    /// route change without an ICE restart is learned within the window while a short queue
    /// never raises the baseline.
    /// </summary>
    internal static readonly TimeSpan BaselineBucketDuration = TimeSpan.FromSeconds(30);
    internal const int BaselineBuckets = 10;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly double[] _bucketMinimumRttMs = new double[BaselineBuckets];
    private int _bucketIndex;
    private long _bucketStartedAt;
    private Reception? _latest;
    private double _baselineRttMs;
    private long _lastRttObservation;
    private bool _connected;

    internal sealed record Reception(double PacketLossFraction, double JitterMs, long ReceivedAt);

    public void SetConnected(bool connected)
    {
        lock (_gate)
        {
            if (_connected == connected) return;
            _connected = connected;
            _latest = null;
            _baselineRttMs = 0;
            Array.Clear(_bucketMinimumRttMs);
        }
    }

    public void Report(double packetLossFraction, double jitterMs)
    {
        lock (_gate)
        {
            if (!_connected) return;
            _latest = new Reception(packetLossFraction, jitterMs, _time.GetTimestamp());
        }
    }

    public Reception? GetFresh()
    {
        lock (_gate)
        {
            return _latest is { } latest && _time.GetElapsedTime(latest.ReceivedAt) < Lifetime
                ? latest
                : null;
        }
    }

    public void ObserveRtt(double rttMs)
    {
        if (!double.IsFinite(rttMs) || rttMs <= 0) return;
        lock (_gate)
        {
            if (!_connected) return;
            var now = _time.GetTimestamp();
            // Increasing queue delay must not raise the transit allowance for the same path.
            // An absent/in-flight check cannot erase or renew a still-fresh minimum.
            if (_baselineRttMs <= 0 || _time.GetElapsedTime(_lastRttObservation, now) >= Lifetime)
            {
                Array.Clear(_bucketMinimumRttMs);
                _bucketIndex = 0;
                _bucketStartedAt = now;
                _bucketMinimumRttMs[0] = rttMs;
            }
            else if (_time.GetElapsedTime(_bucketStartedAt, now) >= BaselineBucketDuration)
            {
                _bucketIndex = (_bucketIndex + 1) % BaselineBuckets;
                _bucketStartedAt = now;
                _bucketMinimumRttMs[_bucketIndex] = rttMs;
            }
            else
            {
                _bucketMinimumRttMs[_bucketIndex] = Math.Min(_bucketMinimumRttMs[_bucketIndex], rttMs);
            }

            var baseline = double.MaxValue;
            foreach (var minimum in _bucketMinimumRttMs)
                if (minimum > 0 && minimum < baseline) baseline = minimum;
            _baselineRttMs = baseline;
            _lastRttObservation = now;
        }
    }

    public double GetBaselineRttMs()
    {
        lock (_gate)
        {
            return _baselineRttMs > 0 && _time.GetElapsedTime(_lastRttObservation) < Lifetime
                ? _baselineRttMs
                : 0;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _latest = null;
            _baselineRttMs = 0;
            Array.Clear(_bucketMinimumRttMs);
        }
    }
}
