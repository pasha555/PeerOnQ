namespace PeerOnQ.Domain.Sessions;

public enum SessionState
{
    Idle = 0,
    ResolvingDevice = 1,
    RequestingPermission = 2,
    AwaitingPermission = 3,
    Negotiating = 4,
    Connecting = 5,
    ConnectedViewOnly = 6,
    Reconnecting = 7,
    Ending = 8,
    Ended = 9,
    Failed = 10,
}

public enum SessionTrigger
{
    StartRequest = 0,
    DeviceResolved = 1,
    PermissionRequestSent = 2,
    PermissionAccepted = 3,
    PermissionRefused = 4,
    NegotiationCompleted = 5,
    MediaConnected = 6,
    ConnectionLost = 7,
    Reconnected = 8,
    EndRequested = 9,
    Closed = 10,
    Fault = 11,
}
