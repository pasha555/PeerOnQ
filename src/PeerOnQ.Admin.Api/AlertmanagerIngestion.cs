using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Cloud.Infrastructure.Persistence;
using PeerOnQ.Observability;

namespace PeerOnQ.Admin.Api;

public sealed class AlertIngestionOptions
{
    public const string SectionName = "PeerOnQ:AlertIngestion";
    public string? Token { get; set; }
    public string? TokenFile { get; set; }
    public string[] AllowedRunbookHosts { get; set; } = [];
    public string DefaultRegion { get; set; } = string.Empty;
    public int MaximumAlertsPerWebhook { get; set; } = 100;
}

public sealed class AlertIngestionOptionsValidator : IValidateOptions<AlertIngestionOptions>
{
    public ValidateOptionsResult Validate(string? name, AlertIngestionOptions options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Token) == string.IsNullOrWhiteSpace(options.TokenFile))
            errors.Add("Configure exactly one alert-ingestion Token or TokenFile.");
        if (options.Token is { Length: < 32 }) errors.Add("Alert-ingestion tokens must contain at least 32 characters.");
        if (options.TokenFile is { Length: > 0 } path && (!Path.IsPathFullyQualified(path) || path.Length > 512))
            errors.Add("Alert-ingestion TokenFile must be a bounded absolute path.");
        if (options.AllowedRunbookHosts.Length == 0
            || options.AllowedRunbookHosts.Any(host => string.IsNullOrWhiteSpace(host) || host.Length > 253))
            errors.Add("At least one runbook host must be allowlisted.");
        if (!IsBoundedName(options.DefaultRegion, 64)) errors.Add("DefaultRegion is required and invalid.");
        if (options.MaximumAlertsPerWebhook is < 1 or > 500)
            errors.Add("MaximumAlertsPerWebhook must be between 1 and 500.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    internal static bool IsBoundedName(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}

public sealed class AlertIngestionTokenProvider(IOptions<AlertIngestionOptions> options)
{
    public bool Validate(string? suppliedToken)
    {
        if (string.IsNullOrWhiteSpace(suppliedToken) || suppliedToken.Length > 1024) return false;
        var expected = ReadExpectedToken();
        if (expected.Length < 32) return false;
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    private string ReadExpectedToken()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.Token)) return options.Value.Token;
        try
        {
            var path = options.Value.TokenFile!;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 4096) return string.Empty;
            return File.ReadAllText(path).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}

public sealed record AlertmanagerWebhook(string Version, string Status, AlertmanagerAlert[] Alerts);

public sealed record AlertmanagerAlert(
    string Status,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Annotations,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Fingerprint);

public static class AlertmanagerIngestion
{
    public const string Path = "/internal/v1/alerts/alertmanager";
    public const string TokenHeader = "X-PeerOnQ-Alertmanager-Token";
    internal const int MaximumRunbookUrlLength = 512;
    internal const int MaximumRunbookFragmentLength = 128;

    public static async Task<IResult> IngestAsync(
        AlertmanagerWebhook webhook,
        HttpContext context,
        AlertIngestionTokenProvider tokenProvider,
        IOptions<AlertIngestionOptions> options,
        CloudDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!tokenProvider.Validate(context.Request.Headers[TokenHeader].FirstOrDefault()))
            throw new ApiProblemException(StatusCodes.Status401Unauthorized, "service_authentication_failed", "Service authentication failed.");
        if (!string.Equals(webhook.Version, "4", StringComparison.Ordinal))
            throw new ArgumentException("The Alertmanager webhook version is unsupported.", nameof(webhook));
        if (webhook.Alerts is null || webhook.Alerts.Length is 0 || webhook.Alerts.Length > options.Value.MaximumAlertsPerWebhook)
            throw new ArgumentException("The Alertmanager webhook contains an invalid number of alerts.", nameof(webhook));

        var now = timeProvider.GetUtcNow();
        foreach (var incoming in webhook.Alerts)
        {
            var mapped = Map(incoming, options.Value, now);
            var existing = await db.AlertEvents.SingleOrDefaultAsync(value => value.Id == mapped.Id, cancellationToken);
            if (existing is null)
            {
                existing = new AlertEvent(
                    mapped.Id,
                    mapped.RuleName,
                    mapped.Severity,
                    mapped.Region,
                    mapped.Summary,
                    mapped.RunbookUrl,
                    mapped.StartedAtUtc);
                db.AlertEvents.Add(existing);
            }
            if (mapped.ResolvedAtUtc is { } resolvedAtUtc) existing.Resolve(resolvedAtUtc);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted();
    }

    private static MappedAlert Map(AlertmanagerAlert alert, AlertIngestionOptions options, DateTimeOffset now)
    {
        if (alert.Labels is null || alert.Annotations is null
            || !alert.Labels.TryGetValue("alertname", out var ruleName)
            || !AlertIngestionOptionsValidator.IsBoundedName(ruleName, 128)
            || string.IsNullOrWhiteSpace(alert.Fingerprint)
            || alert.Fingerprint.Length > 128
            || alert.Fingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("The Alertmanager alert identity is invalid.", nameof(alert));
        if (alert.StartsAt < now.AddDays(-366) || alert.StartsAt > now.AddMinutes(5))
            throw new ArgumentException("The Alertmanager alert start time is invalid.", nameof(alert));

        var status = alert.Status?.Trim().ToLowerInvariant();
        if (status is not ("firing" or "resolved"))
            throw new ArgumentException("The Alertmanager alert status is invalid.", nameof(alert));
        var severity = alert.Labels.TryGetValue("severity", out var severityValue)
            ? severityValue.Trim().ToLowerInvariant() switch
            {
                "critical" => AlertSeverity.Critical,
                "warning" => AlertSeverity.Warning,
                "info" or "information" => AlertSeverity.Information,
                _ => throw new ArgumentException("The Alertmanager alert severity is invalid.", nameof(alert)),
            }
            : AlertSeverity.Warning;
        var region = alert.Labels.TryGetValue("region", out var regionValue) ? regionValue : options.DefaultRegion;
        if (!AlertIngestionOptionsValidator.IsBoundedName(region, 64))
            throw new ArgumentException("The Alertmanager alert region is invalid.", nameof(alert));
        if (!alert.Annotations.TryGetValue("summary", out var summary)
            || string.IsNullOrWhiteSpace(summary)
            || summary.Length > 512)
            throw new ArgumentException("The Alertmanager alert summary is invalid.", nameof(alert));
        if (!alert.Annotations.TryGetValue("runbook_url", out var runbookValue)
            || !Uri.TryCreate(runbookValue, UriKind.Absolute, out var runbook)
            || !IsAllowedRunbook(runbook, options.AllowedRunbookHosts))
            throw new ArgumentException("The Alertmanager runbook URL is invalid or not allowlisted.", nameof(alert));

        DateTimeOffset? resolvedAt = null;
        if (status == "resolved")
        {
            resolvedAt = alert.EndsAt >= alert.StartsAt && alert.EndsAt <= now.AddMinutes(5) ? alert.EndsAt : now;
        }
        return new MappedAlert(
            StableId(alert.Fingerprint, alert.StartsAt),
            ruleName,
            severity,
            region,
            summary.Trim(),
            runbook.AbsoluteUri,
            alert.StartsAt,
            resolvedAt);
    }

    internal static bool IsAllowedRunbook(Uri runbook, IReadOnlyCollection<string> allowedHosts) =>
        runbook.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(runbook.UserInfo)
        && runbook.AbsoluteUri.Length <= MaximumRunbookUrlLength
        && runbook.Fragment.Length <= MaximumRunbookFragmentLength + 1
        && allowedHosts.Contains(runbook.IdnHost, StringComparer.OrdinalIgnoreCase);

    private static Guid StableId(string fingerprint, DateTimeOffset startedAtUtc)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{fingerprint.ToLowerInvariant()}|{startedAtUtc.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        return new Guid(digest.AsSpan(0, 16));
    }

    private sealed record MappedAlert(
        Guid Id,
        string RuleName,
        AlertSeverity Severity,
        string Region,
        string Summary,
        string RunbookUrl,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? ResolvedAtUtc);
}
