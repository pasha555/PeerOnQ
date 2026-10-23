using System.Collections.Concurrent;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Media.Tests;

public partial class WebRtcLoopbackTests
{
    [Fact]
    public async Task Two_way_voice_flows_encrypted_and_plays_only_where_the_local_user_enabled_it()
    {
        var sessionId = SessionId.New();
        var permissions = SessionPermission.ViewScreen;
        await using var capture = new SyntheticCaptureSource();
        await using var sharer = WebRtcMediaSession.CreateSharer(sessionId, capture, MediaProfile.Conservative,
            permissions: permissions);
        await using var viewer = WebRtcMediaSession.CreateViewer(sessionId, permissions: permissions);
        var sharerDevices = new FakeVoiceDevices();
        var viewerDevices = new FakeVoiceDevices();
        sharer.ConfigureVoiceDevices(sharerDevices.CreateCapture, sharerDevices.CreatePlayback);
        viewer.ConfigureVoiceDevices(viewerDevices.CreateCapture, viewerDevices.CreatePlayback);
        sharer.LocalIceCandidate += async (_, candidate) =>
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        viewer.LocalIceCandidate += async (_, candidate) =>
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);

        var offer = await sharer.CreateOfferAsync();
        var answer = await viewer.CreateAnswerAsync(offer);
        await sharer.ApplyRemoteAnswerAsync(answer);
        await using var identities = await TestHybridIdentityPair.CreateAsync();
        await using var viewerTransport = new MediaCollaborationTransport(
            viewer, permissions, identities.Viewer, identities.SharerLegacyFingerprint);
        await using var sharerTransport = new MediaCollaborationTransport(
            sharer, permissions, identities.Sharer, identities.ViewerLegacyFingerprint);
        await WaitUntilAsync(() => viewerTransport.IsReady && sharerTransport.IsReady);
        await WaitUntilAsync(() => viewer.IsVoiceLaneReady && sharer.IsVoiceLaneReady,
            "The encrypted voice lane did not open on both peers.");

        // Only the viewer opts in: its microphone opens, the sharer's devices stay closed and
        // nothing is played on the sharer.
        Assert.True(viewer.SetVoiceEnabled(true));
        Assert.True(viewer.IsVoiceEnabled);
        Assert.False(sharer.IsVoiceEnabled);
        viewerDevices.Capture!.EmitTone(frames: 10, frequency: 440);
        await Task.Delay(500);
        Assert.Equal(0, sharerDevices.CapturesCreated);
        Assert.Equal(0, sharerDevices.PlaybacksCreated);

        // With both users opted in, speech crosses in both directions.
        Assert.True(sharer.SetVoiceEnabled(true));
        viewerDevices.Capture.EmitTone(frames: 50, frequency: 440);
        sharerDevices.Capture!.EmitTone(frames: 50, frequency: 880);
        await WaitUntilAsync(() => sharerDevices.Playback!.SampleCount >= 40 * VoiceAudioFormat.SamplesPerFrame
                                   && viewerDevices.Playback!.SampleCount >= 40 * VoiceAudioFormat.SamplesPerFrame,
            "Voice did not reach both speakers.");

        // Opus is lossy; the decoded tone keeps its energy and pitch.
        AssertTone(sharerDevices.Playback!.Samples, expectedFrequency: 440);
        AssertTone(viewerDevices.Playback!.Samples, expectedFrequency: 880);

        Assert.True(viewer.SetVoiceEnabled(false));
        Assert.False(viewer.IsVoiceEnabled);
        Assert.True(viewerDevices.Capture.Disposed);
        Assert.True(viewerDevices.Playback.Disposed);
    }

    private static void AssertTone(short[] samples, double expectedFrequency)
    {
        // Skip the codec start-up and measure the steady middle of the received speech.
        var steady = samples.AsSpan(samples.Length / 4, samples.Length / 2);
        double energy = 0;
        var crossings = 0;
        for (var index = 0; index < steady.Length; index++)
        {
            energy += (double)steady[index] * steady[index];
            if (index > 0 && (steady[index - 1] < 0) != (steady[index] < 0)) crossings++;
        }
        var rms = Math.Sqrt(energy / steady.Length);
        var frequency = crossings / 2d / ((double)steady.Length / VoiceAudioFormat.SampleRate);
        Assert.InRange(rms, 2_000, 20_000);
        Assert.InRange(frequency, expectedFrequency * 0.9, expectedFrequency * 1.1);
    }

    private sealed class FakeVoiceDevices
    {
        public int CapturesCreated;
        public int PlaybacksCreated;
        public FakeVoiceCapture? Capture { get; private set; }
        public FakeVoicePlayback? Playback { get; private set; }

        public IVoiceCaptureSource CreateCapture()
        {
            Interlocked.Increment(ref CapturesCreated);
            return Capture = new FakeVoiceCapture();
        }

        public IVoicePlaybackSink CreatePlayback()
        {
            Interlocked.Increment(ref PlaybacksCreated);
            return Playback = new FakeVoicePlayback();
        }
    }

    private sealed class FakeVoiceCapture : IVoiceCaptureSource
    {
        private long _phase;
        public bool Disposed { get; private set; }
        public event EventHandler<VoicePcmFrame>? FrameArrived;
        public event EventHandler? CaptureFailed { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() => Disposed = true;

        public void EmitTone(int frames, double frequency)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var samples = new short[VoiceAudioFormat.SamplesPerFrame];
                for (var index = 0; index < samples.Length; index++, _phase++)
                    samples[index] = (short)(12_000 * Math.Sin(2 * Math.PI * frequency * _phase / VoiceAudioFormat.SampleRate));
                var capturedAt = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
                FrameArrived?.Invoke(this, new VoicePcmFrame(capturedAt, samples));
                // Real microphones deliver one frame every 20 ms; keep the lane uncongested.
                Thread.Sleep(VoiceAudioFormat.FrameMilliseconds);
            }
        }
    }

    private sealed class FakeVoicePlayback : IVoicePlaybackSink
    {
        private readonly ConcurrentQueue<short[]> _frames = new();
        public bool Disposed { get; private set; }
        public event EventHandler? PlaybackFailed { add { } remove { } }
        public int SampleCount => _frames.Sum(frame => frame.Length);
        public short[] Samples => _frames.SelectMany(frame => frame).ToArray();
        public void Write(ReadOnlySpan<short> samples) => _frames.Enqueue(samples.ToArray());
        public void Clear() => _frames.Clear();
        public void Dispose() => Disposed = true;
    }
}
