using System.Security.Cryptography;
using System.Threading.Channels;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Collaboration;

/// <summary>
/// An explicit, attended system-output grant. Capture and playback both stop on interruption;
/// reconnect never silently restores either local choice. Audio never changes another scope.
/// </summary>
public sealed class SystemAudioSession : IAsyncDisposable
{
    public const int MaximumQueuedFrames = 4;
    public const int MaximumFrameAgeMilliseconds = 250;
    public const int MaximumClockUncertaintyMilliseconds = 50;
    private readonly IMediaSession _media;
    private readonly Func<ISystemAudioCaptureSource>? _captureFactory;
    private readonly Func<ISystemAudioPlaybackSink>? _playbackFactory;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Lock _transmissionGate = new();
    private readonly Lock _playbackGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<CapturedFrame> _frames = Channel.CreateBounded<CapturedFrame>(
        new BoundedChannelOptions(MaximumQueuedFrames)
        {
            SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest,
        }, dropped => CryptographicOperations.ZeroMemory(dropped.Frame.Pcm));
    private readonly Task _sendLoop;
    private ISystemAudioCaptureSource? _capture;
    private ISystemAudioPlaybackSink? _playback;
    private CancellationTokenSource? _captureRequest;
    private int _captureEnabled;
    private int _muted = 1;
    private int _interrupted;
    private int _revoked;
    private int _unavailable;
    private int _disposed;
    private long _lastReceivedSequence;
    private long _nextCaptureSequence;
    private long _captureGeneration;

    private sealed record CapturedFrame(SystemAudioFrame Frame, long Generation);

    public SystemAudioSession(IMediaSession media, SessionPermission permissions,
        Func<ISystemAudioCaptureSource>? captureFactory = null,
        Func<ISystemAudioPlaybackSink>? playbackFactory = null, TimeProvider? timeProvider = null)
    {
        if (!permissions.HasFlag(SessionPermission.SystemAudio) || !permissions.HasFlag(SessionPermission.ViewScreen))
            throw new ArgumentException("An explicit screen and system-audio grant is required.", nameof(permissions));
        _media = media;
        _captureFactory = captureFactory;
        _playbackFactory = playbackFactory;
        _time = timeProvider ?? TimeProvider.System;
        _media.SystemAudioFrameReceived += OnFrameReceived;
        _media.SystemAudioUnavailable += OnUnavailable;
        _sendLoop = SendLoopAsync();
    }

    public bool IsCaptureEnabled => Volatile.Read(ref _captureEnabled) != 0;
    public SessionId SessionId => _media.SessionId;
    public SessionRole Role => _media.Role;
    public bool IsMuted => Volatile.Read(ref _muted) != 0;
    public bool IsAvailable => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _unavailable) == 0
        && Volatile.Read(ref _revoked) == 0 && Volatile.Read(ref _interrupted) == 0;
    public event EventHandler? StateChanged;

    public async Task SetCaptureEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!enabled) { StopCapture(); return; }
        if (_media.Role != SessionRole.Sharer || _captureFactory is null || !IsAvailable)
            throw new InvalidOperationException("System sound is unavailable. Start a new attended session to resume it.");
        CancellationTokenSource request;
        lock (_gate)
        {
            if (_capture is not null) return;
            _captureRequest?.Cancel();
            request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            _captureRequest = request;
        }
        ISystemAudioCaptureSource? capture = null;
        try
        {
            var start = _time.GetTimestamp();
            while (!_media.IsSystemAudioLaneReady)
            {
                if (!IsAvailable || _time.GetElapsedTime(start) >= TimeSpan.FromSeconds(10))
                    throw new InvalidOperationException("The secure system-sound connection did not become available.");
                await Task.Delay(TimeSpan.FromMilliseconds(20), _time, request.Token);
            }
            capture = _captureFactory();
            capture.FrameArrived += OnCaptured;
            capture.CaptureFailed += OnCaptureFailed;
            lock (_transmissionGate)
            lock (_gate)
            {
                request.Token.ThrowIfCancellationRequested();
                if (!IsAvailable || !ReferenceEquals(request, _captureRequest))
                    throw new OperationCanceledException(request.Token);
                _capture = capture;
                capture = null;
                _captureGeneration++;
                _capture.Start();
                if (!IsAvailable) throw new OperationCanceledException(request.Token);
                Volatile.Write(ref _captureEnabled, 1);
                _media.SetSystemAudioCaptureActive(true);
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            StopCapture(request);
            throw;
        }
        finally
        {
            if (capture is not null)
            {
                capture.FrameArrived -= OnCaptured;
                capture.CaptureFailed -= OnCaptureFailed;
                capture.Dispose();
            }
            lock (_gate) { if (ReferenceEquals(_captureRequest, request)) _captureRequest = null; }
            request.Dispose();
        }
    }

    public void SetMuted(bool muted)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_media.Role != SessionRole.Viewer || _playbackFactory is null)
            throw new InvalidOperationException("This session has no system-sound playback.");
        if (!muted && !IsAvailable)
            throw new InvalidOperationException("System sound is unavailable. Start a new attended session to resume it.");
        lock (_playbackGate)
        {
            _playback?.Clear();
            if (!muted && _playback is null)
            {
                _playback = _playbackFactory();
                _playback.PlaybackFailed += OnPlaybackFailed;
            }
            Volatile.Write(ref _muted, muted ? 1 : 0);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ConnectionInterrupted()
    {
        Volatile.Write(ref _interrupted, 1);
        StopCapture();
        FlushPlayback();
    }

    public void ConnectionResumed()
    {
        Volatile.Write(ref _interrupted, 0);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RevokeLocalPermission()
    {
        Volatile.Write(ref _revoked, 1);
        StopCapture();
        FlushPlayback();
        _media.CloseSystemAudioTransmission();
    }

    private void OnUnavailable(object? sender, EventArgs args)
    {
        Volatile.Write(ref _unavailable, 1);
        StopCapture();
        FlushPlayback();
        _media.CloseSystemAudioTransmission();
    }

    private void OnCaptureFailed(object? sender, EventArgs args)
    {
        lock (_transmissionGate)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(sender, _capture) || !IsCaptureEnabled) return;
            }
            OnUnavailable(sender, args);
        }
    }

    private void OnPlaybackFailed(object? sender, EventArgs args)
    {
        lock (_playbackGate) { if (!ReferenceEquals(sender, _playback) || !IsAvailable) return; }
        OnUnavailable(sender, args);
    }

    private void StopCapture(CancellationTokenSource? expectedRequest = null)
    {
        ISystemAudioCaptureSource? capture;
        lock (_transmissionGate)
        {
            lock (_gate)
            {
                if (expectedRequest is not null && !ReferenceEquals(_captureRequest, expectedRequest)) return;
                Volatile.Write(ref _captureEnabled, 0);
                _media.SetSystemAudioCaptureActive(false);
                _captureGeneration++;
                _captureRequest?.Cancel();
                capture = _capture;
                _capture = null;
            }
            if (capture is not null)
            {
                capture.FrameArrived -= OnCaptured;
                capture.CaptureFailed -= OnCaptureFailed;
                try { capture.Stop(); }
                finally { capture.Dispose(); }
            }
            while (_frames.Reader.TryRead(out var frame)) CryptographicOperations.ZeroMemory(frame.Frame.Pcm);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FlushPlayback()
    {
        Volatile.Write(ref _muted, 1);
        lock (_playbackGate) _playback?.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCaptured(object? sender, SystemAudioFrame frame)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _capture) || !IsCaptureEnabled || !IsAvailable
                || frame.Pcm.Length != SystemAudioFrameCodec.PcmBytes)
            {
                CryptographicOperations.ZeroMemory(frame.Pcm);
                return;
            }
            frame = frame with { Sequence = Interlocked.Increment(ref _nextCaptureSequence) };
            if (!_frames.Writer.TryWrite(new CapturedFrame(frame, _captureGeneration)))
                CryptographicOperations.ZeroMemory(frame.Pcm);
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var captured in _frames.Reader.ReadAllAsync(_lifetime.Token))
            {
                var frame = captured.Frame;
                try
                {
                    lock (_transmissionGate)
                    {
                        if (!IsCaptureEnabled || !IsAvailable) continue;
                        if (captured.Generation != _captureGeneration) continue;
                        var age = NowMicroseconds() - frame.CapturedAtUnixMicroseconds;
                        if (age is < 0 or > MaximumQueuedFrames * SystemAudioFrameCodec.FrameMilliseconds * 1000L) continue;
                        var encoded = SystemAudioFrameCodec.Encode(frame);
                        try { _media.TrySendSystemAudioFrame(encoded); }
                        finally { CryptographicOperations.ZeroMemory(encoded); }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
                {
                    OnUnavailable(this, EventArgs.Empty);
                }
                finally { CryptographicOperations.ZeroMemory(frame.Pcm); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void OnFrameReceived(object? sender, ReadOnlyMemory<byte> payload)
    {
        if (_media.Role != SessionRole.Viewer || !IsAvailable
            || !SystemAudioFrameCodec.TryDecode(payload.Span, out var frame)) return;
        try
        {
            if (frame!.Sequence <= Interlocked.Read(ref _lastReceivedSequence)) return;
            Interlocked.Exchange(ref _lastReceivedSequence, frame.Sequence);
            var estimate = _media.GetPeerClockEstimate();
            if (estimate is null || estimate.Value.UncertaintyMicroseconds < 0
                || estimate.Value.UncertaintyMicroseconds > MaximumClockUncertaintyMilliseconds * 1000L) return;
            var age = NowMicroseconds() + estimate.Value.RemoteMinusLocalOffsetMicroseconds - frame.CapturedAtUnixMicroseconds;
            if (age < -estimate.Value.UncertaintyMicroseconds
                || age + estimate.Value.UncertaintyMicroseconds > MaximumFrameAgeMilliseconds * 1000L) return;
            lock (_playbackGate)
            {
                if (!IsMuted && IsAvailable) _playback?.Write(frame);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            OnUnavailable(this, EventArgs.Empty);
        }
        finally { CryptographicOperations.ZeroMemory(frame!.Pcm); }
    }

    private long NowMicroseconds() => (_time.GetUtcNow().Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _media.SystemAudioFrameReceived -= OnFrameReceived;
        _media.SystemAudioUnavailable -= OnUnavailable;
        StopCapture();
        FlushPlayback();
        _frames.Writer.TryComplete();
        _lifetime.Cancel();
        await _sendLoop;
        lock (_playbackGate)
        {
            if (_playback is not null)
            {
                _playback.PlaybackFailed -= OnPlaybackFailed;
                _playback.Dispose();
                _playback = null;
            }
        }
        _lifetime.Dispose();
    }
}
