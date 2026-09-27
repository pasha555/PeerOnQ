using PeerOnQ.Application.Abstractions;
using PeerOnQ.Platform.Apple;
using PeerOnQ.Transport.Protocol;
using Xunit;

namespace PeerOnQ.Platform.Apple.Tests;

public sealed class AppleCapabilityProfileTests
{
    [Theory]
    [InlineData(AppleClientPlatform.MacOS, PeerOnQClientPlatforms.MacOS)]
    [InlineData(AppleClientPlatform.IOS, PeerOnQClientPlatforms.IOS)]
    [InlineData(AppleClientPlatform.IPadOS, PeerOnQClientPlatforms.IPadOS)]
    public void Create_AdvertisesOnlyImplementedViewerCapabilities(
        AppleClientPlatform platform,
        string expectedWirePlatform)
    {
        var manifest = AppleClientCapabilityProfile.Create(platform);

        Assert.Equal(expectedWirePlatform, manifest.Platform);
        Assert.Contains(EndpointCapabilityNames.SessionViewer, manifest.Capabilities);
        Assert.Contains(EndpointCapabilityNames.ScreenRender, manifest.Capabilities);
        Assert.Contains(EndpointCapabilityNames.InputSend, manifest.Capabilities);
        Assert.Contains(EndpointCapabilityNames.SessionReconnect, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.SessionHost, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.ScreenCapture, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.InputInject, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.UnattendedRequest, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.FileSend, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.ClipboardSend, manifest.Capabilities);
        Assert.True(CapabilityNegotiator.TryNormalize(manifest, out _, out var error), error);
    }

    [Fact]
    public async Task CaptureSource_FailsClosed()
    {
        await using var source = new UnsupportedAppleCaptureSource(AppleClientPlatform.IOS);
        var request = new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Display, "0", "Display", 1, 1),
        };

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => source.StartAsync(request));
        Assert.False(source.IsCapturing);
    }
}
