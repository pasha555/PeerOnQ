using PeerOnQ.Application.Abstractions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class CaptureResolutionPolicyTests
{
    [Theory]
    [InlineData(0, CaptureResolution.Automatic, CaptureResolution.Automatic)]
    [InlineData(1, CaptureResolution.P720, CaptureResolution.P720)]
    [InlineData(2, CaptureResolution.P1080, CaptureResolution.P1080)]
    [InlineData(3, CaptureResolution.Native, CaptureResolution.Automatic)]
    [InlineData(4, CaptureResolution.P1440, CaptureResolution.P1080)]
    [InlineData(5, CaptureResolution.P2160, CaptureResolution.Automatic)]
    public void Legacy_wire_values_remain_readable_and_normalize_to_a_streaming_tier(
        int wireValue, CaptureResolution resolution, CaptureResolution expected)
    {
        Assert.Equal(wireValue, (int)resolution);
        Assert.Equal(expected, CaptureResolutionPolicy.Normalize((CaptureResolution)wireValue));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public void Unknown_resolution_values_are_rejected(int wireValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureResolutionPolicy.Normalize((CaptureResolution)wireValue));
    }

    [Theory]
    [InlineData(QualityProfile.Performance)]
    [InlineData(QualityProfile.Balanced)]
    [InlineData(QualityProfile.Quality)]
    [InlineData(QualityProfile.Office)]
    [InlineData(QualityProfile.LowBandwidth)]
    public void Fixed_built_in_profiles_stay_on_the_standard_tier(QualityProfile quality)
    {
        var profile = MediaProfile.For(quality);

        Assert.Equal(CaptureResolution.P1080, CaptureResolutionPolicy.Normalize(profile.Resolution));
        Assert.False(CaptureResolutionPolicy.AllowsUltraHd(profile.Resolution));
    }

    [Fact]
    public void Automatic_profile_allows_the_4k_tier()
    {
        var profile = MediaProfile.For(QualityProfile.Automatic);

        Assert.Equal(CaptureResolution.Automatic, CaptureResolutionPolicy.Normalize(profile.Resolution));
        Assert.True(CaptureResolutionPolicy.AllowsUltraHd(profile.Resolution));
    }

    [Theory]
    [InlineData(3840, 2160, true)]
    [InlineData(4096, 2160, true)]
    [InlineData(3840, 2400, true)]
    [InlineData(5120, 2880, true)]
    [InlineData(7680, 4320, true)]
    [InlineData(2160, 3840, false)]
    [InlineData(5120, 2160, false)]
    [InlineData(3440, 1440, false)]
    [InlineData(2560, 1440, false)]
    [InlineData(1920, 1080, false)]
    [InlineData(0, 0, false)]
    public void Only_4k_class_sources_that_fit_hardware_h264_use_the_4k_tier(
        int width, int height, bool expected)
    {
        Assert.Equal(expected, CaptureResolutionPolicy.IsUltraHdSource(width, height));
    }

    [Theory]
    [InlineData(3840, 2160, CaptureResolution.Automatic, 1, 2160)]
    [InlineData(3840, 2160, CaptureResolution.Automatic, 2, 1080)]
    [InlineData(3840, 2160, CaptureResolution.Automatic, 4, 1080)]
    [InlineData(3840, 2160, CaptureResolution.P1080, 1, 1080)]
    [InlineData(5120, 2880, CaptureResolution.Native, 1, 2160)]
    [InlineData(2560, 1440, CaptureResolution.Automatic, 1, 1080)]
    [InlineData(1920, 1080, CaptureResolution.Automatic, 4, 1080)]
    [InlineData(1280, 720, CaptureResolution.Automatic, 1, 720)]
    public void Target_short_edge_selects_4k_only_for_4k_sources_and_never_drops_below_the_standard_tier(
        int width, int height, CaptureResolution resolution, int downscaleFactor, int expected)
    {
        Assert.Equal(expected, CaptureResolutionPolicy.ResolveTargetShortEdge(width, height, resolution, downscaleFactor));
    }
}
