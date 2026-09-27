using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Infrastructure.Diagnostics;

public sealed record DiagnosticBundleRequest
{
    public required bool UserConsented { get; init; }
    public required string AppVersion { get; init; }
    public required string UpdateStatus { get; init; }
    public required int DatabaseSchemaVersion { get; init; }
    public IReadOnlyList<string> ConnectionFailureCodes { get; init; } = [];
    public IReadOnlyDictionary<string, double> WebRtcStatistics { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> HealthChecks { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<SessionTimelineEntry> SessionTimeline { get; init; } = [];
}

public sealed record DiagnosticBundleResult(
    Guid DiagnosticId,
    string FilePath,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Creates a bounded, allowlist-only diagnostics archive after an explicit user confirmation.
/// It never reads databases, crash dumps, screen data, clipboard data, user files, or secret stores.
/// </summary>
public sealed class DiagnosticBundleService(
    string diagnosticsDirectory,
    string logDirectory,
    TimeProvider? timeProvider = null)
{
    public const long MaximumBundleBytes = 20L * 1024 * 1024;
    public const int MaximumLogFiles = 5;
    public const int MaximumBytesPerLog = 2 * 1024 * 1024;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<DiagnosticBundleResult> CreateAsync(
        DiagnosticBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.UserConsented)
        {
            throw new InvalidOperationException("Diagnostics can be created only after explicit user consent.");
        }

        Directory.CreateDirectory(diagnosticsDirectory);
        var now = _time.GetUtcNow();
        DeleteExpiredLocalBundles(now);
        var diagnosticId = Guid.NewGuid();
        var finalPath = Path.Combine(diagnosticsDirectory, $"peeronq-diagnostics-{diagnosticId:N}.zip");
        var temporaryPath = finalPath + ".partial";

        try
        {
            await using (var file = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifest = new
                {
                    schemaVersion = 1,
                    diagnosticId,
                    createdAtUtc = now,
                    expiresAtUtc = now.AddDays(14),
                    appVersion = Limit(request.AppVersion, 64),
                    operatingSystem = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    databaseSchemaVersion = request.DatabaseSchemaVersion,
                    updateStatus = Limit(request.UpdateStatus, 80),
                    connectionFailureCodes = request.ConnectionFailureCodes
                        .Take(50)
                        .Select(value => LimitCode(value, 64))
                        .ToArray(),
                    webRtcStatistics = request.WebRtcStatistics
                        .Where(item => AllowedStatisticNames.Contains(item.Key))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                    healthChecks = request.HealthChecks
                        .Take(30)
                        .ToDictionary(
                            item => LimitCode(item.Key, 48),
                            item => LimitCode(item.Value, 48),
                            StringComparer.Ordinal),
                    sessionTimeline = request.SessionTimeline
                        .Take(128)
                        .Select(entry => new
                        {
                            occurredAtUtc = entry.OccurredAtUtc,
                            code = LimitCode(entry.Code, 64),
                            description = Limit(entry.Description, 160),
                        })
                        .ToArray(),
                    disclosure = "Contains sanitized application logs and technical summaries only. No screen, clipboard, file content, credential, token, private key, password, full device ID, or personal document is included.",
                };

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
                await using (var stream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken);
                }

                var logIndex = 0;
                foreach (var path in EnumerateLogFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var content = await ReadBoundedTextAsync(path, cancellationToken);
                    var sanitized = DiagnosticSanitizer.Sanitize(content);
                    var entry = archive.CreateEntry($"logs/log-{++logIndex}.jsonl", CompressionLevel.SmallestSize);
                    await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    await writer.WriteAsync(sanitized.AsMemory(), cancellationToken);
                }
            }

            var length = new FileInfo(temporaryPath).Length;
            if (length > MaximumBundleBytes)
            {
                throw new InvalidOperationException("The sanitized diagnostics archive exceeded the configured limit.");
            }

            File.Move(temporaryPath, finalPath);
            return new DiagnosticBundleResult(
                diagnosticId,
                finalPath,
                length,
                now,
                now.AddDays(14));
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private IEnumerable<string> EnumerateLogFiles()
    {
        if (!Directory.Exists(logDirectory)) return [];
        return Directory
            .EnumerateFiles(logDirectory, "peeronq-*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Take(MaximumLogFiles)
            .Select(info => info.FullName)
            .ToArray();
    }

    private void DeleteExpiredLocalBundles(DateTimeOffset now)
    {
        foreach (var path in Directory.EnumerateFiles(
                     diagnosticsDirectory,
                     "peeronq-diagnostics-*.zip",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (new FileInfo(path).LastWriteTimeUtc < now.UtcDateTime.AddDays(-14)) File.Delete(path);
            }
            catch (IOException)
            {
                // Another export/upload may currently own the file; the next run retries cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the bundle rather than weakening filesystem permissions.
            }
        }
    }

    private static async Task<string> ReadBoundedTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[Math.Min(stream.Length, MaximumBytesPerLog)];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0) break;
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string Limit(string value, int maximum)
    {
        var cleaned = DiagnosticSanitizer.Sanitize(value ?? string.Empty).Trim();
        return cleaned.Length > maximum ? cleaned[..maximum] : cleaned;
    }

    private static string LimitCode(string value, int maximum)
    {
        var cleaned = new string((value ?? string.Empty)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            .Take(maximum)
            .ToArray());
        return string.IsNullOrEmpty(cleaned) ? "unknown" : cleaned;
    }

    private static readonly HashSet<string> AllowedStatisticNames = new(StringComparer.Ordinal)
    {
        "rttMs",
        "packetLossPercent",
        "jitterMs",
        "bitrateKbps",
        "availableOutgoingBitrateKbps",
        "captureFps",
        "encodeFps",
        "decodeFps",
        "renderFps",
        "captureToEncodeP50Ms",
        "captureToEncodeP95Ms",
        "captureToEncodeP99Ms",
        "decodeToRenderP50Ms",
        "decodeToRenderP95Ms",
        "decodeToRenderP99Ms",
        "captureToPresentP50Ms",
        "captureToPresentP95Ms",
        "captureToPresentP99Ms",
        "frameAgeClockUncertaintyMs",
        "inputToInjectionP50Ms",
        "inputToInjectionP95Ms",
        "inputToInjectionP99Ms",
        "inputClockUncertaintyMs",
        "inputDataLaneNegotiated",
        "inputDataLaneReady",
        "inputDataRecordsSent",
        "bulkDataLaneNegotiated",
        "bulkDataLaneReady",
        "nativeBulkTransportNegotiated",
        "nativeBulkTransportReady",
        "nativeBulkBudgetKbps",
        "nativeBulkGoodputKbps",
        "nativeBulkFeedbackSamples",
        "sctpAssociationBufferedBytes",
        "interactiveSctpBufferedBytes",
        "inputSctpBufferedBytes",
        "bulkSctpBufferedBytes",
        "bulkDataFragmentBytes",
        "bulkQueueBudgetBytes",
        "bulkDataRecordsSent",
        "bulkDataFragmentsSent",
        "targetFps",
        "targetBitrateKbps",
        "encoderQueueDepth",
        "sourceWidth",
        "sourceHeight",
        "requestedWidth",
        "requestedHeight",
        "encodedWidth",
        "encodedHeight",
        "decodedWidth",
        "decodedHeight",
        "renderedWidth",
        "renderedHeight",
        "framesCaptured",
        "framesEncoded",
        "framesRendered",
        "droppedFrames",
        "bytesSent",
        "bytesReceived",
        "reconnectCount",
    };
}

public static partial class DiagnosticSanitizer
{
    [GeneratedRegex(@"(?<!\d)(?:LNK-)?(\d{3})-\d{3}-\d{3}-(\d{3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIdPattern();

    [GeneratedRegex(@"(?<![\w])(?:\d{1,3}\.){3}\d{1,3}(?![\w])")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![A-Fa-f0-9:])(?:[A-Fa-f0-9]{0,4}:){2,7}[A-Fa-f0-9]{0,4}(?![A-Fa-f0-9:])")]
    private static partial Regex PossibleIpv6Pattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}(?![A-Za-z0-9_-])")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?i)(?:password|passwd|secret|token|authorization|private[_ -]?key)\s*[:=]\s*[^\s,;}\]]+")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"(?i)(?:[a-z]:\\|\\\\)[^\r\n\""<>|]+")]
    private static partial Regex WindowsPathPattern();

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = DeviceIdPattern().Replace(value, "$1-***-***-$2");
        result = Ipv4Pattern().Replace(result, "[redacted-address]");
        result = PossibleIpv6Pattern().Replace(result, match =>
            IPAddress.TryParse(match.Value, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6
                ? "[redacted-address]"
                : match.Value);
        result = JwtPattern().Replace(result, "[redacted-token]");
        result = SecretPattern().Replace(result, match =>
            $"{match.Value.Split([':', '='], 2)[0]}=[redacted]");
        result = WindowsPathPattern().Replace(result, "[redacted-path]");
        return result;
    }
}
