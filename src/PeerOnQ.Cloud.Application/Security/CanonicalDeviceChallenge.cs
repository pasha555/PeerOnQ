using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Security;

public static class CanonicalDeviceChallenge
{
    public static (string ChallengeId, string Payload) Create(
        string audience,
        string purpose,
        Guid installationId,
        string identityFingerprint,
        string displayName,
        PlatformKindV1 platform,
        ArchitectureKindV1 architecture,
        string appVersion,
        string osVersion,
        InstallChannelV1 installChannel,
        string region,
        string protocolVersion,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var challengeId = Base64Url(RandomNumberGenerator.GetBytes(24));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var payload = string.Join('\n',
            "peeronq-device-auth-v1",
            $"audience={audience}",
            $"purpose={purpose}",
            $"challenge_id={challengeId}",
            $"installation_id={installationId:N}",
            $"key_fingerprint={identityFingerprint}",
            $"display_name_sha256={Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(displayName)))}",
            $"platform={platform}",
            $"architecture={architecture}",
            $"app_version_sha256={Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(appVersion)))}",
            $"os_version_sha256={Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(osVersion)))}",
            $"install_channel={installChannel}",
            $"region_sha256={Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(region)))}",
            $"nonce={nonce}",
            $"issued_at={issuedAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
            $"expires_at={expiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
            $"protocol={protocolVersion}");
        return (challengeId, payload);
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
