using System.Collections.Concurrent;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Transport.Protocol;

namespace PeerOnQ.Transport;

/// <summary>
/// The server answers a refused session request only with an error that echoes the request's
/// message id. 0.9.86 did not map it back, so a request to the device itself (target_is_self)
/// waited the full 30 s permission timeout. Errors now carry the session they refer to.
/// </summary>
public sealed partial class WebSocketSignalingClient
{
    private const int MaximumTrackedSessionRequests = 64;

    private readonly ConcurrentDictionary<string, SessionId> _sessionRequests = new(StringComparer.Ordinal);

    /// <summary>
    /// Highest refresh rate of this device's displays (Hz), sent with every session request so the
    /// sharer picks a frame rate the viewer can show. Null keeps the field out (older behavior).
    /// </summary>
    public int? PreferredMaxFps
    {
        get => Volatile.Read(ref _preferredMaxFps) is > 0 and var fps ? fps : null;
        set => Volatile.Write(ref _preferredMaxFps, value is >= 1 and <= 240 ? value.Value : 0);
    }

    private int _preferredMaxFps;

    private Task SendSessionRequestAsync(SessionRequestMessage message, CancellationToken cancellationToken)
    {
        message = message with { MaxFps = PreferredMaxFps };
        if (SessionId.TryParse(message.SessionId, out var sessionId))
        {
            if (_sessionRequests.Count >= MaximumTrackedSessionRequests) _sessionRequests.Clear();
            _sessionRequests[message.MessageId] = sessionId;
        }

        return SendAsync(message, cancellationToken);
    }

    /// <summary>The session of the request or relayed message that an error answers, if known.</summary>
    private SessionId? ResolveErroredSession(ErrorMessage error)
    {
        if (error.InReplyTo is not { } replyTo) return null;
        if (_sessionRequests.TryRemove(replyTo, out var requested)) return requested;
        return _relayedMessages.TryGetValue(replyTo, out var relayed) ? relayed : null;
    }

    /// <summary>A request that the peer answered can no longer be refused by signaling.</summary>
    private void ForgetSessionRequest(SessionId sessionId)
    {
        foreach (var pair in _sessionRequests)
        {
            if (pair.Value == sessionId) _sessionRequests.TryRemove(pair.Key, out _);
        }
    }
}
