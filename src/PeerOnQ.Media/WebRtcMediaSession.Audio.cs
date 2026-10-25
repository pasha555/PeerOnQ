using System.Diagnostics;
using System.Security.Cryptography;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Media.Pipeline;
using SIPSorcery.Net;

namespace PeerOnQ.Media;

public sealed partial class WebRtcMediaSession
{
    private const string AudioSdpAttribute = "a=x-peeronq-system-audio:pcm16-48000-stereo-v1";
    private const string AudioDescriptionPrefix = "a=x-peeronq-audio-description:";
    private const string AudioIcePrefix = "peeronq-audio:";
    private const string AudioChannelLabel = "peeronq-system-audio-v1";
    private const string AudioChannelProtocol = "peeronq-audio.pcm16.v1";
    // This association contains audio only. Upstream's send implementation is reliable even
    // when DCEP advertises a packet lifetime, so memory and age are bounded here instead.
    internal const ulong MaximumAudioBufferedBytes = 2UL * (SystemAudioFrameCodec.EncodedBytes + SessionTrafficProtector.RecordOverheadBytes);
    private readonly Lock _audioGate = new();
    private readonly List<(string Candidate, string? SdpMid, ushort SdpMLineIndex)> _audioCandidates = [];
    private readonly VideoSecurityFailureBudget _audioSecurityFailures = new(maxConsecutiveFailures: 8);
    private readonly SystemAudioSendWindow _audioSendWindow = new();
    private RTCPeerConnection? _audioPeer;
    private RTCDataChannel? _audioChannel;
    private Task<RTCDataChannel>? _audioChannelCreation;
    private Task? _audioReceiptLoop;
    private volatile bool _audioNegotiated;
    private volatile bool _audioClosed;
    private long _audioBacklogStarted;
    private long _lastAudioFrameSentTimestamp;
    private int _systemAudioCaptureActive;

    internal int SystemAudioReservationKbps => Volatile.Read(ref _systemAudioCaptureActive) != 0
        && IsSystemAudioLaneReady && Interlocked.Read(ref _lastAudioFrameSentTimestamp) is var last && last > 0
        && Stopwatch.GetElapsedTime(last) <= TimeSpan.FromMilliseconds(500) ? 1800 : 0;

    public event EventHandler<ReadOnlyMemory<byte>>? SystemAudioFrameReceived;
    public event EventHandler? SystemAudioUnavailable;

    public bool IsSystemAudioLaneReady => !_audioClosed && _audioNegotiated
        && _permissions.HasFlag(SessionPermission.SystemAudio)
        && State == MediaConnectionState.Connected
        && _audioChannel?.readyState == RTCDataChannelState.open;

    private void InitializeSystemAudio(RTCConfiguration configuration)
    {
        if (!_permissions.HasFlag(SessionPermission.SystemAudio)) return;
        _audioPeer = new RTCPeerConnection(configuration);
        _audioPeer.ondatachannel += ConfigureSystemAudioChannel;
        _audioPeer.onicecandidate += candidate =>
        {
            if (candidate is null || !AdvertiseLocalCandidate(candidate.candidate)) return;
            var routed = (candidate.candidate, string.Concat(AudioIcePrefix, candidate.sdpMid), candidate.sdpMLineIndex);
            lock (_audioGate)
            {
                if (_audioClosed) return;
                if (!_audioNegotiated)
                {
                    if (_audioCandidates.Count < MaximumPendingAuxiliaryIceCandidates) _audioCandidates.Add(routed);
                    return;
                }
            }
            LocalIceCandidate?.Invoke(this, routed);
        };
        _audioPeer.onconnectionstatechange += state =>
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
                CloseSystemAudio();
        };
        if (Role == SessionRole.Sharer)
        {
            _audioChannelCreation = _audioPeer.createDataChannel(AudioChannelLabel,
                new RTCDataChannelInit { ordered = true, protocol = AudioChannelProtocol });
            var token = _lifetime.Token;
            _audioReceiptLoop = Task.Run(() => WatchSystemAudioReceiptsAsync(token));
        }
    }

    private void ConfigureSystemAudioChannel(RTCDataChannel channel)
    {
        lock (_audioGate)
        {
            if (_audioClosed || !_permissions.HasFlag(SessionPermission.SystemAudio)
                || channel.label != AudioChannelLabel || channel.protocol != AudioChannelProtocol
                || !channel.ordered || channel.maxPacketLifeTime is not null || channel.maxRetransmits is not null
                || (_audioChannel is not null && !ReferenceEquals(_audioChannel, channel)))
            {
                channel.close();
                return;
            }
            _audioChannel = channel;
        }
        channel.onmessage += (_, _, payload) => ReceiveSystemAudioFrame(payload);
        channel.onclose += () => CloseSystemAudio();
    }

    private void ReceiveSystemAudioFrame(byte[] payload)
    {
        var plaintextLength = Role == SessionRole.Viewer ? SystemAudioFrameCodec.EncodedBytes : SystemAudioReceiptCodec.EncodedBytes;
        if (!IsSystemAudioLaneReady || payload.Length != plaintextLength + SessionTrafficProtector.RecordOverheadBytes) return;
        var protector = Volatile.Read(ref _trafficProtector);
        if (protector?.IsEstablished != true) return;
        if (!protector.TryUnprotectAudioFrame(payload, out var plaintext, out var error))
        {
            if (error != "stale_audio_record")
            {
                _audioSecurityFailures.RecordFailure();
                if (_audioSecurityFailures.IsExhausted)
                    SecurityError?.Invoke(this, "audio_authentication_failed");
            }
            return;
        }
        _audioSecurityFailures.RecordSuccess();
        try
        {
            if (Role == SessionRole.Sharer)
            {
                if (!SystemAudioReceiptCodec.TryDecode(plaintext, out var acknowledged)) { CloseSystemAudio(); return; }
                var receipt = _audioSendWindow.Acknowledge(acknowledged);
                if (receipt is AudioReceiptResult.Invalid or AudioReceiptResult.Expired) CloseSystemAudio();
                return;
            }
            if (!SystemAudioFrameCodec.TryReadHeader(plaintext, out _, out var sequence)) { CloseSystemAudio(); return; }
            SendSystemAudioReceipt(sequence);
            SystemAudioFrameReceived?.Invoke(this, plaintext);
        }
        catch (Exception) { CloseSystemAudio(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void SendSystemAudioReceipt(long sequence)
    {
        lock (_audioGate)
        {
            if (!IsSystemAudioLaneReady || Role != SessionRole.Viewer
                || Volatile.Read(ref _trafficProtector) is not { IsEstablished: true } protector) return;
            if (_audioChannel!.bufferedAmount + SystemAudioReceiptCodec.EncodedBytes
                + SessionTrafficProtector.RecordOverheadBytes > MaximumAudioBufferedBytes) return;
            var receipt = SystemAudioReceiptCodec.Encode(sequence);
            try { _audioChannel.send(protector.ProtectAudioFrame(receipt)); }
            finally { CryptographicOperations.ZeroMemory(receipt); }
        }
    }

    public bool TrySendSystemAudioFrame(ReadOnlySpan<byte> frame)
    {
        if (Role != SessionRole.Sharer || !SystemAudioFrameCodec.TryReadHeader(frame, out _, out var sequence)) return false;
        var close = false;
        lock (_audioGate)
        {
            if (!IsSystemAudioLaneReady || Volatile.Read(ref _trafficProtector) is not { IsEstablished: true } protector)
                return false;
            if (_audioChannel!.bufferedAmount + (ulong)frame.Length + SessionTrafficProtector.RecordOverheadBytes > MaximumAudioBufferedBytes)
            {
                if (_audioBacklogStarted == 0) _audioBacklogStarted = Stopwatch.GetTimestamp();
                close = Stopwatch.GetElapsedTime(_audioBacklogStarted) >= TimeSpan.FromMilliseconds(160);
            }
            else
            {
                _audioBacklogStarted = 0;
                var admitted = _audioSendWindow.TryAdmit(sequence);
                if (admitted == AudioAdmission.Expired) close = true;
                else if (admitted != AudioAdmission.Accepted) return false;
                else
                {
                    // Admission happens before PNQE sequence allocation. Audio never waits for
                    // another lane, and records discarded under pressure consume no secure sequence.
                    var protectedFrame = protector.ProtectAudioFrame(frame);
                    _audioChannel.send(protectedFrame);
                    Interlocked.Exchange(ref _lastAudioFrameSentTimestamp, Stopwatch.GetTimestamp());
                    return true;
                }
            }
        }
        if (close) CloseSystemAudio();
        return false;
    }

    private async Task WatchSystemAudioReceiptsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_audioClosed)
            {
                await Task.Delay(40, cancellationToken);
                if (_audioSendWindow.IsExpired) { CloseSystemAudio(); return; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<string> AddSystemAudioOfferAsync(string sdp, bool restart, CancellationToken cancellationToken)
    {
        if (_audioPeer is null || _audioClosed) return sdp;
        if (_audioChannelCreation is not null)
        {
            ConfigureSystemAudioChannel(await _audioChannelCreation.WaitAsync(cancellationToken));
            _audioChannelCreation = null;
        }
        if (restart) _audioPeer.restartIce();
        return AdvertiseAuxiliaryDataLane(sdp, await CreateAuxiliaryOfferAsync(_audioPeer),
            AudioSdpAttribute, AudioDescriptionPrefix, "audioDescription");
    }

    private async Task<string?> CreateSystemAudioAnswerAsync(string offer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_audioPeer is null || _audioClosed || !HasSdpAttribute(offer, AudioSdpAttribute)
            || !TryGetAuxiliaryDescription(offer, AudioDescriptionPrefix, out var description))
            return null;
        var answer = await CreateAuxiliaryAnswerAsync(_audioPeer, description, "audio");
        CompleteSystemAudioNegotiation();
        return answer;
    }

    private string AddSystemAudioAnswer(string sdp, string? description) => description is null ? sdp
        : AdvertiseAuxiliaryDataLane(sdp, description, AudioSdpAttribute, AudioDescriptionPrefix, "audioDescription");

    private void ApplySystemAudioAnswer(string answer)
    {
        if (_audioPeer is null || _audioClosed) return;
        if (!HasSdpAttribute(answer, AudioSdpAttribute)
            || !TryGetAuxiliaryDescription(answer, AudioDescriptionPrefix, out var description))
        {
            CloseSystemAudio();
            return;
        }
        ApplyAuxiliaryAnswer(_audioPeer, description, "audio");
        CompleteSystemAudioNegotiation();
    }

    private void CompleteSystemAudioNegotiation()
    {
        (string Candidate, string? SdpMid, ushort SdpMLineIndex)[] pending;
        lock (_audioGate)
        {
            if (_audioClosed) return;
            _audioNegotiated = true;
            pending = [.. _audioCandidates];
            _audioCandidates.Clear();
        }
        foreach (var candidate in pending) LocalIceCandidate?.Invoke(this, candidate);
    }

    private static string RemoveSystemAudioDescription(string sdp) =>
        RemoveAuxiliaryDescription(sdp, AudioDescriptionPrefix);

    private bool TryAddSystemAudioIceCandidate(string candidate, string? sdpMid, ushort index)
    {
        if (sdpMid?.StartsWith(AudioIcePrefix, StringComparison.Ordinal) != true) return false;
        if (_audioPeer is null || _audioClosed) return true;
        var routedMid = sdpMid[AudioIcePrefix.Length..];
        _audioPeer.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = candidate, sdpMid = string.IsNullOrEmpty(routedMid) ? null : routedMid, sdpMLineIndex = index,
        });
        return true;
    }

    /// <param name="closePeer">Session close passes false: its bounded peer close owns the audio peer.</param>
    private void CloseSystemAudio(bool closePeer = true)
    {
        lock (_audioGate)
        {
            if (_audioClosed) return;
            _audioClosed = true;
            SetSystemAudioCaptureActive(false);
            _audioNegotiated = false;
            _audioCandidates.Clear();
            _audioSendWindow.Close();
        }
        SystemAudioUnavailable?.Invoke(this, EventArgs.Empty);
        if (closePeer) _audioPeer?.close();
    }

    public void CloseSystemAudioTransmission() => CloseSystemAudio();

    public void SetSystemAudioCaptureActive(bool active)
    {
        Volatile.Write(ref _systemAudioCaptureActive,
            active && Role == SessionRole.Sharer && _permissions.HasFlag(SessionPermission.SystemAudio) ? 1 : 0);
        if (!active) Interlocked.Exchange(ref _lastAudioFrameSentTimestamp, 0);
    }

    private async ValueTask DisposeSystemAudioAsync()
    {
        if (_audioReceiptLoop is not null)
        {
            try { await _audioReceiptLoop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { /* Ends with the session. */ }
        }
        // A peer whose bounded close did not finish would block its disposal the same way.
        if (_peerClose is { IsCompleted: true }) _audioPeer?.Dispose();
    }
}
