using PeerOnQ.Domain.Errors;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Domain.Tests;

public class SessionStateMachineTests
{
    private static SessionStateMachine ConnectedViewer()
    {
        var machine = new SessionStateMachine();
        machine.Fire(SessionTrigger.StartRequest);
        machine.Fire(SessionTrigger.DeviceResolved);
        machine.Fire(SessionTrigger.PermissionRequestSent);
        machine.Fire(SessionTrigger.PermissionAccepted);
        machine.Fire(SessionTrigger.NegotiationCompleted);
        machine.Fire(SessionTrigger.MediaConnected);
        return machine;
    }

    [Fact]
    public void Viewer_happy_path_reaches_ConnectedViewOnly()
    {
        var machine = ConnectedViewer();

        Assert.Equal(SessionState.ConnectedViewOnly, machine.State);
        Assert.False(machine.IsTerminal);
        Assert.Equal(6, machine.History.Count);
    }

    [Fact]
    public void Sharer_enters_AwaitingPermission_directly()
    {
        var machine = new SessionStateMachine();

        machine.Fire(SessionTrigger.PermissionRequestSent);

        Assert.Equal(SessionState.AwaitingPermission, machine.State);
    }

    [Fact]
    public void Refused_permission_ends_the_session()
    {
        var machine = new SessionStateMachine();
        machine.Fire(SessionTrigger.PermissionRequestSent);

        machine.Fire(SessionTrigger.PermissionRefused);
        Assert.Equal(SessionState.Ending, machine.State);

        machine.Fire(SessionTrigger.Closed);
        Assert.Equal(SessionState.Ended, machine.State);
        Assert.True(machine.IsTerminal);
    }

    [Fact]
    public void Reconnect_cycle_returns_to_ConnectedViewOnly()
    {
        var machine = ConnectedViewer();

        machine.Fire(SessionTrigger.ConnectionLost);
        Assert.Equal(SessionState.Reconnecting, machine.State);

        machine.Fire(SessionTrigger.Reconnected);
        Assert.Equal(SessionState.ConnectedViewOnly, machine.State);
    }

    [Theory]
    [InlineData(SessionState.Idle, SessionTrigger.MediaConnected)]
    [InlineData(SessionState.Idle, SessionTrigger.PermissionAccepted)]
    [InlineData(SessionState.Idle, SessionTrigger.Closed)]
    [InlineData(SessionState.AwaitingPermission, SessionTrigger.MediaConnected)]
    [InlineData(SessionState.Negotiating, SessionTrigger.PermissionAccepted)]
    [InlineData(SessionState.ConnectedViewOnly, SessionTrigger.StartRequest)]
    [InlineData(SessionState.Ended, SessionTrigger.EndRequested)]
    [InlineData(SessionState.Ended, SessionTrigger.Fault)]
    [InlineData(SessionState.Failed, SessionTrigger.Reconnected)]
    public void Invalid_transitions_are_rejected(SessionState from, SessionTrigger trigger)
    {
        var machine = new SessionStateMachine(from);
        RejectedTransition? rejected = null;
        machine.TransitionRejected += (_, e) => rejected = e;

        var moved = machine.TryFire(trigger, out var next);

        Assert.False(moved);
        Assert.Equal(from, machine.State);
        Assert.Equal(from, next);
        Assert.NotNull(rejected);
        Assert.Equal(from, rejected!.Value.State);
        Assert.Equal(trigger, rejected.Value.Trigger);
    }

    [Fact]
    public void Fire_throws_on_an_invalid_transition()
    {
        var machine = new SessionStateMachine();

        var ex = Assert.Throws<InvalidSessionTransitionException>(
            () => machine.Fire(SessionTrigger.MediaConnected));

        Assert.Contains("MediaConnected", ex.Message);
        Assert.Contains("Idle", ex.Message);
    }

    [Fact]
    public void Terminal_states_accept_nothing()
    {
        foreach (var terminal in new[] { SessionState.Ended, SessionState.Failed })
        {
            var machine = new SessionStateMachine(terminal);

            foreach (var trigger in Enum.GetValues<SessionTrigger>())
            {
                Assert.False(machine.CanFire(trigger));
            }

            Assert.True(machine.IsTerminal);
        }
    }

    [Fact]
    public void Any_live_state_can_end_or_fault()
    {
        SessionState[] live =
        [
            SessionState.ResolvingDevice, SessionState.RequestingPermission, SessionState.AwaitingPermission,
            SessionState.Negotiating, SessionState.Connecting, SessionState.ConnectedViewOnly,
            SessionState.Reconnecting,
        ];

        foreach (var state in live)
        {
            Assert.Equal(SessionState.Ending, new SessionStateMachine(state).Fire(SessionTrigger.EndRequested));
            Assert.Equal(SessionState.Failed, new SessionStateMachine(state).Fire(SessionTrigger.Fault));
        }
    }

    [Fact]
    public void Transitions_are_recorded_in_order()
    {
        var machine = ConnectedViewer();

        var history = machine.History;

        Assert.Equal(SessionState.Idle, history[0].From);
        Assert.Equal(SessionState.ConnectedViewOnly, history[^1].To);
        for (var i = 1; i < history.Count; i++)
        {
            Assert.Equal(history[i - 1].To, history[i].From);
        }
    }

    [Fact]
    public void Concurrent_fires_produce_exactly_one_winner()
    {
        var machine = new SessionStateMachine(SessionState.ConnectedViewOnly);
        var successes = 0;

        Parallel.For(0, 64, iteration =>
        {
            if (machine.TryFire(SessionTrigger.ConnectionLost, out _))
            {
                Interlocked.Increment(ref successes);
            }
        });

        Assert.Equal(1, successes);
        Assert.Equal(SessionState.Reconnecting, machine.State);
    }
}
