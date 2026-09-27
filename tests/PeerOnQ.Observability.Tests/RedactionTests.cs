using System.Diagnostics.Metrics;
using PeerOnQ.Observability;

namespace PeerOnQ.Observability.Tests;

public sealed class RedactionTests
{
    [Fact]
    public void Sanitize_RemovesSecretsAndMasksIdentifiers()
    {
        const string input = "Authorization: Bearer abc.def password=hunter2 device=LNK-483-123-456-204 owner=alice@example.com";

        var sanitized = SensitiveDataRedactor.Sanitize(input);

        Assert.DoesNotContain("abc.def", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("483-123-456-204", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", sanitized, StringComparison.Ordinal);
        Assert.Contains("LNK-483-***-***-204", sanitized, StringComparison.Ordinal);
        Assert.Contains("a***@example.com", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Metrics_ReplaceUntrustedDimensionsWithBoundedValues()
    {
        var measurements = new List<KeyValuePair<string, object?>>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PeerOnQTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (instrument.Name == "peeronq_download_started_total")
            {
                measurements.AddRange(tags.ToArray());
            }
        });
        listener.Start();

        using var metrics = new PeerOnQMetrics(new PeerOnQMetricState());
        metrics.DownloadStarted("windows-device-123", "custom-user-value");

        Assert.Contains(measurements, item => item.Key == "platform" && Equals(item.Value, "unknown"));
        Assert.Contains(measurements, item => item.Key == "architecture" && Equals(item.Value, "unknown"));
    }

    [Fact]
    public void Sanitize_BoundsMessageLength()
    {
        var sanitized = SensitiveDataRedactor.Sanitize(new string('a', 10_000));

        Assert.Equal(4096, sanitized.Length);
    }

    [Fact]
    public void PrometheusExport_HasNoIdentifiersOrLabels()
    {
        using var metrics = new PeerOnQMetrics(new PeerOnQMetricState
        {
            OnlineDevices = static () => 7,
            ActiveSessions = static () => 2,
        });
        metrics.AuthenticationFailed("device-483-123-456-204");
        metrics.DownloadStarted("windows-device-483", "x64-user-value");

        var output = metrics.ExportPrometheus();

        Assert.Contains("peeronq_online_devices 7", output, StringComparison.Ordinal);
        Assert.Contains("peeronq_authentication_failures_total 1", output, StringComparison.Ordinal);
        Assert.DoesNotContain('{', output);
        Assert.DoesNotContain("483", output, StringComparison.Ordinal);
        Assert.DoesNotContain("device-483", output, StringComparison.OrdinalIgnoreCase);
    }
}
