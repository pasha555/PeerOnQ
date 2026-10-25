using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Domain.Collaboration;

public enum DeviceTrustState
{
    PendingApproval = 0,
    Trusted = 1,
    Revoked = 2,
    Expired = 3,
    FingerprintChanged = 4,
}

public sealed record TrustedDevice
{
    public required Guid RecordId { get; init; }
    public required PeerOnQId DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required string PublicKeyFingerprint { get; init; }
    public required SessionPermission AllowedPermissions { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public DeviceTrustState TrustState { get; init; } = DeviceTrustState.PendingApproval;
    public DateTimeOffset? RevokedAt { get; init; }

    public bool IsUsableAt(DateTimeOffset now) =>
        TrustState == DeviceTrustState.Trusted &&
        (ExpiresAt is null || ExpiresAt > now) &&
        (AllowedPermissions & ~SessionPermissionPolicy.KnownPermissions) == 0;
}

public sealed record AddressBookDevice
{
    public required Guid RecordId { get; init; }
    public required PeerOnQId DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public string? OperatingSystem { get; init; }
    public DateTimeOffset? OperatingSystemVerifiedAt { get; init; }
    public bool IsFavorite { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Notes { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public DateTimeOffset? LastSeenVerifiedAt { get; init; }
    public IReadOnlyList<Guid> GroupIds { get; init; } = [];
}

public sealed record AddressBookGroup
{
    public required Guid GroupId { get; init; }
    public required string Name { get; init; }
}

public enum SecurityAuditEventType
{
    FileTransferOffered = 0,
    FileTransferAccepted = 1,
    FileTransferRejected = 2,
    FileTransferCompleted = 3,
    FileTransferFailed = 4,
    FileTransferCanceled = 5,
    ClipboardEnabled = 6,
    ClipboardDisabled = 7,
    ClipboardRejected = 8,
    TrustedDeviceAdded = 9,
    TrustedDeviceChanged = 10,
    TrustedDeviceRevoked = 11,
    UnattendedEnabled = 12,
    UnattendedDisabled = 13,
    UnattendedAuthenticationSucceeded = 14,
    UnattendedAuthenticationFailed = 15,
    UnattendedLockedOut = 16,
    PermissionChanged = 17,
    SessionStarted = 18,
    SessionEnded = 19,
    AuthenticationFailed = 20,
    UpdateCheckCompleted = 21,
    UpdateCheckFailed = 22,
    UpdateRejected = 23,
    UpdateDownloaded = 24,
    UpdateInstallStarted = 25,
    AuditExported = 26,
    AuditCleared = 27,
    AuditIntegrityFailure = 28,
    CrashReportingConsentChanged = 29,
    CrashRecorded = 30,
    SupportInvitationIssued = 31,
    SupportInvitationRevoked = 32,
    SupportInvitationUsed = 33,
    SupportInvitationRejected = 34,
    TechnicianSessionFocused = 35,
}

public sealed record SecurityAuditEvent
{
    public required Guid EventId { get; init; }
    public required SecurityAuditEventType EventType { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public string? SessionId { get; init; }
    public string? PeerMaskedId { get; init; }
    public string? LocalDevice { get; init; }
    public SessionPermission? PermissionSet { get; init; }
    public string? Outcome { get; init; }
    public string? FailureCategory { get; init; }
    public string? NetworkPath { get; init; }
    public string? AppVersion { get; init; }
    public IReadOnlyDictionary<string, string> SafeMetadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> IntegrityMetadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
