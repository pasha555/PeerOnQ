using PeerOnQ.Media.Pipeline;
using Xunit;

namespace PeerOnQ.Media.Tests;

public class VideoEncoderSelectorTests
{
    [Fact]
    public void Without_hardware_it_falls_back_to_software_and_says_why()
    {
        var selector = new VideoEncoderSelector(() => false);

        var selection = selector.Select();

        Assert.Equal(VideoEncoderKind.SoftwareVp8, selection.Kind);
        Assert.False(selection.IsHardware);
        Assert.Contains("no hardware", selection.Reason);
    }

    [Fact]
    public void Detected_hardware_without_an_implementation_still_falls_back_honestly()
    {
        var selector = new VideoEncoderSelector(() => true, hardwareH264Implemented: () => false);

        var selection = selector.Select();

        Assert.Equal(VideoEncoderKind.SoftwareVp8, selection.Kind);
        Assert.False(selection.IsHardware);
        Assert.Contains("no encoder implementation", selection.Reason);
    }

    [Fact]
    public void Hardware_is_used_only_when_it_exists_and_is_implemented()
    {
        var selector = new VideoEncoderSelector(() => true, hardwareH264Implemented: () => true);

        var selection = selector.Select();

        Assert.Equal(VideoEncoderKind.HardwareH264, selection.Kind);
        Assert.True(selection.IsHardware);
        Assert.Contains("hardware", selection.Describe());
    }

    [Fact]
    public void Hardware_can_be_disabled_per_session()
    {
        var selector = new VideoEncoderSelector(() => true, hardwareH264Implemented: () => true);

        var selection = selector.Select(preferHardware: false);

        Assert.Equal(VideoEncoderKind.SoftwareVp8, selection.Kind);
        Assert.Contains("disabled", selection.Reason);
    }

    [Fact]
    public void A_failing_probe_never_takes_the_session_down()
    {
        var selector = new VideoEncoderSelector(() => throw new InvalidOperationException("driver exploded"));

        var selection = selector.Select();

        Assert.Equal(VideoEncoderKind.SoftwareVp8, selection.Kind);
        Assert.Contains("probe failed", selection.Reason);
    }

    [Fact]
    public void The_description_never_claims_acceleration_that_is_not_running()
    {
        var selection = new VideoEncoderSelector(() => true).Select();

        Assert.DoesNotContain("(hardware)", selection.Describe());
        Assert.Contains("(software)", selection.Describe());
    }
}
