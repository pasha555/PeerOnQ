using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Media.Codecs;

namespace PeerOnQ.Media.Pipeline;

internal sealed record DecodedVideoPresentation(
    int Width, int Height, byte[] Planes, long DecodedTimestamp,
    long CaptureTimestampUnixMicroseconds, long SequenceNumber, PeerClockEstimate? PeerClockEstimate,
    DecodedNv12Frame? Nv12 = null);

/// <summary>
/// Converts at most one decoded image while retaining one newest pending image. Native reference
/// decoding and authentication happen before admission; replacing a decoded image never loses a reference frame.
/// </summary>
internal sealed class LatestDecodedVideoWorker : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Lock _presentationFence = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<DecodedVideoPresentation, byte[]> _convert;
    private readonly Action<DecodedVideoPresentation, byte[]> _present;
    private readonly Action<Exception>? _onError;
    private DecodedVideoPresentation? _pending;
    private long _generation;
    private long _dropped;
    private int _width, _height;
    private bool _closed;
    private int _disposed;

    public LatestDecodedVideoWorker(Func<DecodedVideoPresentation, byte[]> convert,
        Action<DecodedVideoPresentation, byte[]> present, Action<Exception>? onError = null)
    {
        _convert = convert;
        _present = present;
        _onError = onError;
        Completion = Task.Run(RunAsync);
    }

    public Task Completion { get; }
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public int PendingFrames { get { lock (_gate) return _pending is null ? 0 : 1; } }

    public void Enqueue(DecodedVideoPresentation frame)
    {
        lock (_gate)
        {
            if (_closed) { CryptographicOperations.ZeroMemory(frame.Planes); return; }
            if (_width != frame.Width || _height != frame.Height)
            {
                _generation++;
                _width = frame.Width;
                _height = frame.Height;
            }
            if (_pending is not null)
            {
                CryptographicOperations.ZeroMemory(_pending.Planes);
                Interlocked.Increment(ref _dropped);
            }
            _pending = frame;
        }
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { /* Closing won the race after admission. */ }
    }

    public void Clear()
    {
        lock (_presentationFence)
        {
            lock (_gate)
            {
                _generation++;
                if (_pending is not null) CryptographicOperations.ZeroMemory(_pending.Planes);
                _pending = null;
            }
        }
    }

    public void Close()
    {
        lock (_presentationFence)
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                _generation++;
                if (_pending is not null) CryptographicOperations.ZeroMemory(_pending.Planes);
                _pending = null;
            }
        }
        _lifetime.Cancel();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_lifetime.Token);
                DecodedVideoPresentation? frame;
                long generation;
                lock (_gate)
                {
                    if (_closed) return;
                    frame = _pending;
                    _pending = null;
                    generation = _generation;
                }
                if (frame is null) continue;
                byte[]? converted = null;
                try
                {
                    converted = _convert(frame);
                    lock (_presentationFence)
                    {
                        bool current;
                        lock (_gate) current = !_closed && generation == _generation;
                        if (current)
                        {
                            // Clear/close use fence -> gate order too. External callbacks never
                            // hold the admission gate, so receiving the newest pixels stays cheap.
                            _present(frame, converted);
                            converted = null; // The presentation consumer now owns this BGR buffer.
                        }
                    }
                }
                catch (Exception error) { _onError?.Invoke(error); }
                finally
                {
                    CryptographicOperations.ZeroMemory(frame.Planes);
                    if (converted is not null) CryptographicOperations.ZeroMemory(converted);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Close();
        await Completion;
        _lifetime.Dispose();
        _wake.Dispose();
    }
}
