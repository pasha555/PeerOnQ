using PeerOnQ.Signaling.Server.Diagnostics;
using Xunit;

namespace PeerOnQ.Signaling.Tests;

public sealed class SignalingMetricsTests
{
    [Fact]
    public void Prometheus_output_uses_peeronq_metric_names_only()
    {
        using var metrics = new SignalingMetrics();
        metrics.ConnectionOpened();

        var output = metrics.ExportPrometheus(2, 1);

        Assert.Contains("peeronq_signaling_connections_opened_total 1", output);
        Assert.Contains("peeronq_signaling_devices_online 2", output);
        Assert.DoesNotContain("link" + "ora_", output, StringComparison.OrdinalIgnoreCase);
    }
}
