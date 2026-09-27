namespace PeerOnQ.Admin.Api;

public sealed record AdminLoginRequest(string Email, string Password);
public sealed record AdminMfaVerifyRequest(string MfaChallengeId, string Code);
public sealed record AdminAuthResponse(
    string Status,
    string? MfaChallengeId,
    string? AccessToken,
    DateTimeOffset? ExpiresAtUtc);
public sealed record AdminRefreshResponse(string AccessToken, DateTimeOffset ExpiresAtUtc);
public sealed record AdminSessionResponse(
    Guid UserId,
    IReadOnlyList<string> Roles,
    bool MfaEnabled,
    DateTimeOffset ExpiresAtUtc,
    bool ReleasePublicationEnabled,
    bool WebsitePublicationEnabled);
