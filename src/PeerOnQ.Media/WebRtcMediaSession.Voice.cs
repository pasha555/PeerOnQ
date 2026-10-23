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
/// Consent is local on both ends: a device opens its microphone and plays the peer's voice only
/// after its own user enables voice; a peer can never turn on another device's microphone.
/// Every packet is an authenticated record in the per-direction, lossy Voice context of the
/// hybrid post-quantum session, exactly like video and system audio. Congestion drops speech
/// instead of queueing it, and short losses are concealed with Opus FEC/PLC.
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

    public bool IsVoiceLaneReady =>
        _permissions.HasFlag(SessionPermission.ViewScreen)
        && State == MediaConnectionState.Connected
        && _voiceCaptureFactory is not null
        && _voicePlaybackFactory is not null
        && Volatile.Read(ref _voiceDataChannel)?.readyState == RTCDataChannelState.open;

    public bool IsVoiceEnabled => _voiceEnabled;

    public bool SetVoiceEnabled(bool enabled)
    {
        IVoiceCaptureSource? releasedCapture = null;
        IVoicePlaybackSink? releasedPlayback = null;
        object? releasedEncoder = null, releasedDecoder = null;
        var result = true;
        lock (_voiceGate)
        {
            if (!enabled)
            {
                if (!_voiceEnabled && _voiceCapture is null && _voicePlayback is null) return true;
                _voiceEnabled = false;
                (releasedCapture, releasedPlayback) = (_voiceCapture, _voicePlayback);
                (releasedEncoder, releasedDecoder) = (_voiceEncoder, _voiceDecoder);
                _voiceCapture = null;
                _voicePlayback = null;
                _voiceEncoder = null;
                _voiceDecoder = null;
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
                IVoicePlaybackSink? playback = null;
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
                    _voiceDecoder = OpusCodecFactory.CreateDecoder(VoiceAudioFormat.SampleRate, VoiceAudioFormat.Channels);
                    _voiceSendSequence = 0;
                    _voiceLastReceivedSequence = 0;

                    playback = _voicePlaybackFactory!() ?? throw new InvalidOperationException("No speaker is available.");
                    capture = _voiceCaptureFactory!() ?? throw new InvalidOperationException("No microphone is available.");
                    playback.PlaybackFailed += OnVoiceDeviceFailed;
                    capture.CaptureFailed += OnVoiceDeviceFailed;
                    capture.FrameArrived += OnVoiceCaptured;
                    _voicePlayback = playback;
                    _voiceCapture = capture;
                    _voiceEnabled = true;
                    capture.Start();
                    capture = null;
                    playback = null;
                }
                catch (Exception ex)
                {
                    LogVoiceStartFailed(_log, SessionId, ex);
                    _voiceEnabled = false;
                    releasedCapture = _voiceCapture ?? capture;
                    releasedPlayback = _voicePlayback ?? playback;
                    (releasedEncoder, releasedDecoder) = (_voiceEncoder, _voiceDecoder);
                    _voiceCapture = null;
                    _voicePlayback = null;
                    _voiceEncoder = null;
                    _voiceDecoder = null;
                    result = false;
                }
            }
        }

        if (releasedCapture is not null)
        {
            releasedCapture.FrameArrived -= OnVoiceCaptured;
            releasedCapture.CaptureFailed -= OnVoiceDeviceFailed;
            try { releasedCapture.Stop(); }
            catch (Exception) { /* A failed device is released below either way. */ }
            releasedCapture.Dispose();
        }
        if (releasedPlayback is not null)
        {
            releasedPlayback.PlaybackFailed -= OnVoiceDeviceFailed;
            releasedPlayback.Dispose();
        }
        (releasedEncoder as IDisposable)?.Dispose();
        (releasedDecoder as IDisposable)?.Dispose();
        LogVoiceState(_log, SessionId, _voiceEnabled ? "enabled" : "disabled");
        return enabled ? result && _voiceEnabled : !_voiceEnabled;
    }

    private void OnVoiceDeviceFailed(object? sender, EventArgs e) =>
        ThreadPool.QueueUserWorkItem(_ => SetVoiceEnabled(false));

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
        // Nothing is played on a device whose user has not enabled voice.
        if (!_voiceEnabled) return;
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
                if (!_voiceEnabled || _voiceDecoder is not { } decoder || _voicePlayback is not { } playback) return;
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
