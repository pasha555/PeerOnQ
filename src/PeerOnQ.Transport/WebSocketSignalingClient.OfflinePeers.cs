using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Transport.Protocol;

namespace PeerOnQ.Transport;

/// <summary>
/// A peer that went offline answers every relayed SDP/ICE message with target_offline. During an
/// ICE-restart loop that was about 90 errors in 17 s, several per millisecond. Once the server says
/// a session's peer is offline, its relayed messages are held (dropped and counted) for a backoff
/// window that doubles up to 30 s, the error is logged once per window, and a resume clears it.
/// Held SDP/ICE is stale anyway: a resumed session renegotiates with a fresh ICE restart.
/// </summary>
public sealed partial class WebSocketSignalingClient
{
    private const int MaximumTrackedRelayedMessages = 512;
    private static readonly TimeSpan InitialOfflineHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumOfflineHold = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SessionId> _relayedMessages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<SessionId, OfflinePeer> _offlinePeers = new();

    private sealed class OfflinePeer
    {
        public long HoldUntil;
        public TimeSpan Hold;
        public int Held;
    }

    private Task SendRelayedAsync(SessionId sessionId, SignalingMessage message, CancellationToken cancellationToken)
    {
        if (_offlinePeers.TryGetValue(sessionId, out var offline))
        {
            if (Stopwatch.GetTimestamp() < Volatile.Read(ref offline.HoldUntil))
            {
                Interlocked.Increment(ref offline.Held);
                return Task.CompletedTask;
            }

            var held = Interlocked.Exchange(ref offline.Held, 0);
            if (held > 0)
                _log.LogInformation("Held {Count} relayed messages for session {SessionId} while its peer was offline", held, sessionId);
        }

        if (_relayedMessages.Count >= MaximumTrackedRelayedMessages) _relayedMessages.Clear();
        _relayedMessages[message.MessageId] = sessionId;
        return SendAsync(message, cancellationToken);
    }

    /// <summary>True when the error was a repeat target_offline for a held session (not logged again).</summary>
    private bool NoteTargetOffline(ErrorMessage error)
    {
        if (error.Code != SignalingErrorCodes.TargetOffline
            || error.InReplyTo is not { } replyTo
            || !_relayedMessages.TryRemove(replyTo, out var sessionId))
            return false;

        var now = Stopwatch.GetTimestamp();
        var offline = _offlinePeers.GetOrAdd(sessionId, _ => new OfflinePeer());
        lock (offline)
        {
            if (now < offline.HoldUntil) return true;
            offline.Hold = offline.Hold == TimeSpan.Zero
                ? InitialOfflineHold
                : TimeSpan.FromTicks(Math.Min(offline.Hold.Ticks * 2, MaximumOfflineHold.Ticks));
            Volatile.Write(ref offline.HoldUntil, now + (long)(offline.Hold.TotalSeconds * Stopwatch.Frequency));
        }

        _log.LogWarning(
            "Peer of session {SessionId} is offline; holding its relayed messages for {Seconds:0} s",
            sessionId,
            offline.Hold.TotalSeconds);
        return true;
    }

    private void ClearPeerOffline(SessionId sessionId)
    {
        if (_offlinePeers.TryRemove(sessionId, out var offline) && offline.Held > 0)
            _log.LogInformation("Session {SessionId} resumed after {Count} relayed messages were held", sessionId, offline.Held);
    }
}
