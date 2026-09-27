using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeerOnQ.Infrastructure.Configuration;

public enum SignalingEndpointSource
{
    CompiledDefault,
    LocalSettings,
    Environment,
}

public sealed record ResolvedSignalingEndpoint(Uri Uri, SignalingEndpointSource Source);

/// <summary>
/// Resolves and atomically persists the non-secret signaling endpoint. Environment configuration
/// intentionally wins so managed deployments can enforce one endpoint without rewriting user data.
/// </summary>
public sealed class SignalingEndpointStore(
    PeerOnQPaths paths,
    string compiledDefault,
    bool allowDevelopmentOverrides = true)
{
    public const string EnvironmentVariableName = "PEERONQ_SIGNALING_URL";
    public const int MaxSerializedBytes = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public string FilePath => paths.ConnectionSettingsFile;

    public bool AllowsLocalOverrides => allowDevelopmentOverrides;

    public bool IsEnvironmentOverride =>
        allowDevelopmentOverrides && GetEnvironmentValue() is not null;

    public bool IsManaged => !allowDevelopmentOverrides || IsEnvironmentOverride;

    public ResolvedSignalingEndpoint Resolve()
    {
        var environmentValue = allowDevelopmentOverrides ? GetEnvironmentValue() : null;
        if (environmentValue is not null)
        {
            return new ResolvedSignalingEndpoint(ParseAndNormalize(environmentValue), SignalingEndpointSource.Environment);
        }

        if (allowDevelopmentOverrides && File.Exists(FilePath))
        {
            var info = new FileInfo(FilePath);
            if (info.Length is <= 0 or > MaxSerializedBytes)
            {
                throw new InvalidOperationException("The saved signaling configuration has an invalid size.");
            }

            try
            {
                var payload = File.ReadAllBytes(FilePath);
                var settings = JsonSerializer.Deserialize<StoredSettings>(payload, JsonOptions)
                    ?? throw new InvalidOperationException("The saved signaling configuration is empty.");
                return new ResolvedSignalingEndpoint(
                    ParseAndNormalize(settings.SignalingUrl),
                    SignalingEndpointSource.LocalSettings);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("The saved signaling configuration is malformed.", ex);
            }
        }

        return new ResolvedSignalingEndpoint(
            ParseAndNormalize(compiledDefault),
            SignalingEndpointSource.CompiledDefault);
    }

    public async Task<Uri> SaveAsync(string value, CancellationToken cancellationToken = default)
    {
        if (!allowDevelopmentOverrides)
        {
            throw new InvalidOperationException(
                "The signaling server is fixed by this release and cannot be changed in the app.");
        }

        if (IsEnvironmentOverride)
        {
            throw new InvalidOperationException(
                $"The signaling server is managed by {EnvironmentVariableName} and cannot be changed in the app.");
        }

        var endpoint = ParseAndNormalize(value);
        paths.EnsureCreated();

        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new StoredSettings(endpoint.AbsoluteUri),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return endpoint;
    }

    public static Uri ParseAndNormalize(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 2048)
        {
            throw new ArgumentException("Enter a signaling server URL shorter than 2048 characters.", nameof(value));
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("ws" or "wss")
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new ArgumentException("Enter an absolute ws:// or wss:// signaling server URL.", nameof(value));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("The signaling URL cannot contain credentials, a query, or a fragment.", nameof(value));
        }

        if (endpoint.Scheme == "ws" && !IsLoopback(endpoint))
        {
            throw new ArgumentException("Remote signaling servers must use encrypted wss:// transport.", nameof(value));
        }

        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.Length == 0) path = "/ws";
        if (!path.Equals("/ws", StringComparison.Ordinal))
        {
            throw new ArgumentException("The signaling server URL path must be /ws.", nameof(value));
        }

        return new UriBuilder(endpoint) { Path = "/ws" }.Uri;
    }

    private static bool IsLoopback(Uri endpoint) =>
        endpoint.IsLoopback || endpoint.Host is "localhost" or "127.0.0.1" or "::1";

    private static string? GetEnvironmentValue()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record StoredSettings(string SignalingUrl);
}
