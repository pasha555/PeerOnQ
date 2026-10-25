using Microsoft.Extensions.Options;

namespace PeerOnQ.Admin.Api;

public sealed class AdminAuthenticationOptions
{
    public const string SectionName = "PeerOnQ:AdminTokens";
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string? SigningKey { get; set; }
    public string? RefreshHashKey { get; set; }
    public int AccessTokenMinutes { get; set; } = 5;
    public int RefreshTokenHours { get; set; } = 8;
    public int MfaChallengeMinutes { get; set; } = 5;
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public bool AllowMfaBypassForDevelopment { get; set; }
}

public sealed class AdminAuthenticationOptionsValidator : IValidateOptions<AdminAuthenticationOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminAuthenticationOptions options)
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps)
            errors.Add("Admin token issuer must be an absolute HTTPS URI.");
        if (string.IsNullOrWhiteSpace(options.Audience) || options.Audience.Length > 128)
            errors.Add("Admin token audience is required.");
        if (string.IsNullOrWhiteSpace(options.SigningKey) || options.SigningKey.Length < 64)
            errors.Add("Admin JWT signing key must contain at least 64 characters.");
        if (string.IsNullOrWhiteSpace(options.RefreshHashKey) || options.RefreshHashKey.Length < 64)
            errors.Add("Admin refresh-token hash key must contain at least 64 characters.");
        if (options.AccessTokenMinutes is < 1 or > 15) errors.Add("AccessTokenMinutes must be between 1 and 15.");
        if (options.RefreshTokenHours is < 1 or > 168) errors.Add("RefreshTokenHours must be between 1 and 168.");
        if (options.MfaChallengeMinutes is < 1 or > 10) errors.Add("MfaChallengeMinutes must be between 1 and 10.");
        if (options.MaxFailedAttempts is < 3 or > 10) errors.Add("MaxFailedAttempts must be between 3 and 10.");
        if (options.LockoutMinutes is < 5 or > 1440) errors.Add("LockoutMinutes must be between 5 and 1440.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal static class AdminAuthenticationConfiguration
{
    internal static void EnsureMfaBypassAllowed(
        IHostEnvironment environment,
        AdminAuthenticationOptions options)
    {
        if (options.AllowMfaBypassForDevelopment && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "PeerOnQ:AdminTokens:AllowMfaBypassForDevelopment is allowed only in the Development environment.");
        }
    }

    internal static bool IsMfaBypassEnabled(
        IHostEnvironment environment,
        AdminAuthenticationOptions options) =>
        options.AllowMfaBypassForDevelopment && environment.IsDevelopment();
}
