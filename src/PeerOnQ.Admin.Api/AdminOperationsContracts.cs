namespace PeerOnQ.Admin.Api;

public sealed record AdminReasonRequest(string Reason);

public sealed record AdminSessionRow(
    Guid SessionId,
    Guid AdminUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    string UserAgentFamily);

public sealed record AdminPageResponse<T>(IReadOnlyList<T> Items, int Offset, int Limit, long Total);

public sealed record AdminDiagnosticDetailResponse(
    Guid DiagnosticId,
    Guid InstallationId,
    string Status,
    bool ConsentGranted,
    DateTimeOffset ConsentGrantedAtUtc,
    string AppVersion,
    string OsVersion,
    string Architecture,
    string? ErrorId,
    string? IssueCategory,
    string? ReferenceCode,
    long? SanitizedArchiveSizeBytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);
