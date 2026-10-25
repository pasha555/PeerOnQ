using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace PeerOnQ.Signaling.Server;

/// <summary>Rejects unsafe or incomplete production signaling configuration before listening.</summary>
public sealed class SignalingOptionsValidator(IHostEnvironment environment) : IValidateOptions<SignalingOptions>
{
    public ValidateOptionsResult Validate(string? name, SignalingOptions options)
    {
        var failures = new List<string>();

        if (options.MaxMessageBytes is < 1024 or > 65_536)
            failures.Add("Signaling:MaxMessageBytes must be between 1024 and 65536.");
        if (options.MaxPendingSendsPerConnection is < 1 or > 1024)
            failures.Add("Signaling:MaxPendingSendsPerConnection must be between 1 and 1024.");
        if (options.MaxConcurrentSessionsPerDevice is < 1 or > 1024)
            failures.Add("Signaling:MaxConcurrentSessionsPerDevice must be between 1 and 1024.");
        if (options.MessagesPerSecond <= 0 || options.MessageBurst < options.MessagesPerSecond)
            failures.Add("Signaling message rate and burst limits must be positive and internally consistent.");
        if (options.HeartbeatInterval <= TimeSpan.Zero
            || options.HeartbeatTimeout <= options.HeartbeatInterval)
            failures.Add("Signaling heartbeat timeout must be greater than its positive interval.");
        if (options.SweepInterval <= TimeSpan.Zero || options.SweepInterval >= options.HeartbeatTimeout)
            failures.Add("Signaling sweep interval must be positive and shorter than the heartbeat timeout.");
        if (options.DisconnectGracePeriod <= TimeSpan.Zero
            || options.ResumeTokenLifetime < options.DisconnectGracePeriod)
            failures.Add("ResumeTokenLifetime must cover the positive DisconnectGracePeriod.");
        ValidateCluster(options.Cluster, failures);
        if (options.Turn.CredentialLifetime <= TimeSpan.Zero)
            failures.Add("TURN credential lifetime must be positive.");
        if (options.Turn.StunUrls.Any(url => !url.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)))
            failures.Add("Every STUN URL must use the stun: scheme.");
        if (options.Turn.TurnUrls.Any(url =>
                !url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)))
            failures.Add("Every TURN URL must use the turn: or turns: scheme.");
        if (!string.IsNullOrWhiteSpace(options.TrustedProxyIp)
            && !IPAddress.TryParse(options.TrustedProxyIp, out _))
            failures.Add("Signaling:TrustedProxyIp must be one exact IP address.");
        if (options.Turn.HasTurnEndpoints && !options.Turn.HasUsableSharedSecret)
            failures.Add("Configured TURN endpoints require at least 32 base64-compatible secret characters.");

        ValidateAttestation(options.Attestation, failures);

        var protectedEnvironment = environment.IsProduction() || environment.IsStaging();
        if (protectedEnvironment && (!options.Attestation.Required || options.Attestation.AllowDevelopmentTofuFallback))
            failures.Add("Production and Staging signaling require cloud attestation and prohibit TOFU fallback.");
        if (!options.Attestation.Required
            && (!(environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                || !options.Attestation.AllowDevelopmentTofuFallback))
            failures.Add("TOFU fallback must be explicitly enabled and is restricted to Development and Testing.");

        if (environment.IsProduction())
        {
            if (!options.RequireTlsOutsideLoopback)
                failures.Add("Production signaling must require TLS outside loopback.");
            if (string.IsNullOrWhiteSpace(options.PinStorePath))
                failures.Add("Production signaling requires a durable device pin store path.");
            if (options.Turn.StunUrls.Length == 0)
                failures.Add("Production signaling requires at least one STUN URL.");
            if (!options.Turn.HasTurnEndpoints)
                failures.Add("Production signaling requires TURN endpoints.");
            if (!options.Turn.HasUsableSharedSecret)
                failures.Add("Production signaling requires a readable TURN secret.");
            if (options.ResumeTokenLifetime > TimeSpan.FromMinutes(5)
                || options.DisconnectGracePeriod > TimeSpan.FromMinutes(2))
                failures.Add("Production resume and disconnect windows must remain short-lived.");
            if (options.Turn.CredentialLifetime > TimeSpan.FromHours(1))
                failures.Add("Production TURN credentials may not live longer than one hour.");
            if (IsPlaceholder(options.Turn.Realm))
                failures.Add("Production TURN realm must be a real deployment domain.");
            if (string.IsNullOrWhiteSpace(options.Turn.ServerId) || options.Turn.ServerId == "turn-1")
                failures.Add("Production TURN server identifier must be deployment-specific.");
            if (string.IsNullOrWhiteSpace(options.Turn.Region)
                || options.Turn.Region is "development" or "local")
                failures.Add("Production TURN region must be deployment-specific.");
            if (!options.Turn.TurnUrls.Any(IsUdpTurn))
                failures.Add("Production TURN configuration requires a UDP endpoint.");
            if (!options.Turn.TurnUrls.Any(IsTcpTurn))
                failures.Add("Production TURN configuration requires a TCP fallback endpoint.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateAttestation(SignalingAttestationOptions attestation, List<string> failures)
    {
        if (!attestation.Required) return;

        if (!Uri.TryCreate(attestation.Issuer, UriKind.Absolute, out var issuer)
            || issuer.Scheme != Uri.UriSchemeHttps || attestation.Issuer.Length > 256
            || !string.IsNullOrEmpty(issuer.UserInfo) || !string.IsNullOrEmpty(issuer.Query)
            || !string.IsNullOrEmpty(issuer.Fragment))
            failures.Add("Signaling:Attestation:Issuer must be an absolute HTTPS origin.");
        if (string.IsNullOrWhiteSpace(attestation.Audience) || attestation.Audience.Length > 128
            || attestation.Audience.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '/' or '-')))
            failures.Add("Signaling:Attestation:Audience is invalid.");
        if (attestation.MaximumTokenLifetime < TimeSpan.FromMinutes(1)
            || attestation.MaximumTokenLifetime > TimeSpan.FromMinutes(15))
            failures.Add("Signaling:Attestation:MaximumTokenLifetime must be between one and fifteen minutes.");
        if (attestation.ClockSkew < TimeSpan.Zero || attestation.ClockSkew > TimeSpan.FromMinutes(2))
            failures.Add("Signaling:Attestation:ClockSkew must be between zero and two minutes.");
        if (attestation.PublicKeyFiles.Length is < 1 or > 3
            || attestation.PublicKeyFiles.Distinct(StringComparer.Ordinal).Count() != attestation.PublicKeyFiles.Length
            || attestation.PublicKeyFiles.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            || !Security.CloudSignalingAttestationValidator.CanLoadPublicKeys(attestation.PublicKeyFiles))
            failures.Add("Signaling attestation requires one to three distinct readable ECDSA P-256 public-key secret files.");
    }

    private static void ValidateCluster(SignalingClusterOptions cluster, List<string> failures)
    {
        if (!cluster.Enabled) return;

        if (string.IsNullOrWhiteSpace(cluster.InstanceId)
            || cluster.InstanceId.Length > 64
            || cluster.InstanceId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            failures.Add("Signaling:Cluster:InstanceId must be a 1-64 character deployment-unique identifier.");
        if (string.IsNullOrWhiteSpace(cluster.RedisConnectionString))
            failures.Add("Signaling:Cluster:RedisConnectionString is required when clustering is enabled.");
        if (string.IsNullOrWhiteSpace(cluster.KeyPrefix)
            || cluster.KeyPrefix.Length > 96
            || !cluster.KeyPrefix.Contains("{signaling}", StringComparison.Ordinal))
            failures.Add("Signaling:Cluster:KeyPrefix must be bounded and contain the {signaling} Redis hash tag.");
        if (cluster.DeviceLeaseDuration < TimeSpan.FromSeconds(15)
            || cluster.DeviceLeaseDuration > TimeSpan.FromMinutes(5))
            failures.Add("Signaling:Cluster:DeviceLeaseDuration must be between 15 seconds and 5 minutes.");
        if (cluster.DeviceLeaseRefreshInterval < TimeSpan.FromSeconds(5)
            || cluster.DeviceLeaseRefreshInterval * 2 >= cluster.DeviceLeaseDuration)
            failures.Add("Signaling:Cluster:DeviceLeaseRefreshInterval must be at least 5 seconds and less than half the lease duration.");
    }

    private static bool IsPlaceholder(string realm) =>
        string.IsNullOrWhiteSpace(realm)
        || realm.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase)
        || realm.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
        || realm.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
        || realm.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
        || string.Equals(realm, "example.com", StringComparison.OrdinalIgnoreCase)
        || realm.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(realm, "localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsUdpTurn(string url) =>
        url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
        && url.Contains("transport=udp", StringComparison.OrdinalIgnoreCase);

    private static bool IsTcpTurn(string url) =>
        (url.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
         || url.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
        && url.Contains("transport=tcp", StringComparison.OrdinalIgnoreCase);
}
