using PeerOnQ.Application.Abstractions;
using PeerOnQ.Platform.Android;
using PeerOnQ.Transport.Protocol;
using Xunit;

namespace PeerOnQ.Platform.Android.Tests;

public sealed class AndroidCapabilityProfileTests
{
    [Fact]
    public void Create_AdvertisesOnlyImplementedViewerCapabilities()
    {
        var manifest = AndroidClientCapabilityProfile.Create();

        Assert.Equal(PeerOnQClientPlatforms.Android, manifest.Platform);
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
        await using var source = new UnsupportedAndroidCaptureSource();
        var request = new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Display, "0", "Display", 1, 1),
        };

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => source.StartAsync(request));
        Assert.False(source.IsCapturing);
    }
}
