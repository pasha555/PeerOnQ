using System.Collections.Concurrent;
using PeerOnQ.Domain.Identity;

namespace PeerOnQ.Signaling.Server.Sessions;

public sealed record PendingUnattendedChallenge(
    string RequestId,
    PeerOnQId RequesterId,
    PeerOnQId TargetId,
    DateTimeOffset ExpiresAt);

public interface IUnattendedChallengeStore
{
    ValueTask<bool> TryCreateAsync(
        string requestId,
        PeerOnQId requester,
        PeerOnQId target,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);
    ValueTask<PendingUnattendedChallenge?> TryConsumeAsync(
        string requestId,
        PeerOnQId respondingTarget,
        PeerOnQId claimedRequester,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Short-lived routing ownership for unattended password challenges. It carries no password,
/// verifier or proof. A distributed implementation can replace this boundary for multi-node
/// signaling in the same way as ISessionStore.
/// </summary>
public sealed class UnattendedChallengeRegistry(TimeProvider timeProvider) : IUnattendedChallengeStore
{
    private readonly ConcurrentDictionary<string, PendingUnattendedChallenge> _pending = new(StringComparer.Ordinal);

    public bool TryCreate(string requestId, PeerOnQId requester, PeerOnQId target, TimeSpan lifetime)
    {
        RemoveExpired();
        if (_pending.Count >= 10_000) return false;
        return _pending.TryAdd(requestId, new PendingUnattendedChallenge(
            requestId, requester, target, timeProvider.GetUtcNow() + lifetime));
    }

    public bool TryConsume(
        string requestId,
        PeerOnQId respondingTarget,
        PeerOnQId claimedRequester,
        out PendingUnattendedChallenge pending)
    {
        if (!_pending.TryRemove(requestId, out pending!)) return false;
        return pending.ExpiresAt > timeProvider.GetUtcNow()
               && pending.TargetId == respondingTarget
               && pending.RequesterId == claimedRequester;
    }

    public ValueTask<bool> TryCreateAsync(
        string requestId,
        PeerOnQId requester,
        PeerOnQId target,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryCreate(requestId, requester, target, lifetime));
    }

    public ValueTask<PendingUnattendedChallenge?> TryConsumeAsync(
        string requestId,
        PeerOnQId respondingTarget,
        PeerOnQId claimedRequester,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryConsume(
            requestId, respondingTarget, claimedRequester, out var pending)
            ? pending
            : null);
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var pair in _pending)
            if (pair.Value.ExpiresAt <= now) _pending.TryRemove(pair.Key, out _);
    }
}
