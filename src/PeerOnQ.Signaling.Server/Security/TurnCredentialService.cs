using System.Security.Cryptography;
using System.Text;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Transport.Protocol;
using Microsoft.Extensions.Options;

namespace PeerOnQ.Signaling.Server.Security;

/// <summary>
/// Issues coturn REST credentials. The shared secret stays server-side; the returned username
/// and HMAC-SHA1 password expire automatically and are scoped to an authenticated session attempt.
/// </summary>
public sealed class TurnCredentialService(
    IOptions<SignalingOptions> options,
    TimeProvider timeProvider)
{
    public bool IsReady
    {
        get
        {
            var turn = options.Value.Turn;
            return !turn.HasTurnEndpoints || turn.HasUsableSharedSecret;
        }
    }

    public IceServersMessage Issue(string sessionId, PeerOnQId deviceId)
    {
        var turn = options.Value.Turn;
        var servers = new List<IceServerDescriptor>();

        if (turn.StunUrls.Length > 0)
        {
            servers.Add(new IceServerDescriptor { Urls = SanitizeUrls(turn.StunUrls, "stun:") });
        }

        if (turn.HasTurnEndpoints)
        {
            var sharedSecret = turn.ResolveSharedSecret();
            if (sharedSecret is not { Length: >= 32 })
            {
                throw new InvalidOperationException(
                    "TURN endpoints are configured but the server-side shared secret is missing or too short.");
            }

            var expiresAt = timeProvider.GetUtcNow() + turn.CredentialLifetime;
            var expiresUnix = expiresAt.ToUnixTimeSeconds();
            var subject = OpaqueSubject(deviceId, sessionId);
            var username = $"{expiresUnix}:{subject}";

            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(sharedSecret));
            var credential = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));

            servers.Add(new IceServerDescriptor
            {
                Urls = SanitizeUrls(turn.TurnUrls, "turn:", "turns:"),
                Username = username,
                Credential = credential,
                ExpiresAt = expiresAt,
            });
        }

        return new IceServersMessage
        {
            SessionId = sessionId,
            Servers = servers,
            RelayServerId = turn.HasTurnEndpoints ? turn.ServerId : null,
            RelayRegion = turn.HasTurnEndpoints ? turn.Region : null,
            RelayOnly = turn.RelayOnly,
        };
    }

    private static string OpaqueSubject(PeerOnQId deviceId, string sessionId)
    {
        var material = Encoding.UTF8.GetBytes($"{deviceId.Value}:{sessionId}");
        var digest = SHA256.HashData(material);
        return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static string[] SanitizeUrls(IEnumerable<string> urls, params string[] allowedPrefixes)
    {
        var sanitized = urls
            .Select(url => url.Trim())
            .Where(url => allowedPrefixes.Any(prefix => url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sanitized.Length == 0)
        {
            throw new InvalidOperationException("No valid STUN/TURN URLs are configured.");
        }

        return sanitized;
    }
}
