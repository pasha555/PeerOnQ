namespace PeerOnQ.Cloud.Domain.Entities;

public sealed class AppRelease
{
    private AppRelease() { }
    public AppRelease(Guid id, string version, InstallChannel channel, ArchitectureKind architecture, DateTimeOffset publishedAtUtc, string minimumSupportedVersion, string securityFloorVersion, int rolloutPercentage, string signedManifestDigest, Uri artifactUri, long artifactSizeBytes, string artifactSha256)
    {
        if (id == Guid.Empty) throw new ArgumentException("Release ID is required.", nameof(id));
        if (rolloutPercentage is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(rolloutPercentage));
        Id = id;
        Version = Device.NormalizeRequired(version, 64, nameof(version));
        Channel = channel;
        Architecture = architecture;
        PublishedAtUtc = publishedAtUtc;
        MinimumSupportedVersion = Device.NormalizeRequired(minimumSupportedVersion, 64, nameof(minimumSupportedVersion));
        SecurityFloorVersion = Device.NormalizeRequired(securityFloorVersion, 64, nameof(securityFloorVersion));
        SignedManifestDigest = Device.NormalizeHex(signedManifestDigest, 64, nameof(signedManifestDigest));
        if (!artifactUri.IsAbsoluteUri || artifactUri.Scheme is not ("https" or "http")) throw new ArgumentException("Artifact URI must be an absolute HTTP(S) URI.", nameof(artifactUri));
        if (artifactSizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(artifactSizeBytes));
        ArtifactUri = artifactUri.AbsoluteUri;
        ArtifactSizeBytes = artifactSizeBytes;
        ArtifactSha256 = Device.NormalizeHex(artifactSha256, 64, nameof(artifactSha256));
        RolloutPercentage = rolloutPercentage;
        IsActive = true;
        ConcurrencyVersion = 1;
    }
    public Guid Id { get; private set; }
    public string Version { get; private set; } = string.Empty;
    public InstallChannel Channel { get; private set; }
    public ArchitectureKind Architecture { get; private set; }
    public DateTimeOffset PublishedAtUtc { get; private set; }
    public string MinimumSupportedVersion { get; private set; } = string.Empty;
    public string SecurityFloorVersion { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public int RolloutPercentage { get; private set; }
    public string SignedManifestDigest { get; private set; } = string.Empty;
    public string ArtifactUri { get; private set; } = string.Empty;
    public long ArtifactSizeBytes { get; private set; }
    public string ArtifactSha256 { get; private set; } = string.Empty;
    public long ConcurrencyVersion { get; private set; }

    public void ChangeRollout(int percentage)
    {
        if (percentage is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentage));
        RolloutPercentage = percentage;
        ConcurrencyVersion++;
    }
    public void ReplaceSignedManifest(string signedManifestDigest, int rolloutPercentage)
    {
        SignedManifestDigest = Device.NormalizeHex(signedManifestDigest, 64, nameof(signedManifestDigest));
        ChangeRollout(rolloutPercentage);
    }
    public void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        ConcurrencyVersion++;
    }
    public void Deactivate() { IsActive = false; ConcurrencyVersion++; }
}

public sealed class UpdateEvent
{
    private UpdateEvent() { }
    public UpdateEvent(Guid id, Guid installationId, Guid releaseId, UpdateEventKind kind, string? failureCode, DateTimeOffset occurredAtUtc)
    {
        if (id == Guid.Empty || installationId == Guid.Empty || releaseId == Guid.Empty) throw new ArgumentException("Event, installation, and release IDs are required.");
        if (kind != UpdateEventKind.Failed && !string.IsNullOrWhiteSpace(failureCode)) throw new ArgumentException("Failure code is only valid for failed updates.", nameof(failureCode));
        Id = id;
        InstallationId = installationId;
        ReleaseId = releaseId;
        Kind = kind;
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim()[..Math.Min(128, failureCode.Trim().Length)];
        OccurredAtUtc = occurredAtUtc;
    }
    public Guid Id { get; private set; }
    public Guid InstallationId { get; private set; }
    public Guid ReleaseId { get; private set; }
    public UpdateEventKind Kind { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
}

public sealed class DiagnosticBundle
{
    private DiagnosticBundle() { }
    private DiagnosticBundle(Guid id, Guid installationId, byte[] uploadTokenHash, DateTimeOffset uploadTokenExpiresAtUtc, DateTimeOffset expiresAtUtc, string appVersion, string osVersion, ArchitectureKind architecture, string? errorId, string? issueCategory, int databaseSchemaVersion, DateTimeOffset now)
    {
        Id = id;
        InstallationId = installationId;
        UploadTokenHash = uploadTokenHash.ToArray();
        UploadTokenExpiresAtUtc = uploadTokenExpiresAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        AppVersion = appVersion;
        OsVersion = osVersion;
        Architecture = architecture;
        ErrorId = errorId;
        IssueCategory = issueCategory;
        DatabaseSchemaVersion = databaseSchemaVersion;
        ConsentGranted = true;
        ConsentGrantedAtUtc = now;
        CreatedAtUtc = now;
        Status = DiagnosticStatus.AwaitingUpload;
        ConcurrencyVersion = 1;
    }

    public Guid Id { get; private set; }
    public Guid InstallationId { get; private set; }
    public bool ConsentGranted { get; private set; }
    public DateTimeOffset ConsentGrantedAtUtc { get; private set; }
    public byte[] UploadTokenHash { get; private set; } = [];
    public DateTimeOffset UploadTokenExpiresAtUtc { get; private set; }
    public DiagnosticStatus Status { get; private set; }
    public string AppVersion { get; private set; } = string.Empty;
    public string OsVersion { get; private set; } = string.Empty;
    public ArchitectureKind Architecture { get; private set; }
    public string? ErrorId { get; private set; }
    public string? IssueCategory { get; private set; }
    public int DatabaseSchemaVersion { get; private set; }
    public string? StorageObjectKey { get; private set; }
    public long? SanitizedArchiveSizeBytes { get; private set; }
    public string? ArchiveSha256 { get; private set; }
    public string? ReferenceCode { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public long ConcurrencyVersion { get; private set; }

    public static DiagnosticBundle Create(Guid installationId, byte[] uploadTokenHash, DateTimeOffset tokenExpiry, DateTimeOffset expiry, string appVersion, string osVersion, ArchitectureKind architecture, string? errorId, string? issueCategory, int databaseSchemaVersion, DateTimeOffset now)
    {
        if (installationId == Guid.Empty) throw new ArgumentException("Installation ID is required.", nameof(installationId));
        if (uploadTokenHash is not { Length: 32 }) throw new ArgumentException("Upload token hash must contain 32 bytes.", nameof(uploadTokenHash));
        if (tokenExpiry <= now || expiry <= tokenExpiry) throw new ArgumentOutOfRangeException(nameof(expiry));
        if (databaseSchemaVersion < 0) throw new ArgumentOutOfRangeException(nameof(databaseSchemaVersion));
        return new DiagnosticBundle(Guid.NewGuid(), installationId, uploadTokenHash, tokenExpiry, expiry,
            Device.NormalizeRequired(appVersion, 64, nameof(appVersion)), Device.NormalizeRequired(osVersion, 128, nameof(osVersion)), architecture,
            Normalize(errorId, 64), Normalize(issueCategory, 64), databaseSchemaVersion, now);
    }

    public bool TokenMatches(byte[] candidateHash, DateTimeOffset now) =>
        Status == DiagnosticStatus.AwaitingUpload && now < UploadTokenExpiresAtUtc &&
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(UploadTokenHash, candidateHash);

    public void MarkUploaded(byte[] candidateHash, long archiveSize, string sha256, string storageObjectKey, string referenceCode, DateTimeOffset now)
    {
        if (!TokenMatches(candidateHash, now)) throw new InvalidOperationException("Upload authorization is invalid or expired.");
        if (archiveSize is <= 0 or > 100 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(archiveSize));
        SanitizedArchiveSizeBytes = archiveSize;
        ArchiveSha256 = Device.NormalizeHex(sha256, 64, nameof(sha256));
        StorageObjectKey = Device.NormalizeRequired(storageObjectKey, 512, nameof(storageObjectKey));
        ReferenceCode = Device.NormalizeRequired(referenceCode, 32, nameof(referenceCode));
        UploadTokenHash = [];
        Status = DiagnosticStatus.Uploaded;
        ConcurrencyVersion++;
    }

    public void ClaimExpiration(DateTimeOffset now)
    {
        if (now < ExpiresAtUtc) throw new InvalidOperationException("Diagnostic retention has not expired.");
        if (Status is DiagnosticStatus.Expired or DiagnosticStatus.Deleted) return;
        if (Status == DiagnosticStatus.Deleting) return;
        Status = DiagnosticStatus.Deleting;
        UploadTokenHash = [];
        ConcurrencyVersion++;
    }

    public void CompleteExpiration(DateTimeOffset now)
    {
        if (now < ExpiresAtUtc || Status != DiagnosticStatus.Deleting)
            throw new InvalidOperationException("Diagnostic expiration was not claimed.");
        Status = DiagnosticStatus.Expired;
        StorageObjectKey = null;
        UploadTokenHash = [];
        ConcurrencyVersion++;
    }

    private static string? Normalize(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(max, value.Trim().Length)];
}

public sealed class DiagnosticAccessEvent
{
    private DiagnosticAccessEvent() { }
    public DiagnosticAccessEvent(Guid id, Guid diagnosticId, Guid adminUserId, string action, DateTimeOffset occurredAtUtc)
    {
        Id = id;
        DiagnosticId = diagnosticId;
        AdminUserId = adminUserId;
        Action = Device.NormalizeRequired(action, 64, nameof(action));
        OccurredAtUtc = occurredAtUtc;
    }
    public Guid Id { get; private set; }
    public Guid DiagnosticId { get; private set; }
    public Guid AdminUserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }
}
