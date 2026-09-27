using PeerOnQ.Media.Pipeline;
using PeerOnQ.Platform.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace PeerOnQ.EndToEnd.Tests;

public class HardwareEncoderProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void The_probe_reports_what_this_machine_actually_has()
    {
        HardwareEncoderProbe.Invalidate();

        var encoders = HardwareEncoderProbe.Available;

        foreach (var encoder in encoders)
        {
            output.WriteLine($"{encoder.Codec}: {encoder.Name}");
        }

        output.WriteLine($"H.264 hardware encoder present: {HardwareEncoderProbe.Supports(HardwareVideoCodec.H264)}");

        // The machine may or may not have one; what matters is that probing is safe and that
        // the answer is consistent.
        Assert.NotNull(encoders);
        Assert.All(encoders, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
        Assert.Equal(HardwareEncoderProbe.Supports(HardwareVideoCodec.H264),
            encoders.Any(e => e.Codec == HardwareVideoCodec.H264));
    }

    [Fact]
    public void Probing_repeatedly_is_cheap_and_stable()
    {
        HardwareEncoderProbe.Invalidate();

        var first = HardwareEncoderProbe.Available;
        var second = HardwareEncoderProbe.Available;

        Assert.Same(first, second);
        Assert.Equal(first.Count, second.Count);
    }

    [Fact]
    public void The_real_selector_reports_the_encoder_that_will_actually_run()
    {
        var selector = new VideoEncoderSelector(() => HardwareEncoderProbe.Supports(HardwareVideoCodec.H264));

        var selection = selector.Select();

        output.WriteLine(selection.Describe());

        // The shipping codec path has no hardware encoder implementation, so software VP8 must be the answer
        // regardless of what the machine reports.
        Assert.Equal(VideoEncoderKind.SoftwareVp8, selection.Kind);
        Assert.False(selection.IsHardware);
        Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
    }
}
