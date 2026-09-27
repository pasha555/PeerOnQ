using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PeerOnQ.Cloud.Domain;

namespace PeerOnQ.Admin.Api;

public sealed record IssuedAdminAccessToken(string Token, DateTimeOffset ExpiresAtUtc);

public sealed class AdminTokenService(IOptions<AdminAuthenticationOptions> options, TimeProvider timeProvider)
{
    public IssuedAdminAccessToken IssueAccessToken(Guid userId, Guid sessionId, IReadOnlySet<AdminRoleKind> roles, bool mfaSatisfied)
    {
        var configured = options.Value;
        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(configured.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString("N")),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Sid, sessionId.ToString("N")),
            new("mfa", mfaSatisfied ? "true" : "false"),
        };
        claims.AddRange(roles.OrderBy(role => role).Select(role => new Claim(ClaimTypes.Role, role.ToString())));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configured.SigningKey!)),
            SecurityAlgorithms.HmacSha512);
        var token = new JwtSecurityToken(
            configured.Issuer,
            configured.Audience,
            claims,
            now.UtcDateTime,
            expires.UtcDateTime,
            credentials);
        return new IssuedAdminAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public string CreateRefreshToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public byte[] HashRefreshToken(string token)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.Value.RefreshHashKey!));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(token));
    }

    public static Guid RequireAdminUserId(ClaimsPrincipal principal) =>
        Guid.TryParseExact(principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, "N", out var value)
            ? value
            : throw new UnauthorizedAccessException("The admin identity is invalid.");

    public static Guid RequireAdminSessionId(ClaimsPrincipal principal) =>
        Guid.TryParseExact(principal.FindFirst(JwtRegisteredClaimNames.Sid)?.Value, "N", out var value)
            ? value
            : throw new UnauthorizedAccessException("The admin session is invalid.");

    public static DateTimeOffset RequireExpiry(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Exp)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? DateTimeOffset.FromUnixTimeSeconds(value)
            : throw new UnauthorizedAccessException("The admin token expiry is invalid.");
}
