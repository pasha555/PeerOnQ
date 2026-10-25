using System.Security.Cryptography;
using PeerOnQ.Domain.Identity;

namespace PeerOnQ.Signaling.Server.Sessions;

/// <summary>Why a resume was refused, and who owned the participant's lease at that moment.</summary>
public sealed record ResumeRejection(string Reason, string State, string Owner);

/// <summary>
/// Explains a refused resume for the server log. The 0.9.86 field test only logged
/// invalid_or_expired_resume_token, so an expired token, a lost lease and a swept session looked
/// the same. The explanation is computed after the refusal from the stored session; it never
/// changes the decision and is not sent to the client.
/// </summary>
public static class SessionResumeDiagnostics
{
    public static ResumeRejection Explain(
        ServerSession? session,
        PeerOnQId deviceId,
        string? suppliedToken,
        string connectionId,
        int protocolVersion,
        DateTimeOffset now)
    {
        if (session is null) return new ResumeRejection("session_not_found", "none", "none");
        if (!session.Involves(deviceId)) return new ResumeRejection("not_participant", session.State.ToString(), "none");

        var lease = session.LeaseOf(deviceId);
        var owner = lease.ConnectionId is null
            ? lease.DisconnectDeadline is { } deadline
                ? $"none, disconnected {(now - (lease.DisconnectedAt ?? now)).TotalSeconds:0} s, window {(deadline > now ? "open" : "closed")}"
                : "none"
            : string.Equals(lease.ConnectionId, connectionId, StringComparison.Ordinal)
                ? "this connection"
                : "an earlier connection";
        var state = session.State.ToString();

        if (session.State is ServerSessionState.AwaitingPermission or ServerSessionState.Ended)
            return new ResumeRejection("session_not_resumable", state, owner);
        if (lease.ResumeTokenHash is null && lease.PreviousResumeTokenHash is null)
            return new ResumeRejection("token_missing", state, owner);
        if (lease.ResumeProtocolVersion != protocolVersion)
            return new ResumeRejection("protocol_mismatch", state, owner);
        if (lease.DisconnectDeadline is { } window && window <= now)
            return new ResumeRejection("disconnect_window_expired", state, owner);

        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(suppliedToken ?? string.Empty);
        }
        catch (FormatException)
        {
            return new ResumeRejection("token_malformed", state, owner);
        }

        var suppliedHash = SHA256.HashData(supplied);
        CryptographicOperations.ZeroMemory(supplied);
        try
        {
            var current = lease.ResumeTokenHash is { Length: > 0 } currentHash
                          && CryptographicOperations.FixedTimeEquals(currentHash, suppliedHash);
            var previous = lease.PreviousResumeTokenHash is { Length: > 0 } previousHash
                           && CryptographicOperations.FixedTimeEquals(previousHash, suppliedHash);
            if (!current && !previous) return new ResumeRejection("token_mismatch", state, owner);
            var expiresAt = current ? lease.ResumeTokenExpiresAt : lease.PreviousResumeTokenExpiresAt;
            return expiresAt <= now
                ? new ResumeRejection("token_expired", state, owner)
                : new ResumeRejection("concurrent_claim", state, owner);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedHash);
        }
    }
}
