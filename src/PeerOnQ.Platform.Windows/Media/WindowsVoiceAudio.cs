using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Platform.Windows.Media;

/// <summary>
/// The default communications microphone for two-way voice. The stream opens in the Windows
/// communications signal-processing mode, which applies the endpoint's acoustic echo
/// cancellation, noise suppression and automatic gain control where the device provides them.
/// It is created only after the local user enables voice.
/// </summary>
public sealed class WindowsVoiceCapture : IVoiceCaptureSource
{
    private readonly Lock _gate = new();
    private readonly short[] _partial = new short[VoiceAudioFormat.SamplesPerFrame];
    private MMDevice? _device;
    private WasapiRecorder? _recorder;
    private int _partialSamples;
    private int _enabled;
    private int _disposed;

    public event EventHandler<VoicePcmFrame>? FrameArrived;
    public event EventHandler? CaptureFailed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_recorder is not null) return;
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        WasapiRecorder recorder;
        try
        {
            recorder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithCommunicationsMode()
                .WithFormat(new WaveFormat(VoiceAudioFormat.SampleRate, VoiceAudioFormat.BitsPerSample, VoiceAudioFormat.Channels))
                .WithBufferLength(VoiceAudioFormat.FrameMilliseconds)
                .WithMmcssThreadPriority()
                .Build();
        }
        catch
        {
            device.Dispose();
            throw;
        }
        _device = device;
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
        _ = qpcPosition;
        if (Volatile.Read(ref _enabled) == 0) return;
        var silent = flags.HasFlag(AudioClientBufferFlags.Silent);
        var samples = MemoryMarshal.Cast<byte, short>(data[..(data.Length & ~1)]);
        lock (_gate)
        {
            if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity))
            {
                Array.Clear(_partial);
                _partialSamples = 0;
            }
            while (!samples.IsEmpty && Volatile.Read(ref _enabled) != 0)
            {
                var length = Math.Min(samples.Length, _partial.Length - _partialSamples);
                if (silent) _partial.AsSpan(_partialSamples, length).Clear();
                else samples[..length].CopyTo(_partial.AsSpan(_partialSamples));
                _partialSamples += length;
                samples = samples[length..];
                if (_partialSamples != _partial.Length) continue;
                var capturedAt = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
                var frame = new VoicePcmFrame(capturedAt, _partial.ToArray());
                Array.Clear(_partial);
                _partialSamples = 0;
                if (FrameArrived is { } handler) handler(this, frame);
                else CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(frame.Samples.AsSpan()));
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (Interlocked.Exchange(ref _enabled, 0) != 0 && args.Exception is not null)
        {
            // NAudio can invoke this on its capture thread; defer teardown so Dispose never joins it.
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
        Interlocked.Exchange(ref _device, null)?.Dispose();
        lock (_gate)
        {
            Array.Clear(_partial);
            _partialSamples = 0;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Stop();
    }
}

/// <summary>
/// Plays the peer's voice on the default output with a small jitter buffer: playback starts
/// after 40 ms, never holds more than 200 ms (oldest speech is dropped first) and, after running
/// dry, waits for the prebuffer again instead of stuttering sample by sample.
/// </summary>
public sealed class WindowsVoicePlayback : IVoicePlaybackSink, IWaveProvider
{
    internal const int PrebufferSamples = VoiceAudioFormat.SamplesPerFrame * 2;
    internal const int CapacitySamples = VoiceAudioFormat.SamplesPerFrame * 10;

    private readonly Lock _gate = new();
    private readonly short[] _ring = new short[CapacitySamples];
    private readonly WasapiPlayer _player;
    private int _readIndex;
    private int _count;
    private bool _buffering = true;
    private bool _started;
    private volatile bool _failed;
    private bool _disposed;

    public event EventHandler? PlaybackFailed;

    public WindowsVoicePlayback()
    {
        _player = new WasapiPlayerBuilder().WithSharedMode().WithLatency(VoiceAudioFormat.FrameMilliseconds)
            .WithMmcssThreadPriority().Build();
        try { _player.Init(this); }
        catch { _player.Dispose(); throw; }
        _player.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is null) return;
            _failed = true;
            ThreadPool.QueueUserWorkItem(_ => PlaybackFailed?.Invoke(this, EventArgs.Empty));
        };
    }

    public WaveFormat WaveFormat { get; } =
        new(VoiceAudioFormat.SampleRate, VoiceAudioFormat.BitsPerSample, VoiceAudioFormat.Channels);

    public void Write(ReadOnlySpan<short> samples)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failed) throw new IOException("The voice playback device became unavailable.");
            if (samples.Length > CapacitySamples) samples = samples[^CapacitySamples..];
            var overflow = _count + samples.Length - CapacitySamples;
            if (overflow > 0)
            {
                // Keep conversation latency bounded: drop the oldest queued speech.
                _readIndex = (_readIndex + overflow) % CapacitySamples;
                _count -= overflow;
            }
            var writeIndex = (_readIndex + _count) % CapacitySamples;
            var first = Math.Min(samples.Length, CapacitySamples - writeIndex);
            samples[..first].CopyTo(_ring.AsSpan(writeIndex));
            samples[first..].CopyTo(_ring);
            _count += samples.Length;
            if (_buffering && _count >= PrebufferSamples) _buffering = false;
            if (!_started && !_buffering)
            {
                _player.Play();
                _started = true;
            }
        }
    }

    public int Read(Span<byte> buffer)
    {
        var output = MemoryMarshal.Cast<byte, short>(buffer[..(buffer.Length & ~1)]);
        lock (_gate)
        {
            var available = _buffering ? 0 : Math.Min(_count, output.Length);
            var first = Math.Min(available, CapacitySamples - _readIndex);
            _ring.AsSpan(_readIndex, first).CopyTo(output);
            _ring.AsSpan(0, available - first).CopyTo(output[first..]);
            _readIndex = (_readIndex + available) % CapacitySamples;
            _count -= available;
            output[available..].Clear();
            if (_count == 0) _buffering = true;
        }
        buffer[(buffer.Length & ~1)..].Clear();
        return buffer.Length;
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _readIndex = 0;
            _count = 0;
            _buffering = true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Array.Clear(_ring);
            _count = 0;
        }
        _player.Dispose();
    }
}
