using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Abstractions;

public sealed record UnattendedChallengeRequestNotification(
    string RequestId,
    PeerOnQId RequesterId,
    string RequesterFingerprint,
    DateTimeOffset ExpiresAt);

public sealed record UnattendedChallenge(
    string RequestId,
    bool Available,
    Guid? ChallengeId,
    string? Challenge,
    byte[]? Salt,
    int Iterations,
    DateTimeOffset? ExpiresAt,
    string ReasonCode,
    SessionPermission? AllowedPermissions = null);

public interface IUnattendedSignalingClient
{
    string? LocalKeyFingerprint { get; }
    event EventHandler<UnattendedChallengeRequestNotification>? UnattendedChallengeRequested;

    Task<UnattendedChallenge> RequestUnattendedChallengeAsync(
        PeerOnQId target,
        CancellationToken cancellationToken = default);

    Task SendUnattendedChallengeResponseAsync(
        string requestId,
        PeerOnQId requester,
        UnattendedChallenge challenge,
        CancellationToken cancellationToken = default);
}
