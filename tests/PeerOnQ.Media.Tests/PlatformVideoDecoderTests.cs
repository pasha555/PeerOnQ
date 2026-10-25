using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media;
using Xunit;

namespace PeerOnQ.Media.Tests;

public sealed class PlatformVideoDecoderTests
{
    [Fact]
    public async Task Viewer_UsesAndDisposesInjectedPlatformDecoder()
    {
        var sink = new TrackingSink();
        await using (var viewer = WebRtcMediaSession.CreateViewer(
                         SessionId.New(),
                         encodedVideoSink: sink))
        {
            Assert.True(viewer.UsesPlatformVideoDecoder);
        }

        Assert.True(sink.Disposed);
    }

    [Fact]
    public async Task Engine_CreatesOnePlatformDecoderForTheBoundSession()
    {
        SessionId observed = default;
        var sink = new TrackingSink();
        var engine = new WebRtcMediaEngine(
            viewerVideoSinkFactory: sessionId =>
            {
                observed = sessionId;
                return sink;
            });
        var expected = SessionId.New();

        await using var viewer = await engine.CreateViewerSessionAsync(expected);

        Assert.Equal(expected, observed);
        Assert.True(Assert.IsType<WebRtcMediaSession>(viewer).UsesPlatformVideoDecoder);
    }

    private sealed class TrackingSink : IEncodedVideoFrameSink
    {
        public bool Disposed { get; private set; }

        public event EventHandler<EncodedVideoFramePresentation>? FramePresented
        {
            add { }
            remove { }
        }

        public bool TrySubmit(AuthenticatedVp8Frame frame) => true;

        public void Dispose() => Disposed = true;
    }
}
