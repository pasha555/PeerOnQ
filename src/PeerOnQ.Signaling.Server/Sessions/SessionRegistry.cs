using System.Collections.Concurrent;
using System.Security.Cryptography;
using PeerOnQ.Domain.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PeerOnQ.Signaling.Server.Sessions;

public enum ServerSessionState
{
    AwaitingPermission = 0,
    Negotiating = 1,
    Active = 2,
    Ended = 3,
}

public sealed class ServerSession
{
    public required string SessionId { get; init; }
    public required PeerOnQId RequesterId { get; init; }
    public required PeerOnQId TargetId { get; init; }
    public required string Mode { get; set; }
    public required int Permissions { get; set; }
    public required string AccessKind { get; init; }
    public bool FileRelayEnabled { get; init; }
    public bool RemoteScopeSelectionRequired { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset PermissionDeadline { get; set; }
    public DateTimeOffset NegotiationDeadline { get; set; }
    public ServerSessionState State { get; set; } = ServerSessionState.AwaitingPermission;
    public required SessionParticipantLease RequesterLease { get; init; }
    public required SessionParticipantLease TargetLease { get; init; }

    public bool Involves(PeerOnQId device) => device == RequesterId || device == TargetId;

    public PeerOnQId PeerOf(PeerOnQId device) => device == RequesterId ? TargetId : RequesterId;

    public SessionParticipantLease LeaseOf(PeerOnQId device) =>
        device == RequesterId ? RequesterLease : TargetLease;
}

public sealed class SessionParticipantLease
{
    public required PeerOnQId DeviceId { get; init; }
    public string? ConnectionId { get; set; }
    public byte[]? ResumeTokenHash { get; set; }
    public DateTimeOffset ResumeTokenExpiresAt { get; set; }
    public int ResumeProtocolVersion { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public DateTimeOffset? DisconnectDeadline { get; set; }
}

public sealed record IssuedResumeToken(string Token, DateTimeOffset ExpiresAt);

public sealed record ExpiredServerSession(ServerSession Session, string Reason);
public sealed record SessionCreateResult(bool Created, ServerSession Session);

/// <summary>
/// Distributed-ready persistence boundary. The in-memory implementation is used for one-node
/// development; a shared implementation can preserve the same ownership and resume invariants.
/// </summary>
public interface ISessionStore
{
    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);
    ValueTask<SessionCreateResult> TryCreateAsync(
        string sessionId,
        PeerOnQId requester,
        PeerOnQId target,
        string mode,
        int permissions = 1,
        string accessKind = "attended",
        bool fileRelayEnabled = false,
        CancellationToken cancellationToken = default,
        bool remoteScopeSelectionRequired = false);
    ValueTask<ServerSession?> GetAsync(string sessionId, CancellationToken cancellationToken = default);
    ValueTask<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default);
    ValueTask BindOwnerAsync(
        string sessionId,
        PeerOnQId deviceId,
        string connectionId,
        CancellationToken cancellationToken = default);
    ValueTask<bool> IsOwnedByAsync(
        string sessionId,
        PeerOnQId deviceId,
        string connectionId,
        CancellationToken cancellationToken = default);
    ValueTask<IssuedResumeToken> IssueResumeTokenAsync(
        string sessionId,
        PeerOnQId deviceId,
        int protocolVersion,
        CancellationToken cancellationToken = default);
    ValueTask<ServerSession?> TryResumeAsync(
        string sessionId,
        PeerOnQId deviceId,
        string token,
        string connectionId,
        int protocolVersion,
        CancellationToken cancellationToken = default);
    ValueTask<bool> TryTransitionAsync(
        string sessionId,
        ServerSessionState expected,
        ServerSessionState next,
        CancellationToken cancellationToken = default);
    ValueTask<bool> TryAcceptScopeAsync(
        string sessionId,
        string mode,
        int permissions,
        CancellationToken cancellationToken = default);
    ValueTask MarkActiveAsync(string sessionId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ServerSession>> MarkDisconnectedAsync(
        PeerOnQId deviceId,
        string connectionId,
        CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ExpiredServerSession>> RemoveExpiredAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Tracks in-flight sessions so the server can route by session id, reject strangers, and
/// clean up sessions that were abandoned by either side.
/// </summary>
public sealed class SessionRegistry(
    IOptions<SignalingOptions> options,
    ILogger<SessionRegistry> logger,
    TimeProvider? timeProvider = null) : ISessionStore
{
    private readonly ConcurrentDictionary<string, ServerSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public int Count => _sessions.Count;

    public ValueTask<int> CountAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Count);

    public ValueTask<SessionCreateResult> TryCreateAsync(
        string sessionId,
        PeerOnQId requester,
        PeerOnQId target,
        string mode,
        int permissions = 1,
        string accessKind = "attended",
        bool fileRelayEnabled = false,
        CancellationToken cancellationToken = default,
        bool remoteScopeSelectionRequired = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var created = TryCreate(
            sessionId,
            requester,
            target,
            mode,
            out var session,
            permissions,
            accessKind,
            fileRelayEnabled,
            remoteScopeSelectionRequired);
        return ValueTask.FromResult(new SessionCreateResult(created, session));
    }

    public ValueTask<ServerSession?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryGet(sessionId, out var session) ? session : null);
    }

    public ValueTask<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_sessions.TryRemove(sessionId, out _));
    }

    public ValueTask BindOwnerAsync(
        string sessionId,
        PeerOnQId deviceId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BindOwner(sessionId, deviceId, connectionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsOwnedByAsync(
        string sessionId,
        PeerOnQId deviceId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(IsOwnedBy(sessionId, deviceId, connectionId));
    }

    public ValueTask<IssuedResumeToken> IssueResumeTokenAsync(
        string sessionId,
        PeerOnQId deviceId,
        int protocolVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(IssueResumeToken(sessionId, deviceId, protocolVersion));
    }

    public ValueTask<ServerSession?> TryResumeAsync(
        string sessionId,
        PeerOnQId deviceId,
        string token,
        string connectionId,
        int protocolVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(TryResume(
            sessionId, deviceId, token, connectionId, protocolVersion, out var session)
            ? session
            : null);
    }

    public ValueTask<bool> TryTransitionAsync(
        string sessionId,
        ServerSessionState expected,
        ServerSessionState next,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var session)) return ValueTask.FromResult(false);
        lock (session)
        {
            if (session.State != expected) return ValueTask.FromResult(false);
            session.State = next;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> TryAcceptScopeAsync(
        string sessionId,
        string mode,
        int permissions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var session)) return ValueTask.FromResult(false);
        lock (session)
        {
            if (session.State != ServerSessionState.AwaitingPermission
                || (permissions & ~session.Permissions) != 0)
            {
                return ValueTask.FromResult(false);
            }

            session.Mode = mode;
            session.Permissions = permissions;
            session.State = ServerSessionState.Negotiating;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask MarkActiveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            lock (session)
            {
                if (session.State == ServerSessionState.Negotiating)
                    session.State = ServerSessionState.Active;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ServerSession>> MarkDisconnectedAsync(
        PeerOnQId deviceId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MarkDisconnected(deviceId, connectionId));
    }

    public ValueTask<IReadOnlyList<ExpiredServerSession>> RemoveExpiredAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RemoveExpired());
    }

    public bool TryCreate(
        string sessionId,
        PeerOnQId requester,
        PeerOnQId target,
        string mode,
        out ServerSession session,
        int permissions = 1,
        string accessKind = "attended",
        bool fileRelayEnabled = false,
        bool remoteScopeSelectionRequired = false)
    {
        var now = _time.GetUtcNow();
        session = new ServerSession
        {
            SessionId = sessionId,
            RequesterId = requester,
            TargetId = target,
            Mode = mode,
            Permissions = permissions,
            AccessKind = accessKind,
            FileRelayEnabled = fileRelayEnabled,
            RemoteScopeSelectionRequired = remoteScopeSelectionRequired,
            CreatedAt = now,
            PermissionDeadline = now + options.Value.PermissionTimeout,
            NegotiationDeadline = now + options.Value.PermissionTimeout + options.Value.NegotiationTimeout,
            RequesterLease = new SessionParticipantLease { DeviceId = requester },
            TargetLease = new SessionParticipantLease { DeviceId = target },
        };

        return _sessions.TryAdd(sessionId, session);
    }

    public bool TryGet(string sessionId, out ServerSession session) => _sessions.TryGetValue(sessionId, out session!);

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public void BindOwner(string sessionId, PeerOnQId deviceId, string connectionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || !session.Involves(deviceId)) return;

        lock (session)
        {
            var lease = session.LeaseOf(deviceId);
            lease.ConnectionId = connectionId;
            lease.DisconnectedAt = null;
            lease.DisconnectDeadline = null;
        }
    }

    public bool IsOwnedBy(string sessionId, PeerOnQId deviceId, string connectionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || !session.Involves(deviceId)) return false;

        lock (session)
        {
            return string.Equals(session.LeaseOf(deviceId).ConnectionId, connectionId, StringComparison.Ordinal);
        }
    }

    public IssuedResumeToken IssueResumeToken(string sessionId, PeerOnQId deviceId, int protocolVersion)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || !session.Involves(deviceId))
        {
            throw new InvalidOperationException("Cannot issue a resume token to a non-participant.");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(tokenBytes);
        var expiresAt = _time.GetUtcNow() + options.Value.ResumeTokenLifetime;

        lock (session)
        {
            var lease = session.LeaseOf(deviceId);
            if (lease.ResumeTokenHash is { Length: > 0 } previousHash)
                CryptographicOperations.ZeroMemory(previousHash);
            lease.ResumeTokenHash = SHA256.HashData(tokenBytes);
            lease.ResumeTokenExpiresAt = expiresAt;
            lease.ResumeProtocolVersion = protocolVersion;
        }

        CryptographicOperations.ZeroMemory(tokenBytes);
        return new IssuedResumeToken(token, expiresAt);
    }

    public bool TryResume(
        string sessionId,
        PeerOnQId deviceId,
        string token,
        string connectionId,
        int protocolVersion,
        out ServerSession session)
    {
        if (!_sessions.TryGetValue(sessionId, out session!) || !session.Involves(deviceId)) return false;

        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(token);
        }
        catch (FormatException)
        {
            return false;
        }

        var suppliedHash = SHA256.HashData(supplied);
        CryptographicOperations.ZeroMemory(supplied);

        lock (session)
        {
            var lease = session.LeaseOf(deviceId);
            var now = _time.GetUtcNow();
            var valid = lease.ResumeTokenHash is { Length: > 0 }
                        && lease.ResumeTokenExpiresAt > now
                        && lease.ResumeProtocolVersion == protocolVersion
                        && (lease.DisconnectDeadline is null || lease.DisconnectDeadline > now)
                        && CryptographicOperations.FixedTimeEquals(lease.ResumeTokenHash, suppliedHash);

            CryptographicOperations.ZeroMemory(suppliedHash);
            if (!valid) return false;

            // Consume under the same lock as validation so concurrent connections cannot replay
            // one token before the handler issues its rotated replacement.
            CryptographicOperations.ZeroMemory(lease.ResumeTokenHash!);
            lease.ResumeTokenHash = null;
            lease.ResumeTokenExpiresAt = default;
            lease.ResumeProtocolVersion = default;
            lease.ConnectionId = connectionId;
            lease.DisconnectedAt = null;
            lease.DisconnectDeadline = null;
            return true;
        }
    }

    public IReadOnlyList<ServerSession> MarkDisconnected(PeerOnQId deviceId, string connectionId)
    {
        var affected = new List<ServerSession>();
        var now = _time.GetUtcNow();

        foreach (var session in _sessions.Values)
        {
            if (!session.Involves(deviceId)) continue;

            lock (session)
            {
                var lease = session.LeaseOf(deviceId);
                if (!string.Equals(lease.ConnectionId, connectionId, StringComparison.Ordinal)) continue;

                lease.ConnectionId = null;
                lease.DisconnectedAt = now;
                lease.DisconnectDeadline = now + options.Value.DisconnectGracePeriod;
                affected.Add(session);
            }
        }

        return affected;
    }

    public IReadOnlyList<ExpiredServerSession> RemoveExpired()
    {
        var now = _time.GetUtcNow();
        var expired = new List<ExpiredServerSession>();

        foreach (var (id, session) in _sessions)
        {
            DateTimeOffset deadline;
            string reason;

            lock (session)
            {
                var disconnectedDeadline = new[]
                    {
                        session.RequesterLease.DisconnectDeadline,
                        session.TargetLease.DisconnectDeadline,
                    }
                    .Where(value => value is not null)
                    .Min();

                if (disconnectedDeadline is { } resumeDeadline)
                {
                    deadline = resumeDeadline;
                    reason = "ReconnectFailed";
                }
                else
                {
                    deadline = session.State == ServerSessionState.AwaitingPermission
                        ? session.PermissionDeadline
                        : session.NegotiationDeadline;
                    reason = session.State == ServerSessionState.AwaitingPermission
                        ? "PermissionTimeout"
                        : "NegotiationTimeout";
                }
            }

            if (session.State != ServerSessionState.Active && deadline <= now && _sessions.TryRemove(id, out var removed))
            {
                logger.LogInformation(
                    "Session {SessionId} expired in state {State}", removed.SessionId, removed.State);
                expired.Add(new ExpiredServerSession(removed, reason));
            }
            else if (reason == "ReconnectFailed" && deadline <= now && _sessions.TryRemove(id, out removed))
            {
                logger.LogInformation("Session {SessionId} resume window expired", removed.SessionId);
                expired.Add(new ExpiredServerSession(removed, reason));
            }
        }

        return expired;
    }

}
