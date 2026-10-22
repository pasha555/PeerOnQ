using System.Diagnostics;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Platform.Windows.Media;

/// <summary>WASAPI render-loopback only. No microphone endpoint is ever opened.</summary>
public sealed class WindowsSystemAudioCapture : ISystemAudioCaptureSource
{
    private readonly Lock _gate = new();
    private readonly byte[] _partial = new byte[SystemAudioFrameCodec.PcmBytes];
    private WasapiRecorder? _recorder;
    private int _partialBytes;
    private long _partialTimestamp;
    private long _sequence;
    private int _enabled;
    private int _disposed;

    public event EventHandler<SystemAudioFrame>? FrameArrived;
    public event EventHandler? CaptureFailed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_recorder is not null) return;
        var recorder = new WasapiRecorderBuilder().WithLoopbackCapture().WithSharedMode()
            .WithFormat(new WaveFormat(SystemAudioFrameCodec.SampleRate, SystemAudioFrameCodec.BitsPerSample, SystemAudioFrameCodec.Channels))
            .WithBufferLength(SystemAudioFrameCodec.FrameMilliseconds).WithMmcssThreadPriority().Build();
        _recorder = recorder;
        recorder.DataAvailable += OnDataAvailable;
        recorder.RecordingStopped += OnRecordingStopped;
        Volatile.Write(ref _enabled, 1);
        try { recorder.StartRecording(); }
        catch { Stop(); throw; }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        _ = devicePosition;
        if (Volatile.Read(ref _enabled) == 0) return;
        var nowUs = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
        var qpcNowUs = (long)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));
        var captureUs = qpcPosition > 0 ? nowUs - (qpcNowUs - qpcPosition / 10) : nowUs;
        if (captureUs <= 0 || Math.Abs(nowUs - captureUs) > 5_000_000) return;
        lock (_gate)
        {
            if (Volatile.Read(ref _enabled) == 0) return;
            if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity))
            {
                CryptographicOperations.ZeroMemory(_partial);
                _partialBytes = 0;
            }
            var consumed = 0;
            while (!data.IsEmpty && Volatile.Read(ref _enabled) != 0)
            {
                if (_partialBytes == 0)
                    _partialTimestamp = captureUs + consumed * 1_000_000L / (SystemAudioFrameCodec.SampleRate * 4);
                var length = Math.Min(data.Length, _partial.Length - _partialBytes);
                data[..length].CopyTo(_partial.AsSpan(_partialBytes));
                _partialBytes += length;
                consumed += length;
                data = data[length..];
                if (_partialBytes != _partial.Length) continue;
                var frame = new SystemAudioFrame(_partialTimestamp, ++_sequence, _partial.ToArray());
                CryptographicOperations.ZeroMemory(_partial);
                _partialBytes = 0;
                if (FrameArrived is { } handler) handler(this, frame);
                else CryptographicOperations.ZeroMemory(frame.Pcm);
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (Interlocked.Exchange(ref _enabled, 0) != 0 && args.Exception is not null)
        {
            // NAudio can invoke this on its capture thread. Defer owner teardown so that
            // Dispose never joins that same thread; the send gate has already been disabled.
            ThreadPool.QueueUserWorkItem(_ => CaptureFailed?.Invoke(this, EventArgs.Empty));
        }
    }

    public void Stop()
    {
        Volatile.Write(ref _enabled, 0);
        var recorder = Interlocked.Exchange(ref _recorder, null);
        if (recorder is not null)
        {
            recorder.DataAvailable -= OnDataAvailable;
            recorder.RecordingStopped -= OnRecordingStopped;
            recorder.StopRecording();
            recorder.Dispose();
        }
        lock (_gate) { CryptographicOperations.ZeroMemory(_partial); _partialBytes = 0; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Stop();
    }
}

public sealed class WindowsSystemAudioPlayback : ISystemAudioPlaybackSink, IWaveProvider
{
    private readonly Lock _gate = new();
    private readonly SystemAudioPlaybackBuffer _buffer = new();
    private readonly WasapiPlayer _player;
    private long _lastSequence;
    private bool _started;
    private volatile bool _failed;
    private bool _disposed;

    public event EventHandler? PlaybackFailed;

    public WindowsSystemAudioPlayback()
    {
        _player = new WasapiPlayerBuilder().WithSharedMode().WithLatency(20).WithMmcssThreadPriority().Build();
        try { _player.Init(this); }
        catch { _player.Dispose(); throw; }
        _player.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is null) return;
            _failed = true;
            ThreadPool.QueueUserWorkItem(_ => PlaybackFailed?.Invoke(this, EventArgs.Empty));
        };
    }

    public WaveFormat WaveFormat { get; } = new(SystemAudioFrameCodec.SampleRate, SystemAudioFrameCodec.BitsPerSample, SystemAudioFrameCodec.Channels);
    public int Read(Span<byte> buffer) => _buffer.Read(buffer);

    public void Write(SystemAudioFrame frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failed) throw new IOException("System-sound playback device became unavailable.");
            if (_lastSequence > 0 && frame.Sequence != _lastSequence + 1) _buffer.Clear();
            _lastSequence = frame.Sequence;
            _buffer.Add(frame.Pcm);
            if (!_started && _buffer.BufferedBytes >= SystemAudioPlaybackBuffer.PrebufferBytes)
            {
                _player.Play();
                _started = true;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _buffer.Clear();
            if (_started) _player.Stop();
            _started = false;
            _lastSequence = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _buffer.Clear();
            _player.Dispose();
        }
    }
}
