using PeerOnQ.Application.Abstractions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class CaptureResolutionPolicyTests
{
    [Theory]
    [InlineData(0, CaptureResolution.Automatic, CaptureResolution.P1080)]
    [InlineData(1, CaptureResolution.P720, CaptureResolution.P720)]
    [InlineData(2, CaptureResolution.P1080, CaptureResolution.P1080)]
    [InlineData(3, CaptureResolution.Native, CaptureResolution.P1080)]
    [InlineData(4, CaptureResolution.P1440, CaptureResolution.P1080)]
    [InlineData(5, CaptureResolution.P2160, CaptureResolution.P1080)]
    public void Legacy_wire_values_remain_readable_and_normalize_to_the_current_streaming_limit(
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
    [InlineData(QualityProfile.Automatic)]
    [InlineData(QualityProfile.Performance)]
    [InlineData(QualityProfile.Balanced)]
    [InlineData(QualityProfile.Quality)]
    [InlineData(QualityProfile.Office)]
    [InlineData(QualityProfile.LowBandwidth)]
    public void Built_in_profiles_never_select_a_resolution_above_the_current_limit(QualityProfile quality)
    {
        var profile = MediaProfile.For(quality);

        Assert.Equal(CaptureResolution.P1080, CaptureResolutionPolicy.Normalize(profile.Resolution));
    }
}
