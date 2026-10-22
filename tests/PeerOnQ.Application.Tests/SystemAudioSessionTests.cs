using System.Buffers.Binary;
using System.Collections.Concurrent;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Sessions;
using Xunit;

namespace PeerOnQ.Application.Tests;

public sealed class SystemAudioSessionTests
{
    private const SessionPermission Scope = SessionPermission.ViewScreen | SessionPermission.SystemAudio;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static long NowUs => (Now.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;

    [Fact]
    public void Audio_is_never_a_default_or_an_unattended_or_screenless_grant()
    {
        foreach (var mode in new[] { SessionMode.ViewOnly, SessionMode.FullControl, SessionMode.FileTransferOnly })
            Assert.False(SessionPermissionPolicy.ForMode(mode).HasFlag(SessionPermission.SystemAudio));
        Assert.False(SessionPermissionPolicy.IsValid(SessionMode.Custom, SessionPermission.SystemAudio));
        Assert.True(Phase1SessionScope.IsAllowed(SessionMode.ViewOnly, Scope, SessionAccessKind.Attended));
        Assert.False(Phase1SessionScope.IsAllowed(SessionMode.ViewOnly, Scope, SessionAccessKind.Unattended));
        Assert.False(Phase1SessionScope.IsAllowed(SessionMode.ViewOnly, Scope, SessionAccessKind.SupportInvitation));
        Assert.False(Phase1SessionScope.IsRemoteScopeSelectionGrant(SessionMode.ViewOnly, Scope, SessionPermission.ViewScreen));
        Assert.True(Phase1SessionScope.IsRemoteScopeSelectionGrant(SessionMode.ViewOnly, SessionPermission.ViewScreen,
            Phase1SessionScope.FullControlPermissions | SessionPermission.SystemAudio));
    }

    [Fact]
    public void Codec_rejects_peer_selected_formats_lengths_and_trailing_bytes()
    {
        var frame = Frame(1);
        var encoded = SystemAudioFrameCodec.Encode(frame);
        Assert.Equal(3_840, frame.Pcm.Length);
        Assert.True(SystemAudioFrameCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(frame.Sequence, decoded!.Sequence);
        Assert.Equal(frame.Pcm, decoded.Pcm);
        Assert.False(SystemAudioFrameCodec.TryDecode(encoded[..^1], out _));
        Assert.False(SystemAudioFrameCodec.TryDecode([.. encoded, 0], out _));
        BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(8), 192_000);
        Assert.False(SystemAudioFrameCodec.TryDecode(encoded, out _));
    }

    [Fact]
    public void Playback_buffer_prebuffers_40ms_discards_oldest_at_100ms_and_clears_on_mute()
    {
        var buffer = new SystemAudioPlaybackBuffer();
        var pcm = Enumerable.Repeat((byte)1, SystemAudioFrameCodec.PcmBytes).ToArray();
        var output = new byte[pcm.Length];
        buffer.Add(pcm);
        buffer.Read(output);
        Assert.All(output, value => Assert.Equal(0, value));
        Assert.Equal(pcm.Length, buffer.BufferedBytes);
        buffer.Add(pcm);
        buffer.Read(output);
        Assert.All(output, value => Assert.Equal(1, value));
        buffer.Clear();
        for (byte i = 1; i <= 9; i++) buffer.Add(Enumerable.Repeat(i, pcm.Length).ToArray());
        Assert.Equal(SystemAudioPlaybackBuffer.CapacityBytes, buffer.BufferedBytes);
        buffer.Read(output);
        Assert.All(output, value => Assert.Equal(5, value));
        buffer.Clear();
        buffer.Read(output);
        Assert.All(output, value => Assert.Equal(0, value));
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public async Task Capture_requires_local_enable_and_drops_old_queued_PCM_before_transport_admission()
    {
        var media = new FakeMedia(SessionRole.Sharer) { BlockFirstSend = true };
        var capture = new FakeCapture();
        await using var audio = new SystemAudioSession(media, Scope, () => capture, timeProvider: new FrozenTime());
        Assert.False(audio.IsCaptureEnabled);
        Assert.Equal(0, capture.StartCalls);
        await audio.SetCaptureEnabledAsync(true);
        var frames = Enumerable.Range(1, 11).Select(index => Frame(index)).ToArray();
        capture.Emit(frames[0]);
        await media.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            foreach (var frame in frames.Skip(1)) capture.Emit(frame);
            foreach (var dropped in frames.Skip(1).Take(6)) Assert.All(dropped.Pcm, value => Assert.Equal(0, value));
        }
        finally { media.SendRelease.Set(); }
        await WaitUntilAsync(() => media.SentSequences.Count == 5);
        Assert.Equal(new long[] { 1, 8, 9, 10, 11 }, media.SentSequences.ToArray());
        audio.ConnectionInterrupted();
        Assert.False(audio.IsCaptureEnabled);
        Assert.Equal(1, capture.StopCalls);
        audio.ConnectionResumed();
        Assert.False(audio.IsCaptureEnabled);
        Assert.Equal(1, capture.StartCalls);
    }

    [Fact]
    public async Task Borrowed_media_memory_is_copied_synchronously_and_clock_uncertainty_fails_closed()
    {
        var media = new FakeMedia(SessionRole.Viewer);
        var sink = new FakePlayback();
        await using var audio = new SystemAudioSession(media, Scope, playbackFactory: () => sink, timeProvider: new FrozenTime());
        Assert.True(audio.IsMuted);
        audio.SetMuted(false);
        media.Emit(Frame(1));
        Assert.Empty(sink.Written);
        media.Clock = new PeerClockEstimate(0, 51_000);
        media.Emit(Frame(2));
        media.Clock = new PeerClockEstimate(0, 0);
        media.Emit(Frame(3) with { CapturedAtUnixMicroseconds = NowUs - 251_000 });
        media.Emit(Frame(4) with { CapturedAtUnixMicroseconds = NowUs + 1 });
        Assert.Empty(sink.Written);
        media.Emit(Frame(5));
        Assert.Single(sink.Written);
        Assert.All(sink.Written[0], value => Assert.Equal(5, value));
        audio.SetMuted(true);
        Assert.True(sink.ClearCalls > 0);
        media.Emit(Frame(6));
        Assert.Single(sink.Written);
        audio.SetMuted(false);
        audio.ConnectionInterrupted();
        Assert.True(audio.IsMuted);
        audio.ConnectionResumed();
        Assert.True(audio.IsMuted);
        media.Emit(Frame(7));
        Assert.Single(sink.Written);
    }

    [Fact]
    public async Task Revocation_closes_only_audio_and_cannot_be_restored_by_reconnect()
    {
        var media = new FakeMedia(SessionRole.Sharer);
        var capture = new FakeCapture();
        await using var audio = new SystemAudioSession(media, Scope, () => capture, timeProvider: new FrozenTime());
        await audio.SetCaptureEnabledAsync(true);
        audio.RevokeLocalPermission();
        Assert.False(audio.IsCaptureEnabled);
        Assert.False(audio.IsAvailable);
        Assert.Equal(1, media.AudioCloseCalls);
        Assert.Equal(MediaConnectionState.Connected, media.State);
        audio.ConnectionResumed();
        await Assert.ThrowsAsync<InvalidOperationException>(() => audio.SetCaptureEnabledAsync(true));
        Assert.Equal(1, capture.DisposeCalls);
    }

    [Fact]
    public async Task Canceled_deferred_enable_cannot_cancel_a_later_local_enable_request()
    {
        var media = new FakeMedia(SessionRole.Sharer) { AudioReady = false };
        var capture = new FakeCapture();
        await using var audio = new SystemAudioSession(media, Scope, () => capture);
        var context = new QueuedContext();
        var original = SynchronizationContext.Current;
        Task first;
        Task second;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            first = audio.SetCaptureEnabledAsync(true);
            await audio.SetCaptureEnabledAsync(false);
            second = audio.SetCaptureEnabledAsync(true);
            media.AudioReady = true;
            context.PumpUntil(() => first.IsCompleted && second.IsCompleted);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await second;
        Assert.True(audio.IsCaptureEnabled);
        Assert.Equal(1, capture.StartCalls);
        Assert.Equal(0, capture.StopCalls);
    }

    [Fact]
    public async Task Already_captured_old_source_delegates_cannot_send_PCM_or_stop_a_replacement_source()
    {
        var media = new FakeMedia(SessionRole.Sharer);
        var first = new FakeCapture();
        var second = new FakeCapture();
        var sources = new Queue<FakeCapture>([first, second]);
        await using var audio = new SystemAudioSession(media, Scope, () => sources.Dequeue(), timeProvider: new FrozenTime());
        await audio.SetCaptureEnabledAsync(true);
        var lateFrame = first.SnapshotFrameHandler();
        var lateFailure = first.SnapshotFailureHandler();
        await audio.SetCaptureEnabledAsync(false);
        await audio.SetCaptureEnabledAsync(true);
        var stale = Frame(1);
        lateFrame(stale);
        lateFailure();
        Assert.All(stale.Pcm, value => Assert.Equal(0, value));
        Assert.True(audio.IsCaptureEnabled);
        Assert.True(audio.IsAvailable);
        Assert.Equal(0, second.StopCalls);
        second.Emit(Frame(2));
        await WaitUntilAsync(() => media.SentSequences.Count == 1);
        Assert.Equal(new long[] { 1 }, media.SentSequences.ToArray());
        Assert.Equal(0, media.AudioCloseCalls);
    }

    [Fact]
    public async Task Playback_device_failure_immediately_mutes_flushes_and_closes_only_audio()
    {
        var media = new FakeMedia(SessionRole.Viewer);
        var sink = new FakePlayback();
        await using var audio = new SystemAudioSession(media, Scope, playbackFactory: () => sink);
        audio.SetMuted(false);
        sink.Fail();
        Assert.True(audio.IsMuted);
        Assert.False(audio.IsAvailable);
        Assert.True(sink.ClearCalls > 0);
        Assert.Equal(1, media.AudioCloseCalls);
        Assert.Equal(MediaConnectionState.Connected, media.State);
        Assert.Throws<InvalidOperationException>(() => audio.SetMuted(false));
    }

    [Fact]
    public async Task Failed_capture_start_never_marks_capture_or_outgoing_reservation_active()
    {
        var media = new FakeMedia(SessionRole.Sharer);
        var capture = new FakeCapture { FailStart = true };
        await using var audio = new SystemAudioSession(media, Scope, () => capture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => audio.SetCaptureEnabledAsync(true));
        Assert.False(audio.IsCaptureEnabled);
        Assert.False(media.CaptureActive);
        Assert.Equal(0, media.CaptureActivations);
        Assert.Equal(1, capture.StopCalls);
        Assert.Equal(1, capture.DisposeCalls);
    }

    private static SystemAudioFrame Frame(int sequence) => new(NowUs, sequence,
        Enumerable.Repeat((byte)sequence, SystemAudioFrameCodec.PcmBytes).ToArray());

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private sealed class FrozenTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void PumpUntil(Func<bool> done)
        {
            var limit = DateTime.UtcNow.AddSeconds(2);
            while (!done())
            {
                if (DateTime.UtcNow > limit) throw new TimeoutException("Deferred audio enable did not complete.");
                if (_callbacks.TryDequeue(out var callback)) callback.Callback(callback.State);
                else Thread.Sleep(1);
            }
        }
    }

    private sealed class FakeCapture : ISystemAudioCaptureSource
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool FailStart { get; init; }
        public event EventHandler<SystemAudioFrame>? FrameArrived;
        public event EventHandler? CaptureFailed;
        public void Start() { StartCalls++; if (FailStart) throw new InvalidOperationException("No audio endpoint."); }
        public void Stop() => StopCalls++;
        public void Dispose() => DisposeCalls++;
        public void Emit(SystemAudioFrame frame) => FrameArrived?.Invoke(this, frame);
        public Action<SystemAudioFrame> SnapshotFrameHandler()
        {
            var handler = FrameArrived;
            return frame => handler?.Invoke(this, frame);
        }
        public Action SnapshotFailureHandler()
        {
            var handler = CaptureFailed;
            return () => handler?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakePlayback : ISystemAudioPlaybackSink
    {
        public List<byte[]> Written { get; } = [];
        public int ClearCalls { get; private set; }
        public event EventHandler? PlaybackFailed;
        public void Write(SystemAudioFrame frame) => Written.Add(frame.Pcm.ToArray());
        public void Clear() => ClearCalls++;
        public void Dispose() { }
        public void Fail() => PlaybackFailed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeMedia(SessionRole role) : IMediaSession
    {
        public SessionId SessionId { get; } = SessionId.New();
        public SessionRole Role => role;
        public MediaConnectionState State => MediaConnectionState.Connected;
        public bool AudioReady { get; set; } = true;
        public bool IsSystemAudioLaneReady => AudioReady;
        public bool BlockFirstSend { get; init; }
        public PeerClockEstimate? Clock { get; set; }
        public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim SendRelease { get; } = new();
        public ConcurrentQueue<long> SentSequences { get; } = new();
        public int AudioCloseCalls { get; private set; }
        public bool CaptureActive { get; private set; }
        public int CaptureActivations { get; private set; }
        public event EventHandler<MediaConnectionState>? StateChanged { add { } remove { } }
        public event EventHandler<(string Candidate, string? SdpMid, ushort SdpMLineIndex)>? LocalIceCandidate { add { } remove { } }
        public event EventHandler<RemoteVideoFrame>? RemoteFrameReceived { add { } remove { } }
        public event EventHandler<ReadOnlyMemory<byte>>? SystemAudioFrameReceived;
        public event EventHandler? SystemAudioUnavailable { add { } remove { } }
        public PeerClockEstimate? GetPeerClockEstimate() => Clock;
        public bool TrySendSystemAudioFrame(ReadOnlySpan<byte> encoded)
        {
            if (BlockFirstSend && SentSequences.Count == 0)
            {
                SendEntered.TrySetResult();
                if (!SendRelease.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException();
            }
            Assert.True(SystemAudioFrameCodec.TryDecode(encoded, out var frame));
            SentSequences.Enqueue(frame!.Sequence);
            return true;
        }
        public void Emit(SystemAudioFrame frame)
        {
            var borrowed = SystemAudioFrameCodec.Encode(frame);
            SystemAudioFrameReceived?.Invoke(this, borrowed);
            Array.Clear(borrowed);
        }
        public void CloseSystemAudioTransmission() => AudioCloseCalls++;
        public void SetSystemAudioCaptureActive(bool active) { CaptureActive = active; if (active) CaptureActivations++; }
        public Task<string> CreateOfferAsync(CancellationToken cancellationToken = default) => Task.FromResult("offer");
        public Task<string> CreateAnswerAsync(string offer, CancellationToken cancellationToken = default) => Task.FromResult("answer");
        public Task ApplyRemoteAnswerAsync(string answer, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddRemoteIceCandidateAsync(string candidate, string? mid, ushort index, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public MediaStatistics GetStatistics() => MediaStatistics.Empty;
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
