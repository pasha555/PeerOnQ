using System.Net;

namespace PeerOnQ.Downloads.Service;

public static class DownloadRequestPrivacy
{
    public static string? UniquenessScope(HttpContext context, TimeProvider timeProvider)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null) return null;
        var network = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? string.Join('.', address.GetAddressBytes().Take(3))
            : Convert.ToHexString(address.GetAddressBytes().Take(7).ToArray());
        return $"{timeProvider.GetUtcNow():yyyy-MM-dd}|{network}|{UserAgentFamily(context.Request.Headers.UserAgent)}";
    }

    public static string UserAgentFamily(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Unknown";
        var value = userAgent.Trim();
        var known = new[] { "PeerOnQ", "Edg", "Chrome", "Firefox", "Safari", "curl", "Wget" };
        return known.FirstOrDefault(item => value.Contains(item, StringComparison.OrdinalIgnoreCase)) ?? "Other";
    }

    public static string NormalizeSource(string? source)
    {
        var value = string.IsNullOrWhiteSpace(source) ? "direct" : source.Trim().ToLowerInvariant();
        return value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : "other";
    }

    public static string? NormalizeCampaign(string? campaign)
    {
        if (string.IsNullOrWhiteSpace(campaign)) return null;
        var value = campaign.Trim();
        return value.Length <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            ? value
            : null;
    }
}
