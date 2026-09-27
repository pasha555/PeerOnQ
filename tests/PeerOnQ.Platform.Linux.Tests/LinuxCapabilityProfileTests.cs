using PeerOnQ.Platform.Linux;
using PeerOnQ.Transport.Protocol;
using Xunit;

namespace PeerOnQ.Platform.Linux.Tests;

public sealed class LinuxCapabilityProfileTests
{
    [Fact]
    public void Create_AdvertisesOnlyImplementedViewerCapabilities()
    {
        var manifest = LinuxClientCapabilityProfile.Create();

        Assert.Equal(PeerOnQClientPlatforms.Linux, manifest.Platform);
        Assert.Contains(EndpointCapabilityNames.SessionViewer, manifest.Capabilities);
        Assert.Contains(EndpointCapabilityNames.ScreenRender, manifest.Capabilities);
        Assert.Contains(EndpointCapabilityNames.InputSend, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.SessionHost, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.ScreenCapture, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.InputInject, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.UnattendedAccept, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.DisplaySelectRequest, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.FileSend, manifest.Capabilities);
        Assert.DoesNotContain(EndpointCapabilityNames.ClipboardSend, manifest.Capabilities);
        Assert.True(CapabilityNegotiator.TryNormalize(manifest, out _, out var error), error);
    }
}
