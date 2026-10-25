using System.Text.RegularExpressions;

namespace PeerOnQ.Observability;

public static partial class SensitiveDataRedactor
{
    private const string Redacted = "[REDACTED]";

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sanitized = AuthorizationRegex().Replace(value, "$1" + Redacted);
        sanitized = SecretAssignmentRegex().Replace(sanitized, "$1=" + Redacted);
        sanitized = DeviceIdRegex().Replace(sanitized, static match =>
        {
            var groups = match.Groups;
            var prefix = groups[1].Success ? groups[1].Value : string.Empty;
            return $"{prefix}{groups[2].Value}-***-***-{groups[5].Value}";
        });
        sanitized = EmailRegex().Replace(sanitized, static match => MaskEmail(match.Value));
        return sanitized.Length <= 4096 ? sanitized : sanitized[..4096];
    }

    private static string MaskEmail(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0) return Redacted;
        return $"{value[0]}***@{value[(at + 1)..]}";
    }

    [GeneratedRegex(@"(?i)(authorization\s*:\s*(?:bearer|basic)\s+)[^\s,;]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"(?i)\b(password|passwd|secret|token|private[_-]?key|recovery[_-]?code|cookie)\s*=\s*[^\s,;&]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"(?i)\b(LNK-)?(\d{3})-(\d{3})-(\d{3})-(\d{3})\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex DeviceIdRegex();

    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex EmailRegex();
}
