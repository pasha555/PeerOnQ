namespace PeerOnQ.Cloud.Application.Services;

internal static class ClientRegistrationValidation
{
    public static void ValidateProtocol(CloudSecurityOptions options, string protocolVersion)
    {
        if (string.IsNullOrWhiteSpace(protocolVersion) || protocolVersion.Length > 32 ||
            !options.SupportedProtocolVersions.Contains(protocolVersion, StringComparer.Ordinal))
        {
            throw new CloudServiceException(CloudErrorCodes.UnsupportedProtocol,
                "The client protocol version is unsupported.");
        }
    }

    public static string Normalize(string? value, int maximumLength, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maximumLength)
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest,
                $"{fieldName} must contain 1 to {maximumLength} characters.");
        return normalized;
    }

    public static void EnsureSupportedVersion(string appVersion, string? minimumVersion)
    {
        var current = ParseVersion(appVersion);
        if (minimumVersion is not null && current < ParseVersion(minimumVersion))
            throw new CloudServiceException(CloudErrorCodes.UnsupportedVersion,
                "The client version is no longer supported.");
    }

    private static Version ParseVersion(string? value)
    {
        var core = value?.Split(['-', '+'], 2)[0] ?? string.Empty;
        if (!Version.TryParse(core, out var version))
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest,
                "Application version is invalid.");
        return version;
    }
}
