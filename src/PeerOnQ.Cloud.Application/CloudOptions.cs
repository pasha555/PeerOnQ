namespace PeerOnQ.Cloud.Application;

public sealed class CloudSecurityOptions
{
    public string ChallengeAudience { get; init; } = "peeronq-cloud";
    public string ChallengePurpose { get; init; } = "device-authentication";
    public string[] SupportedProtocolVersions { get; init; } = ["1"];
    // Keep a margin below the v1 client's two-minute acceptance ceiling so normal server/client
    // clock and network skew cannot make a freshly issued challenge appear invalid.
    public TimeSpan ChallengeLifetime { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan DeviceAccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan MaximumClientClockSkew { get; init; } = TimeSpan.FromMinutes(5);
    public int HeartbeatIntervalSeconds { get; init; } = 25;
    public TimeSpan DiagnosticUploadTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public long MaximumDiagnosticArchiveBytes { get; init; } = 25 * 1024 * 1024;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ChallengeAudience) || string.IsNullOrWhiteSpace(ChallengePurpose))
            throw new InvalidOperationException("Challenge audience and purpose are required.");
        if (SupportedProtocolVersions.Length == 0 || SupportedProtocolVersions.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("At least one supported protocol version is required.");
        if (ChallengeLifetime <= TimeSpan.Zero || DeviceAccessTokenLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("Security lifetimes must be positive.");
        if (MaximumClientClockSkew <= TimeSpan.Zero || MaximumClientClockSkew > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("Maximum client clock skew must be between zero and 15 minutes.");
        if (HeartbeatIntervalSeconds is < 10 or > 120)
            throw new InvalidOperationException("Heartbeat interval must be between 10 and 120 seconds.");
        if (DiagnosticUploadTokenLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("Diagnostic upload-token lifetime must be positive.");
        if (MaximumDiagnosticArchiveBytes is <= 0 or > 100 * 1024 * 1024)
            throw new InvalidOperationException("Diagnostic archive limit is outside the supported range.");
    }
}

public sealed class SignalingAttestationIssuerOptions
{
    public const string SectionName = "PeerOnQ:SignalingAttestation";

    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = "peeronq-signaling";
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);
    public string? PrivateKeyFile { get; init; }

    public void Validate()
    {
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer)
            || issuer.Scheme != Uri.UriSchemeHttps || Issuer.Length > 256
            || !string.IsNullOrEmpty(issuer.UserInfo) || !string.IsNullOrEmpty(issuer.Query)
            || !string.IsNullOrEmpty(issuer.Fragment))
            throw new InvalidOperationException("The signaling attestation issuer must be an absolute HTTPS origin.");
        if (string.IsNullOrWhiteSpace(Audience) || Audience.Length > 128
            || Audience.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '/' or '-')))
            throw new InvalidOperationException("The signaling attestation audience is invalid.");
        if (Lifetime < TimeSpan.FromMinutes(1) || Lifetime > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("The signaling attestation lifetime must be between one and fifteen minutes.");
        if (string.IsNullOrWhiteSpace(PrivateKeyFile) || !Path.IsPathRooted(PrivateKeyFile))
            throw new InvalidOperationException("The signaling attestation private-key file must be an absolute secret path.");
    }
}

public sealed class RetentionOptions
{
    public TimeSpan PresenceHistory { get; init; } = TimeSpan.FromDays(90);
    public TimeSpan SessionMetadata { get; init; } = TimeSpan.FromDays(180);
    public TimeSpan DownloadEvents { get; init; } = TimeSpan.FromDays(395);
    /// <summary>Applied prospectively to each diagnostic bundle's immutable expiry timestamp.</summary>
    public TimeSpan Diagnostics { get; init; } = TimeSpan.FromDays(14);
    public TimeSpan ServiceHealthSnapshots { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan AuditEvents { get; init; } = TimeSpan.FromDays(365);
    public TimeSpan AlertEvents { get; init; } = TimeSpan.FromDays(365);
    public TimeSpan CompletedAdminSessions { get; init; } = TimeSpan.FromDays(90);
    public int BatchSize { get; init; } = 500;
    public string PolicyVersion { get; init; } = "phase6-v1";
    public bool AuditEventsEnabled { get; init; }
    public bool AuditEventsLegalHold { get; init; }
    public bool AlertEventsEnabled { get; init; } = true;
    public bool AlertEventsLegalHold { get; init; }
    public bool DiagnosticsLegalHold { get; init; }

    public void Validate(TimeSpan? diagnosticUploadTokenLifetime = null)
    {
        if (BatchSize is < 1 or > 5000) throw new InvalidOperationException("Retention batch size must be between 1 and 5000.");
        if (string.IsNullOrWhiteSpace(PolicyVersion) || PolicyVersion.Length > 64 ||
            PolicyVersion.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new InvalidOperationException("Retention policy version is invalid.");
        if (PresenceHistory <= TimeSpan.Zero || SessionMetadata <= TimeSpan.Zero || DownloadEvents <= TimeSpan.Zero ||
            Diagnostics <= TimeSpan.Zero || ServiceHealthSnapshots <= TimeSpan.Zero || CompletedAdminSessions <= TimeSpan.Zero)
            throw new InvalidOperationException("Retention durations must be positive.");
        if (AuditEvents < TimeSpan.FromDays(365))
            throw new InvalidOperationException("Audit-event retention cannot be shorter than 365 days.");
        if (AlertEvents < TimeSpan.FromDays(7))
            throw new InvalidOperationException("Alert-event retention cannot be shorter than 7 days.");
        if (diagnosticUploadTokenLifetime is { } tokenLifetime && Diagnostics <= tokenLifetime)
            throw new InvalidOperationException("Diagnostic retention must exceed the upload-token lifetime.");
    }
}
