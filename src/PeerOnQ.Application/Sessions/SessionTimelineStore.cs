using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Sessions;

/// <summary>Bounded, address-free history sourced only from real coordinator events.</summary>
internal sealed class SessionTimelineStore(TimeProvider timeProvider)
{
    private const int MaximumSessions = 50;
    private const int MaximumEntriesPerSession = 128;
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, Queue<SessionTimelineEntry>> _entries = [];
    private readonly LinkedList<SessionId> _recency = [];

    public void Record(SessionId sessionId, string code, string description)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var timeline))
            {
                timeline = [];
                _entries[sessionId] = timeline;
            }

            while (timeline.Count >= MaximumEntriesPerSession) timeline.Dequeue();
            timeline.Enqueue(new SessionTimelineEntry(timeProvider.GetUtcNow(), code, description));

            _recency.Remove(sessionId);
            _recency.AddLast(sessionId);
            while (_recency.Count > MaximumSessions)
            {
                var oldest = _recency.First!.Value;
                _recency.RemoveFirst();
                _entries.Remove(oldest);
            }
        }
    }

    public IReadOnlyList<SessionTimelineEntry> Get(SessionId sessionId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(sessionId, out var timeline)
                ? timeline.ToArray()
                : [];
        }
    }
}
