using System.Diagnostics;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Media.Tests;

public partial class WebRtcLoopbackTests
{
    [Fact]
    public async Task System_audio_uses_its_own_authenticated_association_and_revocation_keeps_screen_transport_alive()
    {
        var scope = SessionPermission.ViewScreen | SessionPermission.SystemAudio;
        var id = SessionId.New();
        await using var sharer = WebRtcMediaSession.CreateSharer(id, null, MediaProfile.Conservative, permissions: scope);
        await using var viewer = WebRtcMediaSession.CreateViewer(id, permissions: scope);
        BindIce(sharer, viewer);
        await using var identities = await TestHybridIdentityPair.CreateAsync();
        await using var viewerTransport = new MediaCollaborationTransport(viewer, scope, identities.Viewer, identities.SharerLegacyFingerprint);
        await using var sharerTransport = new MediaCollaborationTransport(sharer, scope, identities.Sharer, identities.ViewerLegacyFingerprint);
        var offer = await sharer.CreateOfferAsync();
        Assert.Contains("a=x-peeronq-system-audio:pcm16-48000-stereo-v1", offer, StringComparison.Ordinal);
        var answer = await viewer.CreateAnswerAsync(offer);
        await sharer.ApplyRemoteAnswerAsync(answer);
        await WaitForAudioAsync(() => viewerTransport.IsReady && sharerTransport.IsReady
            && sharer.IsSystemAudioLaneReady && viewer.IsSystemAudioLaneReady);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.SystemAudioFrameReceived += (_, memory) => received.TrySetResult(memory.ToArray());
        var frame = SystemAudioFrameCodec.Encode(new SystemAudioFrame(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, 1, new byte[SystemAudioFrameCodec.PcmBytes]));
        Assert.Equal(0, sharer.GetStatistics().SystemAudioReservationKbps);
        sharer.SetSystemAudioCaptureActive(true);
        Assert.Equal(0, sharer.GetStatistics().SystemAudioReservationKbps);
        Assert.False(viewer.TrySendSystemAudioFrame(frame));
        Assert.False(sharer.TrySendSystemAudioFrame(frame.AsSpan(0, frame.Length - 1)));
        Assert.True(sharer.TrySendSystemAudioFrame(frame));
        Assert.Equal(frame, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitForAudioAsync(() => sharer.GetStatistics().SystemAudioOutstandingFrames == 0);
        Assert.Equal(1800, sharer.GetStatistics().SystemAudioReservationKbps);
        Assert.InRange(sharer.GetStatistics().SystemAudioBufferedBytes, 0, (long)WebRtcMediaSession.MaximumAudioBufferedBytes);
        sharer.SetSystemAudioCaptureActive(false);
        Assert.Equal(0, sharer.GetStatistics().SystemAudioReservationKbps);
        sharer.CloseSystemAudioTransmission();
        await WaitForAudioAsync(() => !viewer.IsSystemAudioLaneReady);
        Assert.False(sharer.IsSystemAudioLaneReady);
        Assert.Equal(MediaConnectionState.Connected, sharer.State);
        Assert.Equal(MediaConnectionState.Connected, viewer.State);
        Assert.True(viewerTransport.IsReady);
        Assert.True(sharerTransport.IsReady);
        Assert.False(sharer.TrySendSystemAudioFrame(frame));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tampered_or_future_receipt_closes_only_audio_and_quiet_missing_ACKs_cannot_wait_forever(bool futureReceipt)
    {
        var scope = SessionPermission.ViewScreen | SessionPermission.SystemAudio;
        var id = SessionId.New();
        await using var sharer = WebRtcMediaSession.CreateSharer(id, null, MediaProfile.Conservative, permissions: scope);
        await using var viewer = WebRtcMediaSession.CreateViewer(id, permissions: scope);
        BindIce(sharer, viewer);
        var master = RandomNumberGenerator.GetBytes(64);
        using var senderProtection = new SessionTrafficProtector(master, id, SessionRole.Sharer);
        using var receiverProtection = new SessionTrafficProtector(master, id, SessionRole.Viewer);
        CryptographicOperations.ZeroMemory(master);
        sharer.SetTrafficProtector(senderProtection);
        viewer.SetTrafficProtector(new FaultyAudioReceiptProtector(receiverProtection, futureReceipt));
        var offer = await sharer.CreateOfferAsync();
        await sharer.ApplyRemoteAnswerAsync(await viewer.CreateAnswerAsync(offer));
        await WaitForAudioAsync(() => sharer.IsSystemAudioLaneReady && viewer.IsSystemAudioLaneReady);
        var frame = SystemAudioFrameCodec.Encode(new SystemAudioFrame(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, 1, new byte[SystemAudioFrameCodec.PcmBytes]));
        Assert.True(sharer.TrySendSystemAudioFrame(frame));
        // No further frames are sent. The watchdog must handle a quiet source too.
        await WaitForAudioAsync(() => !sharer.IsSystemAudioLaneReady);
        Assert.Equal(0, sharer.GetStatistics().SystemAudioOutstandingFrames);
        Assert.Equal(MediaConnectionState.Connected, sharer.State);
        Assert.Equal(MediaConnectionState.Connected, viewer.State);
    }

    [Fact]
    public async Task System_audio_and_input_remain_live_while_the_separate_bulk_lane_is_busy()
    {
        var scope = Phase1SessionScope.FullControlPermissions | SessionPermission.SystemAudio;
        var id = SessionId.New();
        await using var sharer = WebRtcMediaSession.CreateSharer(id, null, MediaProfile.Conservative, permissions: scope);
        await using var viewer = WebRtcMediaSession.CreateViewer(id, permissions: scope);
        BindIce(sharer, viewer);
        await using var identities = await TestHybridIdentityPair.CreateAsync();
        await using var viewerTransport = new MediaCollaborationTransport(viewer, scope, identities.Viewer, identities.SharerLegacyFingerprint);
        await using var sharerTransport = new MediaCollaborationTransport(sharer, scope, identities.Sharer, identities.ViewerLegacyFingerprint);
        var sink = new FakeRemoteInputSink();
        await using var inputViewer = new RemoteInputSession(id, SessionRole.Viewer, viewerTransport);
        await using var inputSharer = new RemoteInputSession(id, SessionRole.Sharer, sharerTransport, sink);
        var offer = await sharer.CreateOfferAsync();
        await sharer.ApplyRemoteAnswerAsync(await viewer.CreateAnswerAsync(offer));
        await WaitForAudioAsync(() => viewerTransport.IsReady && sharerTransport.IsReady
            && sharer.IsSystemAudioLaneReady && viewer.IsSystemAudioLaneReady
            && sharer.IsBulkDataLaneReady && viewer.IsBulkDataLaneReady
            && sharer.IsInputDataLaneReady && viewer.IsInputDataLaneReady);
        await inputViewer.SetLocalCaptureEnabledAsync(true);
        var audioFrames = 0;
        long bulkBytes = 0;
        viewer.SystemAudioFrameReceived += (_, _) => Interlocked.Increment(ref audioFrames);
        viewerTransport.MessageReceived += (_, message) =>
        {
            if (message is TransferChunk chunk) Interlocked.Add(ref bulkBytes, chunk.Payload.Length);
        };
        var payload = new byte[64 * 1024];
        var hash = Convert.ToHexStringLower(SHA256.HashData(payload));
        var transferId = Guid.NewGuid();
        using var stop = new CancellationTokenSource();
        var bulk = Task.Run(async () =>
        {
            for (long index = 0; ; index++)
                await sharerTransport.SendAsync(new TransferChunk
                {
                    TransferId = transferId, RelativePath = "audio-contention.bin", Offset = index * payload.Length,
                    Index = index, ChunkSha256 = hash, Payload = payload,
                }, stop.Token);
        });
        try
        {
            await WaitForAudioAsync(() => Interlocked.Read(ref bulkBytes) > 0);
            sharer.SetSystemAudioCaptureActive(true);
            for (var index = 1; index <= 10; index++)
            {
                var encoded = SystemAudioFrameCodec.Encode(new SystemAudioFrame(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, index, new byte[SystemAudioFrameCodec.PcmBytes]));
                sharer.TrySendSystemAudioFrame(encoded);
                await inputViewer.SendKeyAsync((ushort)(0x41 + index), true, false);
                await Task.Delay(20);
            }
            await WaitForAudioAsync(() => Volatile.Read(ref audioFrames) >= 5 && sink.Inputs.Count == 10);
            Assert.False(bulk.IsCompleted);
            Assert.InRange(sharer.GetStatistics().SystemAudioOutstandingFrames, 0, 12);
            Assert.InRange(sharer.GetStatistics().SystemAudioOutstandingBytes, 0, 50 * 1024);
            Assert.InRange(sharer.GetStatistics().SystemAudioBufferedBytes, 0, (long)WebRtcMediaSession.MaximumAudioBufferedBytes);
            Assert.Equal(MediaConnectionState.Connected, sharer.State);
        }
        finally
        {
            stop.Cancel();
            try { await bulk; } catch (OperationCanceledException) { }
            sharer.SetSystemAudioCaptureActive(false);
        }
        // This is functional liveness/buffer evidence. The separate existing 35ms latency
        // theories retain their original, stricter performance assertions unchanged.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_or_older_peer_never_receives_audio_on_the_primary_channel(bool hostRequestsAudio)
    {
        var id = SessionId.New();
        var scope = SessionPermission.ViewScreen | (hostRequestsAudio ? SessionPermission.SystemAudio : SessionPermission.None);
        await using var sharer = WebRtcMediaSession.CreateSharer(id, null, MediaProfile.Conservative, permissions: scope);
        await using var viewer = WebRtcMediaSession.CreateViewer(id, permissions: SessionPermission.ViewScreen);
        BindIce(sharer, viewer);
        var offer = await sharer.CreateOfferAsync();
        var answer = await viewer.CreateAnswerAsync(offer);
        Assert.DoesNotContain("a=x-peeronq-system-audio:", answer, StringComparison.Ordinal);
        await sharer.ApplyRemoteAnswerAsync(answer);
        await WaitForAudioAsync(() => sharer.State == MediaConnectionState.Connected && viewer.State == MediaConnectionState.Connected);
        Assert.False(sharer.IsSystemAudioLaneReady);
        Assert.False(viewer.IsSystemAudioLaneReady);
        Assert.False(sharer.TrySendSystemAudioFrame(new byte[SystemAudioFrameCodec.EncodedBytes]));
    }

    private static void BindIce(WebRtcMediaSession sharer, WebRtcMediaSession viewer)
    {
        sharer.LocalIceCandidate += async (_, candidate) =>
            await viewer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        viewer.LocalIceCandidate += async (_, candidate) =>
            await sharer.AddRemoteIceCandidateAsync(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
    }

    private static async Task WaitForAudioAsync(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Audio loopback did not reach its bounded state.");
            await Task.Delay(10);
        }
    }

    private sealed class FaultyAudioReceiptProtector(SessionTrafficProtector inner, bool futureReceipt) : ISessionTrafficProtector
    {
        public bool IsEstablished => inner.IsEstablished;
        public byte[] ProtectVideoFrame(ReadOnlySpan<byte> frame) => inner.ProtectVideoFrame(frame);
        public bool TryUnprotectVideoFrame(ReadOnlySpan<byte> frame, out byte[] plaintext, out string? error) =>
            inner.TryUnprotectVideoFrame(frame, out plaintext, out error);
        public byte[] ProtectAudioFrame(ReadOnlySpan<byte> frame)
        {
            var receipt = futureReceipt ? SystemAudioReceiptCodec.Encode(999) : frame.ToArray();
            var protectedReceipt = inner.ProtectAudioFrame(receipt);
            if (!futureReceipt) protectedReceipt[^1] ^= 1;
            return protectedReceipt;
        }
        public bool TryUnprotectAudioFrame(ReadOnlySpan<byte> frame, out byte[] plaintext, out string? error) =>
            inner.TryUnprotectAudioFrame(frame, out plaintext, out error);
    }
}
