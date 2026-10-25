using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting;

namespace PeerOnQ.Infrastructure.Diagnostics;

/// <summary>
/// Rewrites any full PeerOnQ ID that reaches a log message into its masked form.
/// This is a backstop: call sites are expected to log masked IDs already.
/// </summary>
public sealed partial class PeerOnQIdMaskingEnricher : ILogEventEnricher
{
    [GeneratedRegex(@"(?<!\d)(?:LNK-)?(\d{3})-\d{3}-\d{3}-(\d{3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex IdPattern();

    public static string Mask(string text) => IdPattern().Replace(text, "$1-***-***-$2");

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            if (value is ScalarValue { Value: string text } && IdPattern().IsMatch(text))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(Mask(text))));
            }
        }
    }
}

public sealed class UtcTimestampEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TimestampUtc", logEvent.Timestamp.UtcDateTime));
}

/// <summary>
/// Writes structured JSON without exception messages/stacks or rendered values. String properties
/// are sanitized, and security-sensitive property names are redacted regardless of their value.
/// </summary>
public sealed class SanitizedJsonLogFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("timestampUtc", logEvent.Timestamp.UtcDateTime);
            writer.WriteString("level", logEvent.Level.ToString());
            writer.WriteString("messageTemplate", DiagnosticSanitizer.Sanitize(logEvent.MessageTemplate.Text));
            if (logEvent.Exception is not null)
                writer.WriteString("exceptionType", logEvent.Exception.GetType().FullName);

            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            foreach (var property in logEvent.Properties.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Key);
                if (IsSensitivePropertyName(property.Key)) writer.WriteStringValue("[redacted]");
                else if (property.Key.Equals("AppVersion", StringComparison.Ordinal)
                         && property.Value is ScalarValue { Value: string appVersion }
                         && Version.TryParse(appVersion, out _))
                    writer.WriteStringValue(appVersion);
                else WriteValue(writer, property.Value, depth: 0);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        output.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteValue(Utf8JsonWriter writer, LogEventPropertyValue value, int depth)
    {
        if (depth >= 8)
        {
            writer.WriteStringValue("[depth-limited]");
            return;
        }

        switch (value)
        {
            case ScalarValue { Value: null }:
                writer.WriteNullValue();
                break;
            case ScalarValue { Value: string text }:
                writer.WriteStringValue(DiagnosticSanitizer.Sanitize(text));
                break;
            case ScalarValue { Value: bool boolean }:
                writer.WriteBooleanValue(boolean);
                break;
            case ScalarValue { Value: byte or sbyte or short or ushort or int or uint or long } integer:
                writer.WriteNumberValue(Convert.ToInt64(integer.Value, CultureInfo.InvariantCulture));
                break;
            case ScalarValue { Value: float or double or decimal } number:
                var numericValue = Convert.ToDouble(number.Value, CultureInfo.InvariantCulture);
                if (double.IsFinite(numericValue)) writer.WriteNumberValue(numericValue);
                else writer.WriteStringValue(numericValue.ToString(CultureInfo.InvariantCulture));
                break;
            case ScalarValue { Value: DateTime dateTime }:
                writer.WriteStringValue(dateTime);
                break;
            case ScalarValue { Value: DateTimeOffset dateTimeOffset }:
                writer.WriteStringValue(dateTimeOffset);
                break;
            case ScalarValue scalar:
                writer.WriteStringValue(DiagnosticSanitizer.Sanitize(
                    Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) ?? string.Empty));
                break;
            case SequenceValue sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Elements.Take(100)) WriteValue(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            case StructureValue structure:
                writer.WriteStartObject();
                foreach (var property in structure.Properties.Take(100))
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitivePropertyName(property.Name)) writer.WriteStringValue("[redacted]");
                    else WriteValue(writer, property.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case DictionaryValue dictionary:
                writer.WriteStartObject();
                foreach (var pair in dictionary.Elements.Take(100))
                {
                    var key = DiagnosticSanitizer.Sanitize(Convert.ToString(pair.Key.Value, CultureInfo.InvariantCulture) ?? "key");
                    writer.WritePropertyName(string.IsNullOrWhiteSpace(key) ? "key" : key);
                    if (IsSensitivePropertyName(key)) writer.WriteStringValue("[redacted]");
                    else WriteValue(writer, pair.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            default:
                writer.WriteStringValue("[unsupported]");
                break;
        }
    }

    private static bool IsSensitivePropertyName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return SensitivePropertyNames.Any(normalized.Contains);
    }

    private static readonly string[] SensitivePropertyNames =
    [
        "password", "passwd", "secret", "token", "authorization", "privatekey", "credential",
        "clipboard", "keystroke", "screencontent", "filecontent",
    ];
}

public static class PeerOnQLogging
{
    /// <summary>
    /// Console plus a rolling file sink under the PeerOnQ data directory. Secrets are never
    /// logged; IDs pass through <see cref="PeerOnQIdMaskingEnricher"/>.
    /// </summary>
    public static Serilog.ILogger CreateSerilogLogger(
        string logDirectory,
        string appVersion = "unknown",
        string environment = "Development",
        string region = "local",
        LogEventLevel minimum = LogEventLevel.Information)
    {
        Directory.CreateDirectory(logDirectory);

        var formatter = new SanitizedJsonLogFormatter();

        return new LoggerConfiguration()
            .MinimumLevel.Is(minimum)
            .Enrich.With(new PeerOnQIdMaskingEnricher())
            .Enrich.With(new UtcTimestampEnricher())
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "PeerOnQ")
            .Enrich.WithProperty("Service", "PeerOnQ.Desktop")
            .Enrich.WithProperty("Environment", environment)
            .Enrich.WithProperty("Region", region)
            .Enrich.WithProperty("AppVersion", appVersion)
            .Enrich.WithProperty("Component", "desktop")
            .Enrich.WithProperty("EventName", "application_event")
            .WriteTo.Console(formatter)
            .WriteTo.File(
                formatter,
                Path.Combine(logDirectory, "peeronq-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: false)
            .CreateLogger();
    }

    public static ILoggerFactory CreateLoggerFactory(
        string logDirectory,
        string appVersion = "unknown",
        string environment = "Development",
        string region = "local")
    {
        var serilog = CreateSerilogLogger(logDirectory, appVersion, environment, region);
        return new SerilogLoggerFactory(serilog, dispose: true);
    }
}
