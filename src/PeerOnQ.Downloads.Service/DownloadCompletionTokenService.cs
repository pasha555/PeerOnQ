using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace PeerOnQ.Downloads.Service;

public sealed class DownloadCompletionTokenService(IOptions<DownloadsOptions> options, TimeProvider timeProvider)
{
    public string Issue(Guid downloadId)
    {
        var expires = timeProvider.GetUtcNow().AddMinutes(options.Value.CompletionTokenMinutes).ToUnixTimeSeconds();
        var payload = $"{downloadId:N}.{expires.ToString(CultureInfo.InvariantCulture)}";
        var signature = Sign(payload);
        return $"{payload}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    public bool Validate(Guid downloadId, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 512) return false;
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3
            || !Guid.TryParseExact(parts[0], "N", out var tokenId)
            || tokenId != downloadId
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || expires < timeProvider.GetUtcNow().ToUnixTimeSeconds())
            return false;
        byte[] supplied;
        try { supplied = WebEncoders.Base64UrlDecode(parts[2]); }
        catch (FormatException) { return false; }
        var expected = Sign($"{parts[0]}.{parts[1]}");
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private byte[] Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.Value.CompletionTokenKey!));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }
}
