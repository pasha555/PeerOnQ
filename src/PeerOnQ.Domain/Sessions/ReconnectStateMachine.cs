using PeerOnQ.Domain.Errors;

namespace PeerOnQ.Domain.Sessions;

/// <summary>
/// Recovery state machine kept separate from the permission/session lifecycle. A reconnect can
/// restore only the already-approved session; it cannot alter the session mode or permissions.
/// </summary>
public sealed class ReconnectStateMachine
{
    private static readonly IReadOnlyDictionary<(ReconnectState, ReconnectTrigger), ReconnectState> Table =
        BuildTable();

    private readonly Lock _gate = new();
    private readonly List<ReconnectTransition> _history = [];

    public ReconnectState State { get; private set; } = ReconnectState.Connected;

    public IReadOnlyList<ReconnectTransition> History
    {
        get
        {
            lock (_gate)
            {
                return _history.ToArray();
            }
        }
    }

    public event EventHandler<ReconnectTransition>? Transitioned;

    public bool CanFire(ReconnectTrigger trigger) => Table.ContainsKey((State, trigger));

    public bool TryFire(ReconnectTrigger trigger, out ReconnectState next)
    {
        ReconnectTransition transition;

        lock (_gate)
        {
            if (!Table.TryGetValue((State, trigger), out next))
            {
                next = State;
                return false;
            }

            transition = new ReconnectTransition(State, trigger, next);
            State = next;
            _history.Add(transition);
        }

        Transitioned?.Invoke(this, transition);
        return true;
    }

    public ReconnectState Fire(ReconnectTrigger trigger)
    {
        if (!TryFire(trigger, out var next))
        {
            throw new InvalidSessionTransitionException(
                $"Reconnect trigger {trigger} is not valid in state {State}.");
        }

        return next;
    }

    private static Dictionary<(ReconnectState, ReconnectTrigger), ReconnectState> BuildTable()
    {
        var table = new Dictionary<(ReconnectState, ReconnectTrigger), ReconnectState>
        {
            [(ReconnectState.Connected, ReconnectTrigger.ConnectionLost)] = ReconnectState.ConnectionInterrupted,
            [(ReconnectState.ConnectionInterrupted, ReconnectTrigger.RetryStarted)] = ReconnectState.Reconnecting,
            [(ReconnectState.Reconnecting, ReconnectTrigger.ReauthenticationRequired)] = ReconnectState.Reauthenticating,
            [(ReconnectState.Reauthenticating, ReconnectTrigger.Reauthenticated)] = ReconnectState.Reconnecting,
            [(ReconnectState.Reconnecting, ReconnectTrigger.RenegotiationStarted)] = ReconnectState.Renegotiating,
            [(ReconnectState.Renegotiating, ReconnectTrigger.RetryStarted)] = ReconnectState.Reconnecting,
            [(ReconnectState.Renegotiating, ReconnectTrigger.ResumeSucceeded)] = ReconnectState.Resumed,
            [(ReconnectState.Resumed, ReconnectTrigger.Stabilized)] = ReconnectState.Connected,
        };

        ReconnectState[] recoverable =
        [
            ReconnectState.ConnectionInterrupted,
            ReconnectState.Reconnecting,
            ReconnectState.Reauthenticating,
            ReconnectState.Renegotiating,
        ];

        foreach (var state in recoverable)
        {
            table[(state, ReconnectTrigger.RetryExhausted)] = ReconnectState.ReconnectFailed;
            table[(state, ReconnectTrigger.AuthenticationMismatch)] = ReconnectState.ReconnectFailed;
        }

        foreach (var state in Enum.GetValues<ReconnectState>().Where(s => s != ReconnectState.Ended))
        {
            table[(state, ReconnectTrigger.EndRequested)] = ReconnectState.Ended;
        }

        return table;
    }
}
