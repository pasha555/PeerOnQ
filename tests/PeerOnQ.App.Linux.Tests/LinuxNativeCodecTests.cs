using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Channels;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using Xunit;

namespace PeerOnQ.App.Linux.Tests;

public sealed class LinuxNativeCodecTests
{
    [LinuxNativeFact]
    public async Task ActualEncryptedWebRtcDeliversVp8KeyInterAndResizeFramesToTheLinuxDecoder()
    {
        LinuxNativeLibraryBootstrap.Initialize();
        var frames = JsonSerializer.Deserialize<FixtureFrame[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vp8-color-frames.json")))!;

        Assert.Contains(frames, frame => frame.Width == 3840 && frame.Height == 2160);
        Assert.Equal(0, Convert.FromBase64String(frames[0].Encoded)[0] & 1);
        Assert.Equal(1, Convert.FromBase64String(frames[1].Encoded)[0] & 1);
        var sessionId = SessionId.New();
        var key = RandomNumberGenerator.GetBytes(64);
        using var sending = new SessionTrafficProtector(key, sessionId, SessionRole.Sharer);
        using var receiving = new SessionTrafficProtector(key, sessionId, SessionRole.Viewer);
        CryptographicOperations.ZeroMemory(key);
        await using var sharer = WebRtcMediaSession.CreateSharer(sessionId, null, MediaProfile.Conservative);
        await using var viewer = WebRtcMediaSession.CreateViewer(sessionId);
        sharer.SetTrafficProtector(sending);
        viewer.SetTrafficProtector(receiving);
        sharer.LocalIceCandidate += async (_, candidate) =>
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        viewer.LocalIceCandidate += async (_, candidate) =>
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        var sharerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sharer.DataChannelReady += (_, _) => sharerReady.TrySetResult();
        viewer.DataChannelReady += (_, _) => viewerReady.TrySetResult();
        var decoded = Channel.CreateBounded<RemoteVideoFrame>(4);
        viewer.RemoteFrameReceived += (_, frame) => decoded.Writer.TryWrite(frame);
        var offer = await sharer.CreateOfferAsync();
        await sharer.ApplyRemoteAnswerAsync(await viewer.CreateAnswerAsync(offer));
        await Task.WhenAll(sharerReady.Task, viewerReady.Task).WaitAsync(TimeSpan.FromSeconds(30));
        var peer = (RTCPeerConnection)typeof(WebRtcMediaSession)
            .GetField("_peer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sharer)!;

        foreach (var fixture in frames)
        {
            // Test-only pre-encoded synthetic frames avoid using a Windows encoder ABI on Linux.
            // Production PNQE protection, RTP/DTLS delivery and viewer decode remain unchanged.
            var encrypted = sending.ProtectVideoFrame(Convert.FromBase64String(fixture.Encoded));
            peer.SendVideo(3000, encrypted);
            var frame = await decoded.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(fixture.Width, frame.Width);
            Assert.Equal(fixture.Height, frame.Height);
            Assert.True(frame.PipelineDecodedTimestamp > 0);
            Assert.True(FramePixelConverter.TryConvertToBgra(frame, out var bgra));
            var center = (frame.Width * (frame.Height / 2) + frame.Width / 2) * 4;
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange(Math.Abs(bgra[center + channel] - fixture.Bgr[channel]), 0, 8);
            Assert.Equal(255, bgra[center + 3]);
        }
    }

    [LinuxNativeFact]
    public void DistributionVpxDecodesKnownKeyInterAndResizeFramesAndCanBeRecreated()
    {
        LinuxNativeLibraryBootstrap.Initialize();
        var frames = JsonSerializer.Deserialize<FixtureFrame[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vp8-color-frames.json")))!;
        Assert.Contains(frames, frame => frame.Width == 3840 && frame.Height == 2160);

        for (var lifecycle = 0; lifecycle < 2; lifecycle++)
        {
            using var decoder = new VpxVideoEncoder();
            foreach (var fixture in frames)
            {
                var sample = Assert.Single(decoder.DecodeVideo(Convert.FromBase64String(fixture.Encoded),
                    VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.VP8));
                Assert.Equal((uint)fixture.Width, sample.Width);
                Assert.Equal((uint)fixture.Height, sample.Height);
                Assert.Equal(fixture.Width * fixture.Height * 3, sample.Sample.Length);
                foreach (var index in new[] { 0, fixture.Width * (fixture.Height / 2) + fixture.Width / 2,
                             fixture.Width * fixture.Height - 1 })
                    for (var channel = 0; channel < 3; channel++)
                        Assert.InRange(Math.Abs(sample.Sample[index * 3 + channel] - fixture.Bgr[channel]), 0, 8);
            }
        }
    }

    private sealed record FixtureFrame(int Width, int Height, string Encoded, int[] Bgr);

    private sealed class LinuxNativeFactAttribute : FactAttribute
    {
        public LinuxNativeFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("PEERONQ_LINUX_NATIVE_TEST") != "1")
                Skip = "Requires an actual Linux runtime with distro libvpx9; enable PEERONQ_LINUX_NATIVE_TEST=1.";
        }
    }
}
