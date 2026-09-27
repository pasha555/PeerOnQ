using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Collaboration;

/// <summary>
/// Process-local, session-bound input focus. At most one hosted session may inject input through
/// the shared platform sink; switching ownership invalidates the previous session synchronously.
/// </summary>
public sealed class RemoteInputFocusCoordinator
{
    private readonly Lock _gate = new();
    private SessionId? _owner;
    private Action? _ownerLost;

    public void Acquire(SessionId sessionId, Action ownerLost)
    {
        ArgumentNullException.ThrowIfNull(ownerLost);
        Action? previousLost = null;

        lock (_gate)
        {
            if (_owner == sessionId)
            {
                _ownerLost = ownerLost;
                return;
            }

            previousLost = _ownerLost;
            _owner = sessionId;
            _ownerLost = ownerLost;
        }

        previousLost?.Invoke();
    }

    public bool IsOwner(SessionId sessionId)
    {
        lock (_gate) return _owner == sessionId;
    }

    public void Release(SessionId sessionId)
    {
        lock (_gate)
        {
            if (_owner != sessionId) return;
            _owner = null;
            _ownerLost = null;
        }
    }
}
