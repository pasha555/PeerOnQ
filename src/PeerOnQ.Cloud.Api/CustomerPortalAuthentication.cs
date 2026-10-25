using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;

namespace PeerOnQ.Cloud.Api;

public static class CustomerPortalAuthentication
{
    public const string Scheme = "PeerOnQCustomer";
    public const string Policy = "PeerOnQCustomer";
    public const string AccessCookie = "__Host-peeronq_customer_access";
    public const string RefreshCookie = "__Host-peeronq_customer_refresh";
    public const string CsrfCookie = "__Host-peeronq_customer_csrf";
    public const string CsrfHeader = "X-CSRF-Token";
    public const string SessionIdClaim = "peeronq_customer_session";

    public static IServiceCollection AddCustomerPortalAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration.GetSection(CustomerPortalOptions.SectionName).Get<CustomerPortalOptions>() ?? new CustomerPortalOptions();
        services.AddOptions<CustomerPortalOptions>().Bind(configuration.GetSection(CustomerPortalOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<CustomerPortalOptions>>(new CustomerPortalOptionsValidator(environment));
        services.AddSingleton<CustomerPasswordService>();
        services.AddSingleton<CustomerTokenService>();
        services.AddScoped<CustomerAccountService>();
        services.AddScoped<CustomerOrganizationService>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICustomerMailSender, CustomerMailSender>();
        var keyPath = Path.GetFullPath(configured.DataProtectionPath, environment.ContentRootPath);
        Directory.CreateDirectory(keyPath);
        services.AddDataProtection().SetApplicationName("PeerOnQ.CustomerPortal.v1").PersistKeysToFileSystem(new DirectoryInfo(keyPath));

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configured.JwtSigningKey));
        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = true;
            options.SaveToken = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateIssuer = true,
                ValidIssuer = configured.JwtIssuer,
                ValidateAudience = true,
                ValidAudience = configured.JwtAudience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = JwtRegisteredClaimNames.Sub,
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    context.Token = context.Request.Cookies[AccessCookie];
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    if (!Guid.TryParse(context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub), out var accountId) ||
                        !Guid.TryParse(context.Principal?.FindFirstValue(SessionIdClaim), out var sessionId))
                    {
                        context.Fail("The customer session claims are invalid.");
                        return;
                    }

                    var db = context.HttpContext.RequestServices.GetRequiredService<CloudDbContext>();
                    var now = DateTimeOffset.UtcNow;
                    var active = await db.CustomerSessions.AsNoTracking().AnyAsync(value =>
                            value.Id == sessionId && value.AccountId == accountId && value.RevokedAtUtc == null && value.ExpiresAtUtc > now,
                        context.HttpContext.RequestAborted);
                    var enabled = await db.CustomerAccounts.AsNoTracking().AnyAsync(value =>
                            value.Id == accountId && value.Status == Domain.CustomerAccountStatus.Active,
                        context.HttpContext.RequestAborted);
                    if (!active || !enabled) context.Fail("The customer session is no longer active.");
                },
            };
        });
        services.AddAuthorization(options => options.AddPolicy(Policy, policy =>
        {
            policy.AddAuthenticationSchemes(Scheme);
            policy.RequireAuthenticatedUser();
        }));
        return services;
    }

    public static Guid RequireCustomerAccountId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new UnauthorizedAccessException("The customer account claim is missing.");

    public static Guid RequireCustomerSessionId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(SessionIdClaim), out var id)
            ? id
            : throw new UnauthorizedAccessException("The customer session claim is missing.");

    public static void ValidateCsrf(HttpContext context)
    {
        var cookie = context.Request.Cookies[CsrfCookie];
        var header = context.Request.Headers[CsrfHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(header))
            throw new UnauthorizedAccessException("A valid CSRF token is required.");
        var left = Encoding.UTF8.GetBytes(cookie);
        var right = Encoding.UTF8.GetBytes(header);
        if (left.Length != right.Length || !CryptographicOperations.FixedTimeEquals(left, right))
            throw new UnauthorizedAccessException("A valid CSRF token is required.");
    }

    public static void SetSessionCookies(HttpContext context, string accessToken, DateTimeOffset accessExpires, string refreshToken, DateTimeOffset refreshExpires)
    {
        context.Response.Cookies.Append(AccessCookie, accessToken, SecureCookie(accessExpires, true));
        context.Response.Cookies.Append(RefreshCookie, refreshToken, SecureCookie(refreshExpires, true));
        context.Response.Cookies.Append(CsrfCookie, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), SecureCookie(refreshExpires, false));
    }

    public static void ClearSessionCookies(HttpContext context)
    {
        context.Response.Cookies.Delete(AccessCookie, SecureCookie(DateTimeOffset.UnixEpoch, true));
        context.Response.Cookies.Delete(RefreshCookie, SecureCookie(DateTimeOffset.UnixEpoch, true));
        context.Response.Cookies.Delete(CsrfCookie, SecureCookie(DateTimeOffset.UnixEpoch, false));
    }

    private static CookieOptions SecureCookie(DateTimeOffset expires, bool httpOnly) => new()
    {
        Secure = true,
        HttpOnly = httpOnly,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Expires = expires,
        Path = "/",
    };
}

public sealed class CustomerPasswordService
{
    public static CustomerPasswordRules Rules { get; } = new(12, 128, true, true, true);
    private readonly PasswordHasher<CustomerAccount> _hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = 210_000,
    }));

    public string Hash(CustomerAccount account, string password)
    {
        ValidatePassword(password);
        return _hasher.HashPassword(account, password);
    }

    public PasswordVerificationResult Verify(CustomerAccount account, string password) =>
        _hasher.VerifyHashedPassword(account, account.PasswordHash, password ?? string.Empty);

    public static void ValidatePassword(string password)
    {
        if (password is null || password.Length < Rules.MinLength || password.Length > Rules.MaxLength ||
            !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
            throw new ArgumentException("Password must contain 12 to 128 characters with upper-case, lower-case, and numeric characters.", nameof(password));
    }
}

public sealed class CustomerTokenService(IOptions<CustomerPortalOptions> options)
{
    public (string Token, DateTimeOffset ExpiresAtUtc) IssueAccessToken(CustomerAccount account, CustomerSession session, DateTimeOffset now)
    {
        var expires = now.AddMinutes(options.Value.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Value.JwtIssuer,
            Audience = options.Value.JwtAudience,
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, account.Id.ToString("D", CultureInfo.InvariantCulture)),
                new Claim(CustomerPortalAuthentication.SessionIdClaim, session.Id.ToString("D", CultureInfo.InvariantCulture)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
            ]),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.JwtSigningKey)),
                SecurityAlgorithms.HmacSha512),
        };
        return (new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor), expires);
    }

    public static string IssueOpaqueToken(int bytes = 32) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));
    public static byte[] HashOpaqueToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

public static class CustomerTotp
{
    public static bool Verify(byte[] secret, string code, DateTimeOffset now)
    {
        if (secret.Length < 16 || code is not { Length: 6 } || !code.All(char.IsDigit)) return false;
        var supplied = int.Parse(code, CultureInfo.InvariantCulture);
        var counter = now.ToUnixTimeSeconds() / 30;
        Span<byte> counterBytes = stackalloc byte[8];
        for (var offset = -1; offset <= 1; offset++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter + offset);
            var hash = HMACSHA1.HashData(secret, counterBytes);
            var index = hash[^1] & 0x0f;
            var expected = ((hash[index] & 0x7f) << 24 | hash[index + 1] << 16 | hash[index + 2] << 8 | hash[index + 3]) % 1_000_000;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected.ToString("D6", CultureInfo.InvariantCulture)), Encoding.ASCII.GetBytes(code))) return true;
        }
        return false;
    }

    public static string ToBase32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bitsLeft += 8;
            while (bitsLeft >= 5) { output.Append(alphabet[(buffer >> (bitsLeft -= 5)) & 31]); }
        }
        if (bitsLeft > 0) output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return output.ToString();
    }
}
