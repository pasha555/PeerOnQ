using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Concentus;
using Concentus.Enums;
using Microsoft.Extensions.Logging;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Pipeline;
using SIPSorcery.Net;

namespace PeerOnQ.Media;

/// <summary>
/// Two-way Opus voice on a dedicated unordered data channel of the primary association.
///
/// A device's microphone opens only after its own user enables voice; a peer can never turn on
/// another device's microphone. Since 0.9.88 the peer's voice plays automatically, like system
/// sound, so either side can switch its microphone on and be heard; the speaker opens with the
/// first voice packet. Voice exists only in sessions that carry system sound (the organization
/// audio policy). Every packet is an authenticated record in the per-direction, lossy Voice
/// context of the hybrid post-quantum session, exactly like video and system audio. Congestion
/// drops speech instead of queueing it, and short losses are concealed with Opus FEC/PLC.
/// </summary>
public sealed partial class WebRtcMediaSession
{
    private const string VoiceDataChannelLabel = "peeronq.voice.v1";

    /// <summary>Wideband speech with headroom for in-band forward error correction.</summary>
    internal const int VoiceBitrateBitsPerSecond = 32_000;

    /// <summary>A send backlog above this drops new speech instead of adding delay.</summary>
    internal const ulong MaximumVoiceBufferedBytes = 8 * 1024;

    /// <summary>Gaps up to this many frames are concealed; longer gaps restart playback.</summary>
    internal const int MaximumConcealedVoiceFrames = 3;

    private readonly Lock _voiceGate = new();
    private readonly VideoSecurityFailureBudget _voiceSecurityFailures = new(maxConsecutiveFailures: 16);
    private Func<IVoiceCaptureSource?>? _voiceCaptureFactory;
    private Func<IVoicePlaybackSink?>? _voicePlaybackFactory;
    private RTCDataChannel? _voiceDataChannel;
    private Task<RTCDataChannel>? _voiceDataChannelCreation;
    private IVoiceCaptureSource? _voiceCapture;
    private IVoicePlaybackSink? _voicePlayback;
    private IOpusEncoder? _voiceEncoder;
    private IOpusDecoder? _voiceDecoder;
    private long _voiceSendSequence;
    private long _voiceLastReceivedSequence;
    private volatile bool _voiceEnabled;

    [LoggerMessage(1300, LogLevel.Information, "Session {SessionId} voice {State}")]
    private static partial void LogVoiceState(ILogger logger, SessionId sessionId, string state);

    [LoggerMessage(1301, LogLevel.Warning, "Session {SessionId} could not start voice")]
    private static partial void LogVoiceStartFailed(ILogger logger, SessionId sessionId, Exception exception);

    [LoggerMessage(1302, LogLevel.Warning, "Rejected an unauthenticated voice record ({Reason}, {Failures}/{Maximum})")]
    private static partial void LogRejectedVoiceRecord(ILogger logger, string reason, int failures, int maximum);

    [LoggerMessage(1303, LogLevel.Debug, "Voice packet dropped ({Reason})")]
    private static partial void LogVoicePacketDropped(ILogger logger, string reason);

    /// <summary>Platform microphone and speaker factories; voice stays unavailable without them.</summary>
    internal void ConfigureVoiceDevices(Func<IVoiceCaptureSource?>? capture, Func<IVoicePlaybackSink?>? playback)
    {
        _voiceCaptureFactory = capture;
        _voicePlaybackFactory = playback;
    }

    /// <summary>Voice rides on sessions that carry system sound, so the audio policy covers it too.</summary>
    private bool IsVoicePermitted =>
        _permissions.HasFlag(SessionPermission.ViewScreen) && _permissions.HasFlag(SessionPermission.SystemAudio);

    public bool IsVoiceLaneReady =>
        IsVoicePermitted
        && State == MediaConnectionState.Connected
        && _voiceCaptureFactory is not null
        && _voicePlaybackFactory is not null
        && Volatile.Read(ref _voiceDataChannel)?.readyState == RTCDataChannelState.open;

    /// <summary>This device's microphone is on.</summary>
    public bool IsVoiceEnabled => _voiceEnabled;

    private static readonly TimeSpan PeerVoiceActiveWindow = TimeSpan.FromMilliseconds(1500);
    private long _lastPeerVoiceTimestamp;

    /// <summary>The peer's voice was heard within the last 1.5 s (for the in-session indicator).</summary>
    public bool IsPeerVoiceActive
    {
        get
        {
            var last = Volatile.Read(ref _lastPeerVoiceTimestamp);
            return last != 0 && Stopwatch.GetElapsedTime(last) < PeerVoiceActiveWindow;
        }
    }

    /// <summary>Turns this device's microphone on or off; the peer's voice keeps playing either way.</summary>
    public bool SetVoiceEnabled(bool enabled)
    {
        IVoiceCaptureSource? releasedCapture = null;
        object? releasedEncoder = null;
        var result = true;
        lock (_voiceGate)
        {
            if (!enabled)
            {
                if (!_voiceEnabled && _voiceCapture is null) return true;
                _voiceEnabled = false;
                releasedCapture = _voiceCapture;
                releasedEncoder = _voiceEncoder;
                _voiceCapture = null;
                _voiceEncoder = null;
            }
            else if (_voiceEnabled)
            {
                return true;
            }
            else if (!IsVoiceLaneReady || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }
            else
            {
                IVoiceCaptureSource? capture = null;
                try
                {
                    // Only the reviewed managed Opus implementation may run; never probe the
                    // library search path for a native codec.
                    OpusCodecFactory.AttemptToUseNativeLibrary = false;
                    var encoder = OpusCodecFactory.CreateEncoder(
                        VoiceAudioFormat.SampleRate, VoiceAudioFormat.Channels, OpusApplication.OPUS_APPLICATION_VOIP);
                    encoder.Bitrate = VoiceBitrateBitsPerSecond;
                    encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
                    encoder.UseInbandFEC = true;
                    encoder.PacketLossPercent = 10;
                    encoder.UseVBR = true;
                    encoder.Complexity = 8;
                    _voiceEncoder = encoder;
                    _voiceSendSequence = 0;
                    // A talking user hears the reply on the same session: open the speaker now.
                    EnsureVoicePlaybackLocked();

                    capture = _voiceCaptureFactory!() ?? throw new InvalidOperationException("No microphone is available.");
                    capture.CaptureFailed += OnVoiceCaptureFailed;
                    capture.FrameArrived += OnVoiceCaptured;
                    _voiceCapture = capture;
                    _voiceEnabled = true;
                    capture.Start();
                    capture = null;
                }
                catch (Exception ex)
                {
                    LogVoiceStartFailed(_log, SessionId, ex);
                    _voiceEnabled = false;
                    releasedCapture = _voiceCapture ?? capture;
                    releasedEncoder = _voiceEncoder;
                    _voiceCapture = null;
                    _voiceEncoder = null;
                    result = false;
                }
            }
        }

        ReleaseVoiceCapture(releasedCapture, releasedEncoder);
        LogVoiceState(_log, SessionId, _voiceEnabled ? "microphone on" : "microphone off");
        return enabled ? result && _voiceEnabled : !_voiceEnabled;
    }

    /// <summary>Opens the speaker and decoder for the peer's voice once. Callers hold the voice lock.</summary>
    private bool EnsureVoicePlaybackLocked()
    {
        if (_voicePlayback is not null && _voiceDecoder is not null) return true;
        if (!IsVoicePermitted || _voicePlaybackFactory is null || Volatile.Read(ref _disposed) != 0
            || _lifetime.IsCancellationRequested) return false;
        IVoicePlaybackSink? playback = null;
        try
        {
            OpusCodecFactory.AttemptToUseNativeLibrary = false;
            var decoder = OpusCodecFactory.CreateDecoder(VoiceAudioFormat.SampleRate, VoiceAudioFormat.Channels);
            playback = _voicePlaybackFactory() ?? throw new InvalidOperationException("No speaker is available.");
            playback.PlaybackFailed += OnVoicePlaybackFailed;
            _voiceDecoder = decoder;
            _voicePlayback = playback;
            _voiceLastReceivedSequence = 0;
            LogVoiceState(_log, SessionId, "speaker on for the peer's voice");
            return true;
        }
        catch (Exception ex)
        {
            LogVoiceStartFailed(_log, SessionId, ex);
            playback?.Dispose();
            return false;
        }
    }

    /// <summary>Session close: microphone, speaker and codecs are all released.</summary>
    private void ReleaseVoice()
    {
        SetVoiceEnabled(false);
        IVoicePlaybackSink? playback;
        object? decoder;
        lock (_voiceGate)
        {
            playback = _voicePlayback;
            decoder = _voiceDecoder;
            _voicePlayback = null;
            _voiceDecoder = null;
        }
        if (playback is not null)
        {
            playback.PlaybackFailed -= OnVoicePlaybackFailed;
            playback.Dispose();
        }
        (decoder as IDisposable)?.Dispose();
    }

    private void ReleaseVoiceCapture(IVoiceCaptureSource? capture, object? encoder)
    {
        if (capture is not null)
        {
            capture.FrameArrived -= OnVoiceCaptured;
            capture.CaptureFailed -= OnVoiceCaptureFailed;
            try { capture.Stop(); }
            catch (Exception) { /* A failed device is released below either way. */ }
            capture.Dispose();
        }
        (encoder as IDisposable)?.Dispose();
    }

    private void OnVoiceCaptureFailed(object? sender, EventArgs e) =>
        ThreadPool.QueueUserWorkItem(_ => SetVoiceEnabled(false));

    private void OnVoicePlaybackFailed(object? sender, EventArgs e) =>
        ThreadPool.QueueUserWorkItem(_ => ReleaseVoice());

    private void OnVoiceCaptured(object? sender, VoicePcmFrame frame)
    {
        var samples = frame.Samples;
        try
        {
            if (!_voiceEnabled || samples.Length != VoiceAudioFormat.SamplesPerFrame) return;
            var channel = Volatile.Read(ref _voiceDataChannel);
            var protector = Volatile.Read(ref _trafficProtector);
            if (channel?.readyState != RTCDataChannelState.open || protector is not { IsEstablished: true }
                || State != MediaConnectionState.Connected)
                return;
            if (channel.bufferedAmount > MaximumVoiceBufferedBytes)
            {
                LogVoicePacketDropped(_log, "send_backlog");
                return;
            }

            var packet = new byte[VoiceAudioFormat.MaximumEncodedBytes];
            int length;
            long sequence;
            lock (_voiceGate)
            {
                if (!_voiceEnabled || _voiceEncoder is null) return;
                length = _voiceEncoder.Encode(samples, VoiceAudioFormat.SamplesPerFrame, packet, packet.Length);
                sequence = ++_voiceSendSequence;
            }
            if (length <= 0) return;
            var plaintext = VoiceFrameCodec.Encode(sequence, frame.CapturedAtUnixMicroseconds, packet.AsSpan(0, length));
            CryptographicOperations.ZeroMemory(packet);
            byte[] record;
            try { record = protector.ProtectVoiceFrame(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            channel.send(record);
        }
        catch (Exception ex)
        {
            LogVoicePacketDropped(_log, ex.GetType().Name);
        }
        finally
        {
            // Microphone samples are plaintext speech; never leave them in a reusable buffer.
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(samples.AsSpan()));
        }
    }

    private void ReceiveVoiceRecord(byte[] payload)
    {
        // Voice is played only in a session that carries sound under the audio policy.
        if (!IsVoicePermitted) return;
        var protector = Volatile.Read(ref _trafficProtector);
        if (protector is not { IsEstablished: true }) return;
        if (!protector.TryUnprotectVoiceFrame(payload, out var plaintext, out var error))
        {
            if (string.Equals(error, "stale_voice_record", StringComparison.Ordinal)) return;
            var reason = error ?? "voice_authentication_failed";
            var failures = _voiceSecurityFailures.RecordFailure();
            LogRejectedVoiceRecord(_log, reason, failures, _voiceSecurityFailures.MaximumConsecutiveFailures);
            if (_voiceSecurityFailures.IsExhausted) SecurityError?.Invoke(this, reason);
            return;
        }

        _voiceSecurityFailures.RecordSuccess();
        try
        {
            if (!VoiceFrameCodec.TryDecode(plaintext, out var sequence, out _, out var opus))
            {
                // An authenticated peer sent a malformed record: a protocol violation.
                SecurityError?.Invoke(this, "invalid_voice_record");
                return;
            }

            Span<short> pcm = stackalloc short[VoiceAudioFormat.SamplesPerFrame];
            lock (_voiceGate)
            {
                if (!EnsureVoicePlaybackLocked() || _voiceDecoder is not { } decoder || _voicePlayback is not { } playback) return;
                Volatile.Write(ref _lastPeerVoiceTimestamp, Stopwatch.GetTimestamp());
                if (sequence <= _voiceLastReceivedSequence) return;
                var missing = _voiceLastReceivedSequence == 0 ? 0 : sequence - _voiceLastReceivedSequence - 1;
                if (missing > MaximumConcealedVoiceFrames)
                {
                    // A long gap is not worth reconstructing; restart from current speech.
                    playback.Clear();
                }
                else
                {
                    for (var lost = 0; lost < missing; lost++)
                    {
                        // The last lost frame is rebuilt from this packet's in-band FEC; earlier
                        // ones use packet-loss concealment.
                        var concealed = lost == missing - 1
                            ? decoder.Decode(opus, pcm, VoiceAudioFormat.SamplesPerFrame, decode_fec: true)
                            : decoder.Decode(ReadOnlySpan<byte>.Empty, pcm, VoiceAudioFormat.SamplesPerFrame, decode_fec: false);
                        if (concealed > 0) playback.Write(pcm[..concealed]);
                    }
                }

                var decoded = decoder.Decode(opus, pcm, VoiceAudioFormat.SamplesPerFrame, decode_fec: false);
                if (decoded > 0) playback.Write(pcm[..decoded]);
                _voiceLastReceivedSequence = sequence;
            }
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(pcm));
        }
        catch (Exception ex)
        {
            // A packet the decoder rejects (for example longer than 20 ms) is dropped.
            LogVoicePacketDropped(_log, ex.GetType().Name);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
