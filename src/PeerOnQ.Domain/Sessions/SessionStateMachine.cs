using PeerOnQ.Domain.Errors;

namespace PeerOnQ.Domain.Sessions;

public sealed record SessionTransition(SessionState From, SessionTrigger Trigger, SessionState To);

/// <summary>
/// Typed session state machine. Every allowed edge is declared in one table; anything not in
/// the table is rejected and surfaced through <see cref="TransitionRejected"/> so the caller
/// can log it. There is deliberately no fallback transition.
/// </summary>
public sealed class SessionStateMachine
{
    private static readonly IReadOnlyDictionary<(SessionState, SessionTrigger), SessionState> Table =
        BuildTable();

    private static readonly SessionState[] TerminalStates = [SessionState.Ended, SessionState.Failed];

    private readonly Lock _gate = new();
    private readonly List<SessionTransition> _history = [];

    public SessionStateMachine(SessionState initial = SessionState.Idle) => State = initial;

    public SessionState State { get; private set; }

    public bool IsTerminal => TerminalStates.Contains(State);

    public IReadOnlyList<SessionTransition> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToArray();
            }
        }
    }

    public event EventHandler<SessionTransition>? Transitioned;
    public event EventHandler<RejectedTransition>? TransitionRejected;

    public bool CanFire(SessionTrigger trigger) => Table.ContainsKey((State, trigger));

    public bool TryFire(SessionTrigger trigger, out SessionState next)
    {
        SessionTransition transition;

        lock (_gate)
        {
            if (!Table.TryGetValue((State, trigger), out var target))
            {
                next = State;
                TransitionRejected?.Invoke(this, new RejectedTransition(State, trigger));
                return false;
            }

            transition = new SessionTransition(State, trigger, target);
            _history.Add(transition);
            State = target;
            next = target;
        }

        Transitioned?.Invoke(this, transition);
        return true;
    }

    public SessionState Fire(SessionTrigger trigger)
    {
        if (!TryFire(trigger, out var next))
        {
            throw new InvalidSessionTransitionException(
                $"Trigger {trigger} is not valid in state {State}.");
        }

        return next;
    }

    private static Dictionary<(SessionState, SessionTrigger), SessionState> BuildTable()
    {
        var table = new Dictionary<(SessionState, SessionTrigger), SessionState>
        {
            // Viewer side: resolve the device, then ask for permission.
            [(SessionState.Idle, SessionTrigger.StartRequest)] = SessionState.ResolvingDevice,
            [(SessionState.ResolvingDevice, SessionTrigger.DeviceResolved)] = SessionState.RequestingPermission,
            [(SessionState.RequestingPermission, SessionTrigger.PermissionRequestSent)] = SessionState.AwaitingPermission,

            // Sharer side enters the flow here, when a request arrives.
            [(SessionState.Idle, SessionTrigger.PermissionRequestSent)] = SessionState.AwaitingPermission,

            // Permission outcome.
            [(SessionState.AwaitingPermission, SessionTrigger.PermissionAccepted)] = SessionState.Negotiating,
            [(SessionState.AwaitingPermission, SessionTrigger.PermissionRefused)] = SessionState.Ending,

            // Media setup.
            [(SessionState.Negotiating, SessionTrigger.NegotiationCompleted)] = SessionState.Connecting,
            [(SessionState.Connecting, SessionTrigger.MediaConnected)] = SessionState.ConnectedViewOnly,

            // Steady state and recovery.
            [(SessionState.ConnectedViewOnly, SessionTrigger.ConnectionLost)] = SessionState.Reconnecting,
            [(SessionState.Reconnecting, SessionTrigger.Reconnected)] = SessionState.ConnectedViewOnly,

            // Shutdown.
            [(SessionState.Ending, SessionTrigger.Closed)] = SessionState.Ended,
        };

        // Either side may end the session from any live state.
        SessionState[] liveStates =
        [
            SessionState.ResolvingDevice, SessionState.RequestingPermission, SessionState.AwaitingPermission,
            SessionState.Negotiating, SessionState.Connecting, SessionState.ConnectedViewOnly,
            SessionState.Reconnecting,
        ];

        foreach (var state in liveStates)
        {
            table[(state, SessionTrigger.EndRequested)] = SessionState.Ending;
        }

        // Any non-terminal state can fault, including Ending while cleaning up.
        foreach (var state in liveStates.Append(SessionState.Idle).Append(SessionState.Ending))
        {
            table[(state, SessionTrigger.Fault)] = SessionState.Failed;
        }

        return table;
    }
}

public readonly record struct RejectedTransition(SessionState State, SessionTrigger Trigger);
