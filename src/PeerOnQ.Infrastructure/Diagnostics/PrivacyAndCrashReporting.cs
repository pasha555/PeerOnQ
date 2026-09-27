using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Collaboration;

namespace PeerOnQ.Infrastructure.Diagnostics;

public sealed record PrivacySettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool CrashReportingEnabled { get; init; }
    public int AuditRetentionDays { get; init; } = 90;
}

public sealed class PrivacySettingsService(IDeviceSecretStore secretStore, ISecurityAuditLog audit)
{
    private const string StoreName = "privacy-settings-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
    };

    public async Task<PrivacySettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var protectedSettings = await secretStore.TryGetAsync(StoreName, cancellationToken);
        if (protectedSettings is null) return new PrivacySettings();

        try
        {
            var settings = JsonSerializer.Deserialize<PrivacySettings>(protectedSettings, JsonOptions);
            return settings is { SchemaVersion: 1, AuditRetentionDays: >= 1 and <= 3650 }
                ? settings
                : new PrivacySettings();
        }
        catch (JsonException)
        {
            return new PrivacySettings();
        }
    }

    public async Task SetCrashReportingEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(cancellationToken);
        if (current.CrashReportingEnabled == enabled) return;
        await SaveAsync(current with { CrashReportingEnabled = enabled }, cancellationToken);
        await audit.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = SecurityAuditEventType.CrashReportingConsentChanged,
            OccurredAt = DateTimeOffset.UtcNow,
            Outcome = enabled ? "enabled" : "disabled",
        }, cancellationToken);
    }

    public async Task SetAuditRetentionDaysAsync(int days, CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(days), "Audit retention must be between 1 and 3650 days.");
        var current = await GetAsync(cancellationToken);
        await SaveAsync(current with { AuditRetentionDays = days }, cancellationToken);
    }

    private Task SaveAsync(PrivacySettings settings, CancellationToken cancellationToken) =>
        secretStore.SetAsync(StoreName, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions), cancellationToken);
}

public sealed class CrashReportService(
    string reportDirectory,
    PrivacySettingsService privacySettings,
    ISecurityAuditLog audit)
{
    public async Task<string?> RecordAsync(Exception exception, string source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var settings = await privacySettings.GetAsync(cancellationToken);
        if (!settings.CrashReportingEnabled) return null;

        Directory.CreateDirectory(reportDirectory);
        var reportId = Guid.NewGuid();
        var errorMaterial = Encoding.UTF8.GetBytes($"{exception.GetType().FullName}\n{exception.Message}\n{exception.StackTrace}");
        var report = new
        {
            schemaVersion = 1,
            reportId,
            occurredAtUtc = DateTimeOffset.UtcNow,
            source = SanitizeSource(source),
            exceptionType = exception.GetType().FullName,
            errorId = Convert.ToHexString(SHA256.HashData(errorMaterial))[..16],
            appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os = RuntimeInformation.OSDescription,
            disclosure = "No screen, clipboard, file, credential, token, device ID, message, or stack content is stored.",
        };

        var finalPath = Path.Combine(reportDirectory, $"crash-{reportId:D}.json");
        var temporaryPath = finalPath + ".partial";
        await File.WriteAllBytesAsync(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(report), cancellationToken);
        File.Move(temporaryPath, finalPath);

        await audit.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = SecurityAuditEventType.CrashRecorded,
            OccurredAt = DateTimeOffset.UtcNow,
            Outcome = "local_sanitized_report",
            IntegrityMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["report_id"] = reportId.ToString("D"),
            },
        }, cancellationToken);
        return finalPath;
    }

    private static string SanitizeSource(string source)
    {
        var cleaned = new string((source ?? string.Empty).Where(character => !char.IsControl(character)).Take(40).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }
}
