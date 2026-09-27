using PeerOnQ.Application.Abstractions;
using System.Diagnostics.CodeAnalysis;

namespace PeerOnQ.Media.Pipeline;

/// <summary>
/// Bounded hand-off between the capture thread and the encoder.
/// Screen capture must never be blocked by a slow encoder and the queue must never grow
/// without limit, so the newest frame wins and older ones are counted as dropped.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "This public type deliberately implements a bounded latest-frame queue and is already part of the media API.")]
public sealed class VideoFrameQueue(int capacity = 3)
{
    private readonly Lock _gate = new();
    private readonly Queue<CapturedFrame> _frames = new();

    public int Capacity { get; } = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

    public long Enqueued { get; private set; }
    public long Dropped { get; private set; }
    public long Dequeued { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>Returns false when an older frame had to be discarded to make room.</summary>
    public bool TryEnqueue(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            Enqueued++;

            var dropped = false;
            while (_frames.Count >= Capacity)
            {
                _frames.Dequeue();
                Dropped++;
                dropped = true;
            }

            _frames.Enqueue(frame);
            return !dropped;
        }
    }

    public bool TryDequeue(out CapturedFrame? frame)
    {
        lock (_gate)
        {
            if (_frames.Count == 0)
            {
                frame = null;
                return false;
            }

            frame = _frames.Dequeue();
            Dequeued++;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
        }
    }
}

/// <summary>
/// Enforces the configured frame rate on the capture side. Windows.Graphics.Capture can
/// deliver faster than the target, and encoding every frame wastes CPU and bandwidth.
/// </summary>
public sealed class FrameRateLimiter(int targetFps, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private TimeSpan _minimumInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(targetFps, 1, 120));
    private DateTimeOffset _lastAccepted = DateTimeOffset.MinValue;
    private int _targetFps = Math.Clamp(targetFps, 1, 120);

    public int TargetFps
    {
        get
        {
            lock (_gate)
            {
                return _targetFps;
            }
        }
    }

    public long Accepted { get; private set; }
    public long Skipped { get; private set; }

    /// <summary>Changes the target rate mid-session; used by the adaptive controller.</summary>
    public void SetTargetFps(int fps)
    {
        var clamped = Math.Clamp(fps, 1, 120);

        lock (_gate)
        {
            if (_targetFps == clamped) return;

            _targetFps = clamped;
            _minimumInterval = TimeSpan.FromSeconds(1.0 / clamped);
        }
    }

    public bool ShouldAccept()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _lastAccepted < _minimumInterval)
            {
                Skipped++;
                return false;
            }

            _lastAccepted = now;
            Accepted++;
            return true;
        }
    }
}
