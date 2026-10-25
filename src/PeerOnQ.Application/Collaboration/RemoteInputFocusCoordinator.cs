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

    public bool Acquire(SessionId sessionId, Action ownerLost, Func<bool>? prepareInput = null)
    {
        ArgumentNullException.ThrowIfNull(ownerLost);
        lock (_gate)
        {
            // Release held input before the replacement can restore its scope. Sink operations
            // and ownership changes use the same gate, so an old callback cannot stop the new owner.
            if (_owner != sessionId) _ownerLost?.Invoke();
            _owner = sessionId;
            _ownerLost = ownerLost;
            if (prepareInput?.Invoke() != false) return true;
            _ownerLost.Invoke();
            _owner = null;
            _ownerLost = null;
            return false;
        }
    }

    public bool IsOwner(SessionId sessionId)
    {
        lock (_gate) return _owner == sessionId;
    }

    public bool ExecuteIfOwner(SessionId sessionId, Action action)
    {
        lock (_gate)
        {
            if (_owner != sessionId) return false;
            action();
            return true;
        }
    }

    public void ExecuteIfUnowned(Action action)
    {
        lock (_gate)
        {
            if (_owner is null) action();
        }
    }

    public void Release(SessionId sessionId, Action? releaseInput = null)
    {
        lock (_gate)
        {
            if (_owner != sessionId) return;
            releaseInput?.Invoke();
            _owner = null;
            _ownerLost = null;
        }
    }
}
