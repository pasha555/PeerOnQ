namespace PeerOnQ.Media.Pipeline;

/// <summary>One-second byte budget used to enforce the active quality profile's bitrate cap.</summary>
public sealed class BitrateLimiter(int targetKbps, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Queue<(DateTimeOffset At, int Bytes)> _sent = new();
    private readonly Lock _gate = new();
    private int _targetKbps = Math.Max(1, targetKbps);

    public int TargetKbps
    {
        get
        {
            lock (_gate) return _targetKbps;
        }
    }

    public void SetTargetKbps(int targetKbps)
    {
        lock (_gate)
        {
            _targetKbps = Math.Clamp(targetKbps, 1, 100_000);
        }
    }

    public bool TryConsume(int bytes)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            while (_sent.Count > 0 && now - _sent.Peek().At >= TimeSpan.FromSeconds(1))
                _sent.Dequeue();

            var budgetBytes = _targetKbps * 1000L / 8;
            if (_sent.Sum(item => (long)item.Bytes) + bytes > budgetBytes) return false;

            _sent.Enqueue((now, bytes));
            return true;
        }
    }
}
