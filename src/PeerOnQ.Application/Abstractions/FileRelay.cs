using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Abstractions;

/// <summary>
/// Authenticated opaque-file-record route made available by signaling only after both session
/// participants negotiated it. Payloads remain protected by the session record layer.
/// </summary>
public sealed record FileRelayFrame(SessionId SessionId, byte[] Payload);

public interface IFileRelaySignaling
{
    bool IsFileRelayAvailable { get; }
    event EventHandler<FileRelayFrame>? FileRelayReceived;

    Task SendFileRelayAsync(
        SessionId sessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
}
