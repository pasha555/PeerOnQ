using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PeerOnQ.Domain.Identity;
using StackExchange.Redis;

namespace PeerOnQ.Signaling.Server.Sessions;

/// <summary>
/// Redis-backed signaling session authority. Mutations use optimistic compare-and-set transactions,
/// so permission transitions and one-use resume-token consumption remain atomic across nodes.
/// </summary>
public sealed class RedisSessionStore(
    IConnectionMultiplexer redis,
    IOptions<SignalingOptions> options,
    TimeProvider timeProvider,
    ILogger<RedisSessionStore> logger) : ISessionStore
{
    private const int MaxMutationAttempts = 16;
    private const int MaxExpiryBatch = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly LuaScript CreateSessionScript = LuaScript.Prepare(
        "if redis.call('EXISTS', @sessionKey) ~= 0 then return 0 end; " +
        "local limit = tonumber(@limit); " +
        "if redis.call('SCARD', @requesterSessions) >= limit " +
        "or redis.call('SCARD', @targetSessions) >= limit then return -1 end; " +
        "redis.call('SET', @sessionKey, @value); redis.call('SADD', @sessionIndex, @sessionId); " +
        "redis.call('SADD', @requesterSessions, @sessionId); " +
        "redis.call('SADD', @targetSessions, @sessionId); " +
        "redis.call('ZADD', @deadlineIndex, @deadline, @sessionId); return 1");
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly SignalingOptions _options = options.Value;
    private readonly string _prefix = options.Value.Cluster.KeyPrefix;

    public async ValueTask<int> CountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return checked((int)await _database.SetLengthAsync(SessionIndexKey));
    }

    public async ValueTask<SessionCreateResult> TryCreateAsync(
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
        if (!Guid.TryParse(sessionId, out _)
            || requester == target
            || string.IsNullOrWhiteSpace(mode)
            || mode.Length > 32
            || string.IsNullOrWhiteSpace(accessKind)
            || accessKind.Length > 32)
            throw new ArgumentException("Distributed signaling session inputs are invalid.");

        var now = timeProvider.GetUtcNow();
        var record = new RedisSessionRecord
        {
            SessionId = sessionId,
            RequesterId = requester.Value,
            TargetId = target.Value,
            Mode = mode,
            Permissions = permissions,
            AccessKind = accessKind,
            FileRelayEnabled = fileRelayEnabled,
            RemoteScopeSelectionRequired = remoteScopeSelectionRequired,
            CreatedAt = now,
            PermissionDeadline = now + _options.PermissionTimeout,
            NegotiationDeadline = now + _options.PermissionTimeout + _options.NegotiationTimeout,
            State = ServerSessionState.AwaitingPermission,
        };

        var result = await _database.ScriptEvaluateAsync(
            CreateSessionScript,
            new
            {
                sessionKey = (RedisKey)SessionKey(sessionId),
                requesterSessions = (RedisKey)DeviceSessionsKey(requester.Value),
                targetSessions = (RedisKey)DeviceSessionsKey(target.Value),
                sessionIndex = (RedisKey)SessionIndexKey,
                deadlineIndex = (RedisKey)DeadlineIndexKey,
                sessionId = (RedisValue)sessionId,
                value = (RedisValue)Serialize(record),
                deadline = Score(record.PermissionDeadline),
                limit = _options.MaxConcurrentSessionsPerDevice,
            });
        return new SessionCreateResult((long)result == 1, record.ToServerSession());
    }

    public async ValueTask<ServerSession?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(sessionId, cancellationToken);
        return loaded?.Record.ToServerSession();
    }

    public async ValueTask<bool> RemoveAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxMutationAttempts; attempt++)
        {
            var loaded = await LoadAsync(sessionId, cancellationToken);
            if (loaded is null) return false;
            var transaction = _database.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(SessionKey(sessionId), loaded.Value.Serialized));
            _ = transaction.KeyDeleteAsync(SessionKey(sessionId));
            _ = transaction.SetRemoveAsync(SessionIndexKey, sessionId);
            _ = transaction.SetRemoveAsync(DeviceSessionsKey(loaded.Value.Record.RequesterId), sessionId);
            _ = transaction.SetRemoveAsync(DeviceSessionsKey(loaded.Value.Record.TargetId), sessionId);
            _ = transaction.SortedSetRemoveAsync(DeadlineIndexKey, sessionId);
            if (await transaction.ExecuteAsync()) return true;
        }

        logger.LogWarning("Could not remove signaling session {SessionId} after bounded contention", sessionId);
        return false;
    }

    public async ValueTask BindOwnerAsync(
        string sessionId,
        PeerOnQId deviceId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || connectionId.Length > 128)
            throw new ArgumentException("Distributed signaling connection ownership is invalid.", nameof(connectionId));

        var updated = await MutateAsync(sessionId, record =>
        {
            var lease = record.LeaseOf(deviceId.Value);
            if (lease is null) return false;
            lease.ConnectionId = connectionId;
            lease.DisconnectedAt = null;
            lease.DisconnectDeadline = null;
            return true;
        }, cancellationToken);
        if (!updated)
            throw new InvalidOperationException("Could not bind distributed signaling session ownership.");
    }

    public async ValueTask<bool> IsOwnedByAsync(
        string sessionId,
        PeerOnQId deviceId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var loaded = await LoadAsync(sessionId, cancellationToken);
        return loaded?.Record.LeaseOf(deviceId.Value)?.ConnectionId == connectionId;
    }

    public async ValueTask<IssuedResumeToken> IssueResumeTokenAsync(
        string sessionId,
        PeerOnQId deviceId,
        int protocolVersion,
        CancellationToken cancellationToken = default)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(tokenBytes);
        var token = Convert.ToBase64String(tokenBytes);
        var expiresAt = timeProvider.GetUtcNow() + _options.ResumeTokenLifetime;
        CryptographicOperations.ZeroMemory(tokenBytes);

        try
        {
            var hashBase64 = Convert.ToBase64String(hash);
            var updated = await MutateAsync(sessionId, record =>
            {
                var lease = record.LeaseOf(deviceId.Value);
                if (lease is null) return false;
                lease.ResumeTokenHashBase64 = hashBase64;
                lease.ResumeTokenExpiresAt = expiresAt;
                lease.ResumeProtocolVersion = protocolVersion;
                return true;
            }, cancellationToken);
            if (!updated)
                throw new InvalidOperationException("Cannot issue a resume token to a non-participant.");
            return new IssuedResumeToken(token, expiresAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public async ValueTask<ServerSession?> TryResumeAsync(
        string sessionId,
        PeerOnQId deviceId,
        string token,
        string connectionId,
        int protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId)
            || connectionId.Length > 128
            || string.IsNullOrWhiteSpace(token)
            || token.Length > 128)
            return null;

        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(token);
        }
        catch (FormatException)
        {
            return null;
        }

        var suppliedHash = SHA256.HashData(supplied);
        CryptographicOperations.ZeroMemory(supplied);
        try
        {
            for (var attempt = 0; attempt < MaxMutationAttempts; attempt++)
            {
                var loaded = await LoadAsync(sessionId, cancellationToken);
                if (loaded is null) return null;
                var lease = loaded.Value.Record.LeaseOf(deviceId.Value);
                if (lease?.ResumeTokenHashBase64 is null) return null;

                byte[] storedHash;
                try
                {
                    storedHash = Convert.FromBase64String(lease.ResumeTokenHashBase64);
                }
                catch (FormatException)
                {
                    return null;
                }

                var now = timeProvider.GetUtcNow();
                var valid = lease.ResumeTokenExpiresAt > now
                            && lease.ResumeProtocolVersion == protocolVersion
                            && (lease.DisconnectDeadline is null || lease.DisconnectDeadline > now)
                            && CryptographicOperations.FixedTimeEquals(storedHash, suppliedHash);
                CryptographicOperations.ZeroMemory(storedHash);
                if (!valid) return null;

                lease.ResumeTokenHashBase64 = null;
                lease.ResumeTokenExpiresAt = default;
                lease.ResumeProtocolVersion = default;
                lease.ConnectionId = connectionId;
                lease.DisconnectedAt = null;
                lease.DisconnectDeadline = null;
                if (await SaveAsync(loaded.Value, cancellationToken))
                    return loaded.Value.Record.ToServerSession();
            }

            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedHash);
        }
    }

    public ValueTask<bool> TryTransitionAsync(
        string sessionId,
        ServerSessionState expected,
        ServerSessionState next,
        CancellationToken cancellationToken = default) =>
        MutateAsync(sessionId, record =>
        {
            if (record.State != expected) return false;
            record.State = next;
            return true;
        }, cancellationToken);

    public ValueTask<bool> TryAcceptScopeAsync(
        string sessionId,
        string mode,
        int permissions,
        CancellationToken cancellationToken = default) =>
        MutateAsync(sessionId, record =>
        {
            if (record.State != ServerSessionState.AwaitingPermission
                || (permissions & ~record.Permissions) != 0)
            {
                return false;
            }

            record.Mode = mode;
            record.Permissions = permissions;
            record.State = ServerSessionState.Negotiating;
            return true;
        }, cancellationToken);

    public async ValueTask MarkActiveAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await MutateAsync(sessionId, record =>
        {
            if (record.State == ServerSessionState.Active) return true;
            if (record.State != ServerSessionState.Negotiating) return false;
            record.State = ServerSessionState.Active;
            return true;
        }, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ServerSession>> MarkDisconnectedAsync(
        PeerOnQId deviceId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var ids = await _database.SetMembersAsync(DeviceSessionsKey(deviceId.Value));
        var affected = new List<ServerSession>(Math.Min(ids.Length, _options.MaxConcurrentSessionsPerDevice));
        foreach (var value in ids.Take(_options.MaxConcurrentSessionsPerDevice))
        {
            var id = value.ToString();
            ServerSession? updatedSession = null;
            var updated = await MutateAsync(id, record =>
            {
                var lease = record.LeaseOf(deviceId.Value);
                if (lease is null || !string.Equals(lease.ConnectionId, connectionId, StringComparison.Ordinal))
                    return false;
                var now = timeProvider.GetUtcNow();
                lease.ConnectionId = null;
                lease.DisconnectedAt = now;
                lease.DisconnectDeadline = now + _options.DisconnectGracePeriod;
                updatedSession = record.ToServerSession();
                return true;
            }, cancellationToken);
            if (updated && updatedSession is not null) affected.Add(updatedSession);
        }
        return affected;
    }

    public async ValueTask<IReadOnlyList<ExpiredServerSession>> RemoveExpiredAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var ids = await _database.SortedSetRangeByScoreAsync(
            DeadlineIndexKey,
            stop: Score(now),
            take: MaxExpiryBatch);
        var expired = new List<ExpiredServerSession>(ids.Length);

        foreach (var value in ids)
        {
            var id = value.ToString();
            var loaded = await LoadAsync(id, cancellationToken);
            if (loaded is null)
            {
                await _database.SortedSetRemoveAsync(DeadlineIndexKey, id);
                continue;
            }

            var deadline = NextDeadline(loaded.Value.Record);
            if (deadline is null || deadline > now)
            {
                await UpdateDeadlineAsync(id, deadline);
                continue;
            }

            var reason = loaded.Value.Record.RequesterLease.DisconnectDeadline is not null
                         || loaded.Value.Record.TargetLease.DisconnectDeadline is not null
                ? "ReconnectFailed"
                : loaded.Value.Record.State == ServerSessionState.AwaitingPermission
                    ? "PermissionTimeout"
                    : "NegotiationTimeout";
            var session = loaded.Value.Record.ToServerSession();
            if (await RemoveAsync(id, cancellationToken))
                expired.Add(new ExpiredServerSession(session, reason));
        }

        return expired;
    }

    private async ValueTask<bool> MutateAsync(
        string sessionId,
        Func<RedisSessionRecord, bool> mutation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxMutationAttempts; attempt++)
        {
            var loaded = await LoadAsync(sessionId, cancellationToken);
            if (loaded is null || !mutation(loaded.Value.Record)) return false;
            if (await SaveAsync(loaded.Value, cancellationToken)) return true;
        }

        logger.LogWarning("Signaling session {SessionId} exceeded bounded update contention", sessionId);
        return false;
    }

    private async ValueTask<LoadedRecord?> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serialized = await _database.StringGetAsync(SessionKey(sessionId));
        if (serialized.IsNullOrEmpty) return null;
        try
        {
            var record = JsonSerializer.Deserialize<RedisSessionRecord>((string)serialized!, JsonOptions);
            return record is null || !record.IsValid() ? null : new LoadedRecord(serialized, record);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Rejected malformed distributed session state for {SessionId}", sessionId);
            return null;
        }
    }

    private async ValueTask<bool> SaveAsync(LoadedRecord loaded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = _database.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(
            SessionKey(loaded.Record.SessionId),
            loaded.Serialized));
        _ = transaction.StringSetAsync(SessionKey(loaded.Record.SessionId), Serialize(loaded.Record));
        var deadline = NextDeadline(loaded.Record);
        if (deadline is null)
            _ = transaction.SortedSetRemoveAsync(DeadlineIndexKey, loaded.Record.SessionId);
        else
            _ = transaction.SortedSetAddAsync(DeadlineIndexKey, loaded.Record.SessionId, Score(deadline.Value));
        return await transaction.ExecuteAsync();
    }

    private Task UpdateDeadlineAsync(string sessionId, DateTimeOffset? deadline) => deadline is null
        ? _database.SortedSetRemoveAsync(DeadlineIndexKey, sessionId)
        : _database.SortedSetAddAsync(DeadlineIndexKey, sessionId, Score(deadline.Value));

    private static DateTimeOffset? NextDeadline(RedisSessionRecord record)
    {
        var disconnect = new[]
            {
                record.RequesterLease.DisconnectDeadline,
                record.TargetLease.DisconnectDeadline,
            }
            .Where(value => value is not null)
            .Min();
        if (disconnect is not null) return disconnect;
        return record.State switch
        {
            ServerSessionState.AwaitingPermission => record.PermissionDeadline,
            ServerSessionState.Negotiating => record.NegotiationDeadline,
            ServerSessionState.Ended => record.CreatedAt,
            _ => null,
        };
    }

    private string SessionKey(string sessionId) => $"{_prefix}:session:{sessionId}";
    private string DeviceSessionsKey(string deviceId) => $"{_prefix}:device-sessions:{deviceId}";
    private string SessionIndexKey => $"{_prefix}:sessions:index";
    private string DeadlineIndexKey => $"{_prefix}:sessions:deadlines";
    private static double Score(DateTimeOffset value) => value.ToUnixTimeMilliseconds();
    private static string Serialize(RedisSessionRecord record) => JsonSerializer.Serialize(record, JsonOptions);

    private readonly record struct LoadedRecord(RedisValue Serialized, RedisSessionRecord Record);

    private sealed class RedisSessionRecord
    {
        public string SessionId { get; set; } = string.Empty;
        public string RequesterId { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public int Permissions { get; set; }
        public string AccessKind { get; set; } = string.Empty;
        public bool FileRelayEnabled { get; set; }
        public bool RemoteScopeSelectionRequired { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset PermissionDeadline { get; set; }
        public DateTimeOffset NegotiationDeadline { get; set; }
        public ServerSessionState State { get; set; }
        public RedisParticipantLease RequesterLease { get; set; } = new();
        public RedisParticipantLease TargetLease { get; set; } = new();

        public RedisParticipantLease? LeaseOf(string deviceId) =>
            string.Equals(RequesterId, deviceId, StringComparison.Ordinal) ? RequesterLease
            : string.Equals(TargetId, deviceId, StringComparison.Ordinal) ? TargetLease
            : null;

        public bool IsValid() =>
            Guid.TryParse(SessionId, out _)
            && PeerOnQId.IsValid(RequesterId)
            && PeerOnQId.IsValid(TargetId)
            && RequesterId != TargetId
            && Mode is { Length: > 0 and <= 32 }
            && AccessKind is { Length: > 0 and <= 32 }
            && Enum.IsDefined(State)
            && RequesterLease?.IsValid() == true
            && TargetLease?.IsValid() == true;

        public ServerSession ToServerSession() => new()
        {
            SessionId = SessionId,
            RequesterId = PeerOnQId.Parse(RequesterId),
            TargetId = PeerOnQId.Parse(TargetId),
            Mode = Mode,
            Permissions = Permissions,
            AccessKind = AccessKind,
            FileRelayEnabled = FileRelayEnabled,
            RemoteScopeSelectionRequired = RemoteScopeSelectionRequired,
            CreatedAt = CreatedAt,
            PermissionDeadline = PermissionDeadline,
            NegotiationDeadline = NegotiationDeadline,
            State = State,
            RequesterLease = RequesterLease.ToServerLease(PeerOnQId.Parse(RequesterId)),
            TargetLease = TargetLease.ToServerLease(PeerOnQId.Parse(TargetId)),
        };
    }

    private sealed class RedisParticipantLease
    {
        public string? ConnectionId { get; set; }
        public string? ResumeTokenHashBase64 { get; set; }
        public DateTimeOffset ResumeTokenExpiresAt { get; set; }
        public int ResumeProtocolVersion { get; set; }
        public DateTimeOffset? DisconnectedAt { get; set; }
        public DateTimeOffset? DisconnectDeadline { get; set; }

        public bool IsValid()
        {
            if (ConnectionId is { Length: 0 or > 128 }) return false;
            if (ResumeTokenHashBase64 is null) return true;

            try
            {
                var hash = Convert.FromBase64String(ResumeTokenHashBase64);
                var valid = hash.Length == 32;
                CryptographicOperations.ZeroMemory(hash);
                return valid;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public SessionParticipantLease ToServerLease(PeerOnQId deviceId) => new()
        {
            DeviceId = deviceId,
            ConnectionId = ConnectionId,
            ResumeTokenHash = ResumeTokenHashBase64 is null
                ? null
                : Convert.FromBase64String(ResumeTokenHashBase64),
            ResumeTokenExpiresAt = ResumeTokenExpiresAt,
            ResumeProtocolVersion = ResumeProtocolVersion,
            DisconnectedAt = DisconnectedAt,
            DisconnectDeadline = DisconnectDeadline,
        };
    }
}
