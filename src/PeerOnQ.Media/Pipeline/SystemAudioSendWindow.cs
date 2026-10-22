using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Security;

namespace PeerOnQ.Media.Pipeline;

internal enum AudioAdmission { Accepted, Backpressured, Expired, InvalidSequence }
internal enum AudioReceiptResult { Accepted, Stale, Invalid, Expired }

/// <summary>Counts authenticated application delivery, including SCTP's unconfirmed records.</summary>
internal sealed class SystemAudioSendWindow(TimeProvider? timeProvider = null)
{
    internal const int MaximumOutstandingFrames = 12;
    internal const int MaximumOutstandingBytes = MaximumOutstandingFrames
        * (SystemAudioFrameCodec.EncodedBytes + SessionTrafficProtector.RecordOverheadBytes);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Queue<(long Sequence, long SentAt)> _pending = new();
    private long _highestSent;
    private long _lastAcknowledged;
    private double? _receiptRttMs;
    private bool _closed;

    internal int OutstandingFrames { get { lock (_gate) return _pending.Count; } }
    internal double ReceiptDeadlineMs { get { lock (_gate) return DeadlineCore; } }
    private double DeadlineCore => _receiptRttMs is { } rtt ? Math.Clamp(rtt * 3 + 80, 320, 1000) : 500;

    internal AudioAdmission TryAdmit(long sequence)
    {
        lock (_gate)
        {
            if (_closed) return AudioAdmission.Expired;
            if (sequence <= _highestSent || sequence <= 0) return AudioAdmission.InvalidSequence;
            if (IsExpiredCore()) return AudioAdmission.Expired;
            if (_pending.Count >= MaximumOutstandingFrames) return AudioAdmission.Backpressured;
            _pending.Enqueue((sequence, _time.GetTimestamp()));
            _highestSent = sequence;
            return AudioAdmission.Accepted;
        }
    }

    internal AudioReceiptResult Acknowledge(long sequence)
    {
        lock (_gate)
        {
            if (_closed) return AudioReceiptResult.Expired;
            if (sequence <= 0 || sequence > _highestSent) return AudioReceiptResult.Invalid;
            if (sequence <= _lastAcknowledged) return AudioReceiptResult.Stale;
            if (IsExpiredCore()) return AudioReceiptResult.Expired;
            long? sentAt = null;
            foreach (var candidate in _pending)
                if (candidate.Sequence == sequence) { sentAt = candidate.SentAt; break; }
            if (sentAt is null) return AudioReceiptResult.Invalid;
            var sample = _time.GetElapsedTime(sentAt.Value).TotalMilliseconds;
            _receiptRttMs = _receiptRttMs is { } previous ? previous * 0.75 + sample * 0.25 : sample;
            _lastAcknowledged = sequence;
            while (_pending.TryPeek(out var pending) && pending.Sequence <= sequence) _pending.Dequeue();
            return AudioReceiptResult.Accepted;
        }
    }

    internal bool IsExpired { get { lock (_gate) return IsExpiredCore(); } }

    private bool IsExpiredCore() => _pending.TryPeek(out var first)
        && _time.GetElapsedTime(first.SentAt).TotalMilliseconds > DeadlineCore;

    internal void Close()
    {
        lock (_gate) { _closed = true; _pending.Clear(); }
    }
}
