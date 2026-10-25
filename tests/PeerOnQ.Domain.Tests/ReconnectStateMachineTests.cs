using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Domain.Tests;

public sealed class ReconnectStateMachineTests
{
    [Fact]
    public void Secure_resume_uses_the_declared_state_sequence()
    {
        var machine = new ReconnectStateMachine();

        machine.Fire(ReconnectTrigger.ConnectionLost);
        machine.Fire(ReconnectTrigger.RetryStarted);
        machine.Fire(ReconnectTrigger.ReauthenticationRequired);
        machine.Fire(ReconnectTrigger.Reauthenticated);
        machine.Fire(ReconnectTrigger.RenegotiationStarted);
        machine.Fire(ReconnectTrigger.ResumeSucceeded);
        machine.Fire(ReconnectTrigger.Stabilized);

        Assert.Equal(ReconnectState.Connected, machine.State);
        Assert.Equal(
            new[]
            {
                ReconnectState.ConnectionInterrupted,
                ReconnectState.Reconnecting,
                ReconnectState.Reauthenticating,
                ReconnectState.Reconnecting,
                ReconnectState.Renegotiating,
                ReconnectState.Resumed,
                ReconnectState.Connected,
            },
            machine.History.Select(item => item.To));
    }

    [Fact]
    public void Authentication_mismatch_is_terminal_for_reconnect()
    {
        var machine = new ReconnectStateMachine();
        machine.Fire(ReconnectTrigger.ConnectionLost);
        machine.Fire(ReconnectTrigger.RetryStarted);
        machine.Fire(ReconnectTrigger.ReauthenticationRequired);

        machine.Fire(ReconnectTrigger.AuthenticationMismatch);

        Assert.Equal(ReconnectState.ReconnectFailed, machine.State);
        Assert.False(machine.CanFire(ReconnectTrigger.RetryStarted));
    }

    [Fact]
    public void User_can_end_from_every_uncertain_state()
    {
        foreach (var state in new[]
                 {
                     ReconnectState.ConnectionInterrupted,
                     ReconnectState.Reconnecting,
                     ReconnectState.Reauthenticating,
                     ReconnectState.Renegotiating,
                 })
        {
            var machine = Reach(state);
            machine.Fire(ReconnectTrigger.EndRequested);
            Assert.Equal(ReconnectState.Ended, machine.State);
        }
    }

    private static ReconnectStateMachine Reach(ReconnectState state)
    {
        var machine = new ReconnectStateMachine();
        machine.Fire(ReconnectTrigger.ConnectionLost);
        if (state == ReconnectState.ConnectionInterrupted) return machine;
        machine.Fire(ReconnectTrigger.RetryStarted);
        if (state == ReconnectState.Reconnecting) return machine;
        machine.Fire(ReconnectTrigger.ReauthenticationRequired);
        if (state == ReconnectState.Reauthenticating) return machine;
        machine.Fire(ReconnectTrigger.Reauthenticated);
        machine.Fire(ReconnectTrigger.RenegotiationStarted);
        return machine;
    }
}
