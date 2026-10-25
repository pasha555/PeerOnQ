namespace PeerOnQ.Domain.Sessions;

public enum ReconnectState
{
    Connected = 0,
    ConnectionInterrupted = 1,
    Reconnecting = 2,
    Reauthenticating = 3,
    Renegotiating = 4,
    Resumed = 5,
    ReconnectFailed = 6,
    Ended = 7,
}

public enum ReconnectTrigger
{
    ConnectionLost = 0,
    RetryStarted = 1,
    ReauthenticationRequired = 2,
    Reauthenticated = 3,
    RenegotiationStarted = 4,
    ResumeSucceeded = 5,
    Stabilized = 6,
    RetryExhausted = 7,
    AuthenticationMismatch = 8,
    EndRequested = 9,
}

public sealed record ReconnectTransition(
    ReconnectState From,
    ReconnectTrigger Trigger,
    ReconnectState To);
