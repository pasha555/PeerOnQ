namespace PeerOnQ.Domain.Errors;

public class PeerOnQException : Exception
{
    public PeerOnQException(string message) : base(message) { }
    public PeerOnQException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a state machine is asked for a transition that the protocol forbids.</summary>
public sealed class InvalidSessionTransitionException : PeerOnQException
{
    public InvalidSessionTransitionException(string message) : base(message) { }
}

/// <summary>Thrown when the signaling peer sends something that does not match the protocol.</summary>
public sealed class SignalingProtocolException : PeerOnQException
{
    public SignalingProtocolException(string message) : base(message) { }
}

/// <summary>Permanent identity mismatch. Retrying cannot make the pinned key valid.</summary>
public sealed class SignalingAuthenticationException : PeerOnQException
{
    public SignalingAuthenticationException(string message) : base(message) { }
    public SignalingAuthenticationException(string message, Exception innerException)
        : base(message, innerException) { }
}
