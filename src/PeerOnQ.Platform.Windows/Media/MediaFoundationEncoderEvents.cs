using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace PeerOnQ.Platform.Windows.Media;

/// <summary>One asynchronous MF subscription; callbacks never acquire the encoder lock.</summary>
internal sealed class MediaFoundationEncoderEvents : CallbackBase, IMFAsyncCallback
{
    private readonly Lock _callbackGate = new();
    private readonly Func<IMFAsyncResult, MediaEventTypes> _read;
    private readonly Action<IMFAsyncCallback> _subscribe;
    private readonly Action _release;
    private readonly MediaFoundationEncoderEventQueue _queue = new();
    private bool _stopped, _released;

    internal MediaFoundationEncoderEvents(IMFMediaEventGenerator generator)
    {
        var owned = generator.QueryInterface<IMFMediaEventGenerator>();
        _read = result =>
        {
            using var mediaEvent = owned.EndGetEvent(result);
            mediaEvent.Status.CheckError();
            return mediaEvent.EventType;
        };
        _subscribe = callback => owned.BeginGetEvent(callback, null);
        _release = owned.Dispose;
    }

    // Friend tests exercise native subscribe/read failures without constructing a fake MFT.
    internal MediaFoundationEncoderEvents(Func<IMFAsyncResult, MediaEventTypes> read,
        Action<IMFAsyncCallback> subscribe, Action release)
    {
        _read = read; _subscribe = subscribe; _release = release;
    }

    // The encoder stores a strong reference before starting, including failed/partial Begin.
    internal void Start() => _subscribe(this);

    public Result GetParameters(out AsyncCallbackFlags flags, out int queue)
    {
        flags = 0; queue = 0;
        return new Result(unchecked((int)0x80004001)); // Default MF work queue.
    }

    public Result Invoke(IMFAsyncResult result)
    {
        // Stop acquires this fence before native shutdown. A late callback does not touch
        // the released event generator; SharpGen retains its native callback COM reference.
        lock (_callbackGate)
        {
            if (_stopped) return Result.Ok;
            try
            {
                var type = _read(result); // EndGetEvent balances the one outstanding request.
                if (type == MediaEventTypes.Error)
                    throw new InvalidOperationException("The hardware MFT reported a device error.");
                if (_queue.Publish(type)) _subscribe(this);
            }
            catch (Exception error) { _queue.Fail(error); }
        }
        return Result.Ok; // No managed exception crosses the COM callback boundary.
    }

    internal MediaEventTypes Next(TimeSpan timeout) => _queue.Next(timeout);

    internal void Stop()
    {
        lock (_callbackGate) _stopped = true;
        _queue.Stop();
    }

    protected override void DisposeCore(bool disposing)
    {
        try
        {
            if (!disposing) return;
            Stop();
            lock (_callbackGate)
            {
                if (_released) return;
                _released = true;
                _release();
            }
        }
        finally { base.DisposeCore(disposing); }
    }
}

/// <summary>Only bounded event metadata is retained; all deadlines use monotonic elapsed time.</summary>
internal sealed class MediaFoundationEncoderEventQueue
{
    internal const int Capacity = 8;
    private readonly object _gate = new();
    private readonly Queue<MediaEventTypes> _events = new(Capacity);
    private Exception? _error;
    private bool _stopped;

    internal bool Publish(MediaEventTypes type)
    {
        lock (_gate)
        {
            if (_stopped || _error is not null) return false;
            if (_events.Count == Capacity)
                _error = new InvalidOperationException("The hardware MFT event queue exceeded its bound.");
            else _events.Enqueue(type);
            Monitor.PulseAll(_gate);
            return _error is null;
        }
    }

    internal void Fail(Exception error)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _error ??= error;
            Monitor.PulseAll(_gate);
        }
    }

    internal MediaEventTypes Next(TimeSpan timeout)
    {
        var began = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while (_events.Count == 0 && _error is null && !_stopped)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(began);
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException("The hardware H.264 encoder stopped responding.");
                Monitor.Wait(_gate, TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remaining.TotalMilliseconds))));
            }
            if (_stopped) throw new ObjectDisposedException(nameof(MediaFoundationEncoderEventQueue));
            if (_error is not null) throw new InvalidOperationException("The hardware MFT event request failed.", _error);
            return _events.Dequeue();
        }
    }

    internal void Stop()
    {
        lock (_gate)
        {
            _stopped = true; _events.Clear();
            Monitor.PulseAll(_gate);
        }
    }
}
