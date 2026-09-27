using System.Reflection;

namespace PeerOnQ.Infrastructure.Configuration;

public enum PeerOnQDeploymentEnvironment
{
    Development = 0,
    Staging = 1,
    Production = 2,
}

public sealed record CloudServiceEndpoints
{
    public required PeerOnQDeploymentEnvironment Environment { get; init; }
    public required Uri ApiBaseUri { get; init; }
    public required Uri PresenceUri { get; init; }
    public required Uri SignalingUri { get; init; }
    public required Uri UpdatesBaseUri { get; init; }
    public required Uri DownloadsBaseUri { get; init; }
    public required Uri DiagnosticsBaseUri { get; init; }
    public required string Region { get; init; }
}

/// <summary>
/// Resolves non-secret public service endpoints. Official builds use only assembly metadata;
/// development builds may opt into environment overrides so local Docker acceptance remains
/// possible. No production UI can replace these endpoints.
/// </summary>
public static class CloudEndpointConfiguration
{
    private const string MetadataPrefix = "PeerOnQ";
    private const string EnvironmentPrefix = "PEERONQ_";

    public static CloudServiceEndpoints? TryCreate(
        Assembly assembly,
        bool allowDevelopmentEnvironmentOverrides)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(item => item.Key, item => item.Value ?? string.Empty, StringComparer.Ordinal);

        string Read(string metadataName, string environmentName)
        {
            if (metadata.TryGetValue(MetadataPrefix + metadataName, out var compiled)
                && !string.IsNullOrWhiteSpace(compiled))
            {
                return compiled.Trim();
            }

            return allowDevelopmentEnvironmentOverrides
                ? Environment.GetEnvironmentVariable(EnvironmentPrefix + environmentName)?.Trim() ?? string.Empty
                : string.Empty;
        }

        var api = Read("ApiBaseUrl", "API_BASE_URL");
        var presence = Read("PresenceUrl", "PRESENCE_URL");
        var signaling = Read("SignalingUrl", "SIGNALING_URL");
        var updates = Read("UpdatesBaseUrl", "UPDATES_BASE_URL");
        var downloads = Read("DownloadsBaseUrl", "DOWNLOADS_BASE_URL");
        var diagnostics = Read("DiagnosticsBaseUrl", "DIAGNOSTICS_BASE_URL");
        var environmentText = Read("DeploymentEnvironment", "DEPLOYMENT_ENVIRONMENT");
        var region = Read("Region", "REGION");

        var cloudAnchorValues = new[] { api, presence, downloads, diagnostics };
        if (cloudAnchorValues.All(string.IsNullOrWhiteSpace)) return null;

        var endpoints = new[] { api, presence, signaling, updates, downloads, diagnostics };
        var supplied = endpoints.Count(value => !string.IsNullOrWhiteSpace(value));
        if (supplied != endpoints.Length)
        {
            throw new InvalidOperationException(
                "The PeerOnQ cloud endpoint manifest is incomplete; cloud registration is disabled fail-closed.");
        }

        if (!Enum.TryParse<PeerOnQDeploymentEnvironment>(environmentText, ignoreCase: true, out var environment))
        {
            throw new InvalidOperationException(
                "PeerOnQDeploymentEnvironment must be Development, Staging, or Production.");
        }

        var allowLoopbackHttp = environment == PeerOnQDeploymentEnvironment.Development
                                && allowDevelopmentEnvironmentOverrides;

        return new CloudServiceEndpoints
        {
            Environment = environment,
            ApiBaseUri = ParseHttpEndpoint(api, nameof(api), allowLoopbackHttp),
            PresenceUri = ParseHttpEndpoint(presence, nameof(presence), allowLoopbackHttp),
            SignalingUri = ParseSignalingEndpoint(signaling, allowLoopbackHttp),
            UpdatesBaseUri = ParseHttpEndpoint(updates, nameof(updates), allowLoopbackHttp: false),
            DownloadsBaseUri = ParseHttpEndpoint(downloads, nameof(downloads), allowLoopbackHttp),
            DiagnosticsBaseUri = ParseHttpEndpoint(diagnostics, nameof(diagnostics), allowLoopbackHttp),
            Region = ValidateRegion(region),
        };
    }

    private static Uri ParseHttpEndpoint(string value, string name, bool allowLoopbackHttp)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || (uri.Scheme != Uri.UriSchemeHttps && !(allowLoopbackHttp && IsLoopbackHttp(uri))))
        {
            throw new InvalidOperationException($"{name} must be an absolute HTTPS URL.");
        }

        EnsureNoUserControlledParts(uri, name);
        return new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/" }.Uri;
    }

    private static Uri ParseSignalingEndpoint(string value, bool allowLoopbackHttp)
    {
        var uri = SignalingEndpointStore.ParseAndNormalize(value);
        if (uri.Scheme != "wss" && !(allowLoopbackHttp && uri.Scheme == "ws" && uri.IsLoopback))
        {
            throw new InvalidOperationException("PeerOnQSignalingUrl must use WSS outside loopback development.");
        }

        return uri;
    }

    private static void EnsureNoUserControlledParts(Uri uri, string name)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"{name} cannot contain credentials, a query, or a fragment.");
        }
    }

    private static bool IsLoopbackHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp
        && (uri.IsLoopback || uri.Host is "localhost" or "127.0.0.1" or "::1");

    private static string ValidateRegion(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length is < 2 or > 32
            || candidate.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidOperationException("PeerOnQRegion must be a 2-32 character deployment identifier.");
        }

        return candidate.ToLowerInvariant();
    }
}
