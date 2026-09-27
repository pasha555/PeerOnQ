using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain;

namespace PeerOnQ.Cloud.Api;

public sealed class CustomerPortalOptions
{
    public const string SectionName = "PeerOnQ:CustomerPortal";
    public CustomerRegistrationMode RegistrationMode { get; init; } = CustomerRegistrationMode.Closed;
    public bool RequireEmailVerification { get; init; } = true;
    public string JwtIssuer { get; init; } = "peeronq-cloud";
    public string JwtAudience { get; init; } = "peeronq-customer-portal";
    public string JwtSigningKey { get; init; } = string.Empty;
    public int AccessTokenMinutes { get; init; } = 10;
    public int RefreshTokenDays { get; init; } = 14;
    public int InvitationMinutes { get; init; } = 1440;
    public int EmailTokenMinutes { get; init; } = 30;
    public string PortalBaseUrl { get; init; } = "https://portal.dev.localhost:8443";
    public string DataProtectionPath { get; init; } = ".local/customer-data-protection";
    public CustomerMailOptions Mail { get; init; } = new();
}

public sealed class CustomerMailOptions
{
    public string Provider { get; init; } = "FileSink";
    public string FromAddress { get; init; } = "peeronq@localhost.invalid";
    public string FileSinkPath { get; init; } = ".local/customer-mail";
    public string SmtpHost { get; init; } = string.Empty;
    public int SmtpPort { get; init; } = 587;
    public bool SmtpUseTls { get; init; } = true;
    public string SmtpUsername { get; init; } = string.Empty;
    public string SmtpPassword { get; init; } = string.Empty;
}

public sealed class CustomerPortalOptionsValidator(IHostEnvironment environment) : IValidateOptions<CustomerPortalOptions>
{
    public ValidateOptionsResult Validate(string? name, CustomerPortalOptions options)
    {
        var failures = new List<string>();
        if (options.JwtSigningKey.Length < 48) failures.Add("PeerOnQ:CustomerPortal:JwtSigningKey must contain at least 48 characters.");
        if (options.AccessTokenMinutes is < 1 or > 60) failures.Add("AccessTokenMinutes must be between 1 and 60.");
        if (options.RefreshTokenDays is < 1 or > 90) failures.Add("RefreshTokenDays must be between 1 and 90.");
        if (options.InvitationMinutes is < 5 or > 10080) failures.Add("InvitationMinutes must be between 5 and 10080.");
        if (!Uri.TryCreate(options.PortalBaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            failures.Add("PortalBaseUrl must be an absolute HTTPS URL.");
        if (string.IsNullOrWhiteSpace(options.DataProtectionPath)) failures.Add("DataProtectionPath is required.");

        if (options.Mail.Provider.Equals("FileSink", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                failures.Add("The customer FileSink mail provider is allowed only in Development or Testing.");
            if (string.IsNullOrWhiteSpace(options.Mail.FileSinkPath)) failures.Add("Mail:FileSinkPath is required.");
        }
        else if (options.Mail.Provider.Equals("Smtp", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Mail.SmtpHost)) failures.Add("Mail:SmtpHost is required for SMTP.");
            if (options.Mail.SmtpPort is < 1 or > 65535) failures.Add("Mail:SmtpPort is invalid.");
        }
        else if (options.Mail.Provider.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            if (options.RegistrationMode != CustomerRegistrationMode.Closed)
                failures.Add("RegistrationMode must be Closed when customer mail is disabled.");
            if (options.RequireEmailVerification)
                failures.Add("RequireEmailVerification must be false when customer mail is disabled.");
        }
        else
        {
            failures.Add("Mail:Provider must be Disabled, FileSink, or Smtp.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
