namespace PeerOnQ.Signaling.Server;

public sealed class SignalingOptions
{
    public string ServerId { get; set; } = Environment.MachineName;

    /// <summary>How long a registration challenge stays valid.</summary>
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Connection tokens are deliberately short lived; clients re-register on expiry.</summary>
    public TimeSpan ConnectionTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A device that sends nothing for this long is dropped as abandoned.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the sharer has to answer a permission request.</summary>
    public TimeSpan PermissionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A session that never reaches media within this window is abandoned.</summary>
    public TimeSpan NegotiationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Replay window for session-request nonces.</summary>
    public TimeSpan NonceLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Rejects a session request whose client timestamp is too far from server time.</summary>
    public TimeSpan MaxRequestClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    public int MessagesPerSecond { get; set; } = 25;
    public int MessageBurst { get; set; } = 60;

    /// <summary>Hard application limit, additionally capped by the protocol's 64 KB ceiling.</summary>
    public int MaxMessageBytes { get; set; } = 64 * 1024;

    /// <summary>Maximum writes waiting behind one slow WebSocket before it is disconnected.</summary>
    public int MaxPendingSendsPerConnection { get; set; } = 128;

    /// <summary>Operational abuse bound, not a commercial entitlement.</summary>
    public int MaxConcurrentSessionsPerDevice { get; set; } = 64;

    /// <summary>Interval of the janitor that expires sessions and dead connections.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Where pinned device public keys are stored. Empty keeps them in memory only, which
    /// means every device re-pins after a restart.
    /// </summary>
    public string? PinStorePath { get; set; } = "data/device-pins.json";

    /// <summary>
    /// Refuse to serve non-loopback traffic over plain HTTP. Leave this on outside development:
    /// signaling carries session setup and must not be readable on the wire.
    /// </summary>
    public bool RequireTlsOutsideLoopback { get; set; } = true;

    /// <summary>
    /// Exact address of the TLS-terminating reverse proxy whose X-Forwarded-For/Proto headers may
    /// be trusted. Empty means forwarded headers are ignored.
    /// </summary>
    public string? TrustedProxyIp { get; set; }

    /// <summary>Short window in which a reauthenticated device may reclaim its session.</summary>
    public TimeSpan DisconnectGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ResumeTokenLifetime { get; set; } = TimeSpan.FromMinutes(2);

    public SignalingClusterOptions Cluster { get; set; } = new();
    public SignalingAttestationOptions Attestation { get; set; } = new();
    public TurnOptions Turn { get; set; } = new();
}

public sealed class SignalingClusterOptions
{
    /// <summary>Enables Redis-backed device routing and session ownership across signaling nodes.</summary>
    public bool Enabled { get; set; }

    /// <summary>Unique, stable identifier for this signaling process in one deployment.</summary>
    public string InstanceId { get; set; } = Environment.MachineName;

    /// <summary>Injected through protected configuration; never written to logs.</summary>
    public string RedisConnectionString { get; set; } = string.Empty;

    /// <summary>Redis hash-tagged prefix keeps atomic session keys in one cluster slot.</summary>
    public string KeyPrefix { get; set; } = "peeronq:{signaling}";

    /// <summary>Hard-crashed device ownership disappears after this interval.</summary>
    public TimeSpan DeviceLeaseDuration { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How often live local connections renew their distributed ownership.</summary>
    public TimeSpan DeviceLeaseRefreshInterval { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed class SignalingAttestationOptions
{
    /// <summary>Required by default. Only explicit Development/Testing may opt into TOFU.</summary>
    public bool Required { get; set; } = true;
    public bool AllowDevelopmentTofuFallback { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = "peeronq-signaling";
    public TimeSpan MaximumTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
    public string[] PublicKeyFiles { get; set; } = [];
}

public sealed class TurnOptions
{
    public string Realm { get; set; } = "turn.example.com";
    public string ServerId { get; set; } = "turn-1";
    public string Region { get; set; } = "development";
    public string[] StunUrls { get; set; } = [];
    public string[] TurnUrls { get; set; } = [];

    /// <summary>
    /// Coturn REST shared secret. Supply only through a secret store/environment variable;
    /// never place it in appsettings or a client message.
    /// </summary>
    public string? SharedSecret { get; set; }

    /// <summary>Preferred production source: a Docker/Kubernetes secret file mounted read-only.</summary>
    public string? SharedSecretFile { get; set; }

    public TimeSpan CredentialLifetime { get; set; } = TimeSpan.FromMinutes(10);
    public bool RelayOnly { get; set; }

    public bool HasTurnEndpoints => TurnUrls.Length > 0;
    public bool HasUsableSharedSecret => ResolveSharedSecret() is { Length: >= 32 } secret
        && secret.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '+' or '=' or '/' or '-');

    public string? ResolveSharedSecret()
    {
        if (!string.IsNullOrWhiteSpace(SharedSecret)) return SharedSecret.Trim();
        if (string.IsNullOrWhiteSpace(SharedSecretFile)) return null;

        try
        {
            return File.ReadAllText(SharedSecretFile).Trim();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            return null;
        }
    }
}
