using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media;
using PeerOnQ.Media.Codecs;
using PeerOnQ.Media.Pipeline;
using PeerOnQ.Platform.Windows.Media;
using Vortice.MediaFoundation;
using Xunit;

namespace PeerOnQ.EndToEnd.Tests;

[Collection("Hardware video acceptance")]
public sealed class D3D11DecoderOutputTests
{
    [HardwareVideoTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Unsupported_factory_or_manager_keeps_the_first_frame_on_CPU(bool failAttach, bool failDispose)
    {
        var attempts = 0;
        var failed = new FaultingOutput(failAttach: true, failDispose: failDispose);
        using var decoder = new MediaFoundationH264Decoder(() =>
        {
            attempts++;
            if (!failAttach) throw new NotSupportedException("Injected unsupported device.");
            return failed;
        });
        using var cpu = new MediaFoundationH264Decoder(() => null);
        using var encoder = new MediaFoundationH264Encoder();
        var source = HardwareVideoIntegrationTests.SyntheticFrame(1920, 1080);
        var encoded = encoder.EncodeI420(1920, 1080, source, 4000, 30, true);
        try
        {
            Assert.False(decoder.HardwareAccelerated);
            for (var index = 0; index < 2; index++)
            {
                var actual = Assert.Single(decoder.Decode(1920, 1080, encoded, reset: index == 1));
                var expected = Assert.Single(cpu.Decode(1920, 1080, encoded, reset: index == 1));
                Assert.True(actual.Pixels.Span.SequenceEqual(expected.Pixels.Span));
                Assert.False(decoder.HardwareAccelerated);
            }
            Assert.Equal(1, attempts);
            if (failAttach) Assert.Equal(1, failed.Disposals);
        }
        finally { CryptographicOperations.ZeroMemory(source); CryptographicOperations.ZeroMemory(encoded); }
    }

    [HardwareVideoTheory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Initial_or_midstream_readback_failure_recovers_on_CPU_without_retrying_D3D(int failOnCopy, bool failDispose)
    {
        var output = new FaultingOutput(failOnCopy: failOnCopy, failDispose: failDispose);
        var attempts = 0;
        using var decoder = new MediaFoundationH264Decoder(() => { attempts++; return output; });
        using var cpu = new MediaFoundationH264Decoder(() => null);
        using var encoder = new MediaFoundationH264Encoder();
        var source = HardwareVideoIntegrationTests.SyntheticFrame(1920, 1080);
        var key = encoder.EncodeI420(1920, 1080, source, 4000, 30, true);
        var inter = encoder.EncodeI420(1920, 1080, source, 4000, 30, false);
        try
        {
            if (failOnCopy == 2)
            {
                var image = Assert.Single(decoder.DecodeNv12(1920, 1080, key, false));
                CryptographicOperations.ZeroMemory(image.Pixels);
                Assert.True(decoder.HardwareAccelerated);
                Assert.Contains("D3D11 output", decoder.Name);
            }
            Assert.Throws<InvalidOperationException>(() => decoder.DecodeNv12(1920, 1080,
                failOnCopy == 1 ? key : inter, false));
            Assert.False(decoder.HardwareAccelerated);
            Assert.DoesNotContain("D3D11 output", decoder.Name);
            Assert.Equal(1, output.Disposals);
            Assert.Throws<InvalidOperationException>(() => decoder.DecodeNv12(1920, 1080, inter, false));
            Assert.Throws<InvalidOperationException>(() => decoder.DecodeNv12(1920, 1080,
                [0, 0, 1, 0x67, 0x80, 0, 0, 1, 0x68, 0x80, 0, 0, 1, 0x65], false));
            var actual = Assert.Single(decoder.Decode(1920, 1080, key, false));
            var expected = Assert.Single(cpu.Decode(1920, 1080, key, false));
            Assert.True(actual.Pixels.Span.SequenceEqual(expected.Pixels.Span));
            Assert.False(decoder.HardwareAccelerated);
            Assert.Equal(1, attempts);
            decoder.Dispose(); decoder.Dispose();
            Assert.Equal(1, output.Disposals);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(inter);
        }
    }

    [HardwareVideoFact]
    public async Task Midstream_D3D_failure_uses_PLI_and_keeps_the_encrypted_H264_session_connected()
    {
        var faulty = new FaultingOutput(failOnCopy: 2);
        var attempts = 0;
        using var decoder = new MediaFoundationH264Decoder(() => { attempts++; return faulty; });
        var sessionId = SessionId.New();
        await using var capture = new HardwareVideoIntegrationTests.SyntheticVideoCapture();
        await using var sharer = WebRtcMediaSession.CreateSharer(sessionId, capture,
            MediaProfile.For(QualityProfile.Quality),
            encoderSelector: new VideoEncoderSelector(() => true, () => true),
            hardwareH264EncoderFactory: () => new MediaFoundationH264Encoder());
        await using var viewer = WebRtcMediaSession.CreateViewer(sessionId, h264Decoder: decoder);
        var secret = RandomNumberGenerator.GetBytes(64);
        using var sender = new SessionTrafficProtector(secret, sessionId, SessionRole.Sharer);
        using var receiver = new SessionTrafficProtector(secret, sessionId, SessionRole.Viewer);
        CryptographicOperations.ZeroMemory(secret);
        sharer.SetTrafficProtector(sender); viewer.SetTrafficProtector(receiver);
        sharer.LocalIceCandidate += async (_, candidate) =>
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        viewer.LocalIceCandidate += async (_, candidate) =>
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        var frames = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.RemoteFrameReceived += (_, frame) =>
        {
            if (Interlocked.Increment(ref frames) >= 8 && !decoder.HardwareAccelerated)
                recovered.TrySetResult();
        };
        var offer = await sharer.CreateOfferAsync();
        var answer = await viewer.CreateAnswerAsync(offer);
        await sharer.ApplyRemoteAnswerAsync(answer);
        await capture.StartAsync(new CaptureRequest
        {
            Target = new CaptureTargetInfo(CaptureTargetKind.Display, "synthetic", "CPU recovery", 1920, 1080),
        });
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(faulty.SuccessfulCopies >= 1);
        Assert.Equal(1, faulty.Disposals);
        Assert.Equal(1, attempts);
        Assert.True(sharer.IsH264PayloadNegotiated && viewer.IsH264PayloadNegotiated);
        Assert.Equal(MediaConnectionState.Connected, sharer.State);
        Assert.Equal(MediaConnectionState.Connected, viewer.State);
        Assert.False(viewer.GetStatistics().DecoderHardwareAccelerated);
        Assert.True(sharer.GetStatistics().EncoderHardwareAccelerated);
    }

    private sealed class FaultingOutput(int failOnCopy = int.MaxValue, bool failAttach = false, bool failDispose = false)
        : IMediaFoundationD3D11DecoderOutput
    {
        private MediaFoundationD3D11DecoderOutput? _inner;
        private int _copies;
        internal int SuccessfulCopies { get; private set; }
        internal int Disposals { get; private set; }
        public void Attach(IMFTransform transform)
        {
            if (failAttach) throw new NotSupportedException("Injected unsupported manager.");
            (_inner ??= new MediaFoundationD3D11DecoderOutput()).Attach(transform);
        }
        public byte[] Copy(IMFSample sample, int width, int height)
        {
            if (++_copies == failOnCopy) throw new InvalidOperationException("Injected readback failure.");
            var bytes = _inner!.Copy(sample, width, height);
            SuccessfulCopies++;
            return bytes;
        }
        public void Dispose()
        {
            Disposals++; _inner?.Dispose();
            if (failDispose) throw new InvalidOperationException("Injected optional teardown failure.");
        }
    }
}
