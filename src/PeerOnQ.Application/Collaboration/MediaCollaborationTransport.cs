using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Crypto.Parameters;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Security;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Collaboration;

public interface ICollaborationTransport : IAsyncDisposable
{
    SessionPermission Permissions { get; }
    bool IsReady { get; }
    SecureSessionInfo? Security => null;
    event EventHandler? Ready;
    event EventHandler<CollaborationMessage>? MessageReceived;
    event EventHandler<string>? ProtocolError;
    Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken = default);
    TransferPriorityMode TransferPriorityMode => TransferPriorityMode.Balanced;
    bool IsNativeBulkTransportReady => false;
    int NativeBulkBudgetKbps => 0;
    double NativeBulkGoodputKbps => 0;
    long NativeBulkFeedbackSamples => 0;
    bool IsInputAcknowledgementNegotiated => false;
    PeerClockEstimate? PeerClockEstimate => null;
    void ReportInputLatency(InputLatencyMeasurement measurement) { }
    void SetTransferPriorityMode(TransferPriorityMode mode) { }
    void ForceRekey() { }
    void CompleteTransfer(Guid transferId) { }
    ValueTask ReportFileDeliveryAsync(
        Guid transferId,
        int deliveredBytes,
        bool flush = false,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

/// <summary>
/// Authenticated application record layer over the WebRTC data channel. DTLS remains defense in
/// depth; no collaboration payload or video frame is accepted before the hybrid handshake reaches
/// Secure. When both peers negotiated it, encrypted file records use the authenticated signaling
/// relay while WebRTC remains dedicated to the handshake, video, and interactive control traffic.
/// </summary>
public sealed class MediaCollaborationTransport : ICollaborationTransport
{
    private const long InitialPermissionGeneration = 1;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan NativeBulkOfferWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaximumReceiptInterval = TimeSpan.FromMilliseconds(100);
    private const int ReceiptByteInterval = 1024 * 1024;

    private readonly IMediaSession _media;
    private readonly IHybridDeviceIdentityProvider _identity;
    private readonly string _expectedPeerFingerprint;
    private readonly IFileRelaySignaling? _fileRelay;
    private readonly INativeBulkTransportFactory? _nativeBulkTransportFactory;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _inputSendGate = new(1, 1);
    private readonly SemaphoreSlim _clipboardSendGate = new(1, 1);
    private readonly SemaphoreSlim _fileSendGate = new(1, 1);
    private readonly SemaphoreSlim _telemetrySendGate = new(1, 1);
    private readonly SemaphoreSlim _handshakeReceiveGate = new(1, 1);
    private readonly SemaphoreSlim _inputReceiveGate = new(1, 1);
    private readonly SemaphoreSlim _clipboardReceiveGate = new(1, 1);
    private readonly SemaphoreSlim _fileReceiveGate = new(1, 1);
    private readonly SemaphoreSlim _telemetryReceiveGate = new(1, 1);
    private readonly SemaphoreSlim _nativeBulkNegotiationGate = new(1, 1);
    private readonly AdaptiveFileTransferPacer _filePacer = new();
    private readonly NativeBulkCapacityEstimator _nativeBulkCapacity;
    private readonly ConcurrentDictionary<Guid, byte> _nativeBulkTransfers = new();
    private readonly ConcurrentDictionary<Guid, byte> _adaptiveBulkTransfers = new();
    private readonly ConcurrentDictionary<Guid, DeliveryReceiptState> _deliveryReceipts = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _stateGate = new();
    private readonly Lock _clockGate = new();
    private readonly TimeProvider _time;
    private HandshakeState _state = HandshakeState.WaitingForTransport;
    private X25519PrivateKeyParameters? _x25519Private;
    private IMlKem768PrivateKey? _mlKemPrivate;
    private byte[]? _clientHelloFrame;
    private byte[]? _serverHelloFrame;
    private byte[]? _clientKeyFrame;
    private byte[]? _serverFinishFrame;
    private byte[]? _clientFinishFrame;
    private byte[]? _masterSecret;
    private SessionTrafficProtector? _protector;
    private PendingClockRequest? _pendingClockRequest;
    private INativeBulkTransportEndpoint? _nativeBulkEndpoint;
    private INativeBulkTransportListener? _nativeBulkListener;
    private IReliableRemoteSessionTransport? _nativeBulkTransport;
    private Task? _nativeBulkStartupTask;
    private Task? _nativeBulkAcceptTask;
    private Task? _nativeBulkReadTask;
    private int _handshakeWatchdogStarted;
    private int _clockSyncStarted;
    private int _nativeBulkStarted;
    private long _nextClockRequestId;
    private int _disposed;

    public MediaCollaborationTransport(
        IMediaSession media,
        SessionPermission permissions,
        IHybridDeviceIdentityProvider identity,
        string expectedPeerFingerprint,
        ILogger<MediaCollaborationTransport>? logger = null,
        IFileRelaySignaling? fileRelay = null,
        TimeProvider? timeProvider = null,
        INativeBulkTransportFactory? nativeBulkTransportFactory = null)
    {
        if ((permissions & ~SessionPermissionPolicy.KnownPermissions) != 0)
            throw new ArgumentOutOfRangeException(nameof(permissions));
        if (string.IsNullOrWhiteSpace(expectedPeerFingerprint))
            throw new ArgumentException("The signaling-bound peer fingerprint is required.", nameof(expectedPeerFingerprint));

        _media = media;
        _identity = identity;
        _expectedPeerFingerprint = expectedPeerFingerprint.Trim().ToLowerInvariant();
        Permissions = permissions;
        _fileRelay = fileRelay;
        _nativeBulkTransportFactory = nativeBulkTransportFactory;
        _time = timeProvider ?? TimeProvider.System;
        _nativeBulkCapacity = new NativeBulkCapacityEstimator(_time);
        _log = logger ?? NullLogger<MediaCollaborationTransport>.Instance;
        _media.DataChannelReady += OnTransportReady;
        _media.DataMessageReceived += OnDataMessage;
        _media.SecurityError += OnMediaSecurityError;
        if (_fileRelay is not null) _fileRelay.FileRelayReceived += OnFileRelayReceived;
        if (_media.IsDataChannelReady) StartHandshake();
    }

    public SessionPermission Permissions { get; }
    public bool IsReady => Volatile.Read(ref _disposed) == 0 && State == HandshakeState.Secure;
    public SecureSessionInfo? Security { get; private set; }
    public TransferPriorityMode TransferPriorityMode { get; private set; } = TransferPriorityMode.Balanced;
    public bool IsInputAcknowledgementNegotiated => _media.IsInputAcknowledgementNegotiated;
    public bool IsNativeBulkTransportReady => Volatile.Read(ref _nativeBulkTransport) is not null;
    public int NativeBulkBudgetKbps => IsNativeBulkTransportReady
        ? GetFileTransferAllocation(adaptiveBulkPath: true).MaximumBulkKbps
        : 0;
    public double NativeBulkGoodputKbps => IsNativeBulkTransportReady
        ? _nativeBulkCapacity.Snapshot.GoodputKbps
        : 0;
    public long NativeBulkFeedbackSamples => IsNativeBulkTransportReady
        ? _nativeBulkCapacity.Snapshot.FeedbackSamples
        : 0;
    internal long AdaptiveBulkFeedbackSamples => _nativeBulkCapacity.Snapshot.FeedbackSamples;
    public PeerClockEstimate? PeerClockEstimate => _media.GetPeerClockEstimate();

    public event EventHandler? Ready;
    public event EventHandler<CollaborationMessage>? MessageReceived;
    public event EventHandler<string>? ProtocolError;

    public async Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EnsureAuthorized(message);
        var bound = BindOutbound(message);
        var channel = ChannelFor(bound);
        var transferId = TransferIdFor(bound);
        var sendGate = SendGateFor(channel);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        await sendGate.WaitAsync(operation.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!IsReady || _protector is null)
                throw new InvalidOperationException("The authenticated secure session is not ready.");

            var encoded = CollaborationProtocolCodec.Encode(bound);
            var fileRelay = channel == SecureChannelKind.FileTransfer ? _fileRelay : null;
            if (channel == SecureChannelKind.FileTransfer && bound is TransferOffer)
                await WaitForNativeBulkBeforeOfferAsync(operation.Token);
            var nativeBulkTransport = channel == SecureChannelKind.FileTransfer
                ? SelectNativeBulkTransport(bound, transferId)
                : null;
            var useFileRelay = nativeBulkTransport is null
                               && fileRelay?.IsFileRelayAvailable == true;
            var adaptiveBulkPath = channel == SecureChannelKind.FileTransfer
                                   && (nativeBulkTransport is not null
                                       || (!useFileRelay && _media.IsBulkDataLaneNegotiated));
            if (adaptiveBulkPath && bound is TransferOffer && transferId != Guid.Empty)
                _adaptiveBulkTransfers.TryAdd(transferId, 0);
            var adaptiveChunkScheduled = false;
            if (bound is TransferChunk chunk && !useFileRelay)
            {
                await _filePacer.WaitAsync(
                    encoded.Length,
                    () => GetFileTransferAllocation(adaptiveBulkPath),
                    operation.Token);
                if (adaptiveBulkPath)
                {
                    var allocation = GetFileTransferAllocation(adaptiveBulkPath: true);
                    if (allocation.MaximumBulkKbps > 0)
                    {
                        _nativeBulkCapacity.RecordChunkScheduled(
                            transferId,
                            chunk.Payload.Length,
                            allocation.MaximumBulkKbps);
                        adaptiveChunkScheduled = true;
                    }
                }
            }
            var protectedFrame = _protector.Protect(channel, transferId, encoded);
            if (nativeBulkTransport is not null)
            {
                try
                {
                    await nativeBulkTransport.SendAsync(
                        RemoteTransportChannel.FileTransfer,
                        protectedFrame,
                        operation.Token);
                }
                catch
                {
                    if (adaptiveChunkScheduled && bound is TransferChunk failedChunk)
                        _nativeBulkCapacity.RecordChunkFailed(transferId, failedChunk.Payload.Length);
                    throw;
                }
                return;
            }
            if (useFileRelay)
            {
                await fileRelay!.SendFileRelayAsync(_media.SessionId, protectedFrame, operation.Token);
                return;
            }

            EnsureDirectFilePath(channel);
            var priority = channel == SecureChannelKind.FileTransfer
                ? DataMessagePriority.Bulk
                : bound is RemoteInputMessage
                    ? DataMessagePriority.Interactive
                    : DataMessagePriority.Normal;
            try
            {
                await _media.SendDataAsync(protectedFrame, operation.Token, priority);
            }
            catch
            {
                if (adaptiveChunkScheduled && bound is TransferChunk failedChunk)
                    _nativeBulkCapacity.RecordChunkFailed(transferId, failedChunk.Payload.Length);
                throw;
            }
        }
        finally
        {
            sendGate.Release();
        }
    }

    public void ForceRekey() => _protector?.ForceRekey();
    public void ReportInputLatency(InputLatencyMeasurement measurement) =>
        _media.ReportInputLatency(measurement);
    public void SetTransferPriorityMode(TransferPriorityMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _media.SetTransferPriorityMode(mode);
        TransferPriorityMode = mode;
    }
    public void CompleteTransfer(Guid transferId)
    {
        _nativeBulkTransfers.TryRemove(transferId, out _);
        _adaptiveBulkTransfers.TryRemove(transferId, out _);
        _deliveryReceipts.TryRemove(transferId, out _);
        _nativeBulkCapacity.CompleteTransfer(transferId);
        _protector?.ForgetTransfer(transferId);
    }

    public ValueTask ReportFileDeliveryAsync(
        Guid transferId,
        int deliveredBytes,
        bool flush = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deliveredBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (deliveredBytes == 0
            && flush
            && !_nativeBulkTransfers.ContainsKey(transferId))
        {
            // WebRTC completion is acknowledged by TransferComplete immediately after this call.
            // A second queued receipt can race that acknowledgement on the callback's control
            // association and adds no new throughput sample.
            return ValueTask.CompletedTask;
        }
        if ((deliveredBytes == 0 && !flush)
            || Volatile.Read(ref _disposed) != 0
            || !_adaptiveBulkTransfers.ContainsKey(transferId)
            || !IsAdaptiveBulkPathAvailable(transferId))
        {
            return ValueTask.CompletedTask;
        }

        var state = _deliveryReceipts.GetOrAdd(transferId, static _ => new DeliveryReceiptState());
        var startSender = false;
        lock (state)
        {
            if (state.DeliveredBytes > long.MaxValue - deliveredBytes) return ValueTask.CompletedTask;
            state.DeliveredBytes += deliveredBytes;
            var now = _time.GetTimestamp();
            if (!state.HasPendingBytes)
            {
                state.HasPendingBytes = true;
                state.PendingSinceTimestamp = now;
            }

            var pendingBytes = state.DeliveredBytes - state.LastReportedBytes;
            if (!flush
                && pendingBytes < ReceiptByteInterval
                && _time.GetElapsedTime(state.PendingSinceTimestamp, now) < MaximumReceiptInterval)
            {
                return ValueTask.CompletedTask;
            }
            if (pendingBytes <= 0) return ValueTask.CompletedTask;

            state.LastReportedBytes = state.DeliveredBytes;
            state.HasPendingBytes = false;
            if (!state.SenderActive)
            {
                state.SenderActive = true;
                startSender = true;
            }
        }

        // SIPSorcery can invoke the data-channel callback while holding its receive-side transport
        // lock. Queue the reverse-direction receipt so disk backpressure can unwind that callback
        // before the peer sends on the same SCTP association. One coalescing worker per transfer
        // prevents unbounded receipt tasks and keeps delivery byte counts monotonic.
        if (startSender) _ = Task.Run(() => DrainDeliveryReceiptsAsync(transferId, state));
        return ValueTask.CompletedTask;
    }

    private async Task DrainDeliveryReceiptsAsync(Guid transferId, DeliveryReceiptState state)
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            long deliveredBytes;
            lock (state)
            {
                if (state.LastSentBytes >= state.LastReportedBytes)
                {
                    state.SenderActive = false;
                    return;
                }
                deliveredBytes = state.LastReportedBytes;
            }

            try
            {
                await SendAsync(new TransferReceipt
                {
                    TransferId = transferId,
                    DeliveredBytes = deliveredBytes,
                }, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                lock (state) state.SenderActive = false;
                _log.LogDebug(ex, "Isolated bulk delivery feedback was unavailable");
                return;
            }

            lock (state) state.LastSentBytes = Math.Max(state.LastSentBytes, deliveredBytes);
        }
    }

    private HandshakeState State
    {
        get { lock (_stateGate) return _state; }
        set { lock (_stateGate) _state = value; }
    }

    private void OnTransportReady(object? sender, EventArgs args) => StartHandshake();

    private void StartHandshake()
    {
        if (_disposed != 0 || !_media.IsDataChannelReady) return;
        lock (_stateGate)
        {
            if (_state != HandshakeState.WaitingForTransport) return;
            _state = _media.Role == SessionRole.Viewer
                ? HandshakeState.SendingClientHello
                : HandshakeState.AwaitingClientHello;
        }

        if (Interlocked.Exchange(ref _handshakeWatchdogStarted, 1) == 0)
            _ = WatchHandshakeAsync();
        if (_media.Role == SessionRole.Viewer)
            _ = SendClientHelloAsync();
    }

    private async Task WatchHandshakeAsync()
    {
        try
        {
            await Task.Delay(HandshakeTimeout, _lifetime.Token);
            if (!IsReady) Fail("secure_handshake_timeout");
        }
        catch (OperationCanceledException)
        {
            // Established or disposed.
        }
    }

    private async Task SendClientHelloAsync()
    {
        try
        {
            _x25519Private = CreateX25519PrivateKey();
            var hello = new SecureClientHello
            {
                SessionId = _media.SessionId.Value,
                Nonce = RandomNumberGenerator.GetBytes(32),
                X25519PublicKey = _x25519Private.GeneratePublicKey().GetEncoded(),
                Identity = await _identity.GetPublicIdentityAsync(_lifetime.Token),
            };
            _clientHelloFrame = SecureHandshakeCodec.Encode(hello);
            State = HandshakeState.AwaitingServerHello;
            await _media.SendDataAsync(_clientHelloFrame, _lifetime.Token, DataMessagePriority.Interactive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail("secure_handshake_failed", ex);
        }
    }

    private void OnDataMessage(object? sender, ReadOnlyMemory<byte> payload) =>
        _ = ProcessInboundAsync(payload, requireDirectFilePath: true);

    private void OnFileRelayReceived(object? sender, FileRelayFrame frame)
    {
        if (frame.SessionId == _media.SessionId)
        {
            // The WebSocket receive loop must not outrun authenticated decrypt/dispatch and
            // accumulate whole file records in memory. Waiting in this background event applies
            // TCP backpressure all the way to the sender while leaving the media path separate.
            ProcessInboundAsync(frame.Payload, requireDirectFilePath: false).GetAwaiter().GetResult();
        }
    }

    private async Task ProcessInboundAsync(
        ReadOnlyMemory<byte> payload,
        bool requireDirectFilePath,
        bool nativeBulkPath = false)
    {
        var adaptiveBulkPath = nativeBulkPath
                               || (requireDirectFilePath && _media.IsBulkDataLaneNegotiated);
        try
        {
            // A peer can receive the first SCTP message before its local on-open callback is
            // dispatched. Receiving data proves the channel is open, so initialize the role
            // state before validating that first handshake frame.
            if (State == HandshakeState.WaitingForTransport && _media.IsDataChannelReady)
                StartHandshake();

            if (SecureHandshakeCodec.IsHandshakeFrame(payload.Span))
            {
                await _handshakeReceiveGate.WaitAsync(_lifetime.Token);
                try
                {
                    if (IsReady)
                    {
                        Fail("unexpected_handshake_message");
                        return;
                    }
                    await ProcessHandshakeAsync(payload);
                }
                finally
                {
                    _handshakeReceiveGate.Release();
                }
                return;
            }

            // The record header is used only to choose a bounded per-channel lane. AEAD below
            // remains authoritative, so a forged header cannot change permission or context.
            if (!SessionTrafficProtector.TryReadRoutingContext(
                    payload.Span,
                    out var routedChannel,
                    out var routedTransferId))
            {
                Fail("invalid_secure_frame");
                return;
            }

            var receiveGate = ReceiveGateFor(routedChannel);
            await receiveGate.WaitAsync(_lifetime.Token);
            try
            {
                if (!IsReady || _protector is null)
                {
                    Fail("application_data_before_secure");
                    return;
                }

                if (!_protector.TryUnprotect(
                        payload.Span,
                        out var channel,
                        out var transferId,
                        out var plaintext,
                        out var error))
                {
                    Fail(error ?? "secure_record_rejected");
                    return;
                }

                try
                {
                    if (channel != routedChannel || transferId != routedTransferId)
                    {
                        Fail("secure_record_context_mismatch");
                        return;
                    }
                    if (!requireDirectFilePath && channel != SecureChannelKind.FileTransfer)
                    {
                        Fail("invalid_file_relay_channel");
                        return;
                    }
                    if (requireDirectFilePath) EnsureDirectFilePath(channel);
                    if (channel == SecureChannelKind.Telemetry)
                    {
                        if (NativeBulkNegotiationCodec.IsFrame(plaintext))
                        {
                            try
                            {
                                await HandleNativeBulkNegotiationAsync(plaintext);
                            }
                            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                            {
                                // Session teardown.
                            }
                            catch (Exception ex)
                            {
                                // Native bulk is an optimization. A failed LAN attempt must leave
                                // the authenticated WebRTC collaboration path available.
                                _log.LogDebug(ex, "Native bulk negotiation was unavailable");
                            }
                            return;
                        }
                        // Timing is optional negotiated instrumentation shared by frame-age and
                        // input-ack features. A malformed authenticated sample is ignored rather
                        // than turning diagnostics into a session-availability dependency.
                        if (_media.IsVideoFrameTelemetryNegotiated
                            || _media.IsInputAcknowledgementNegotiated)
                        {
                            try
                            {
                                await HandleClockTelemetryAsync(plaintext);
                            }
                            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                            {
                                // Session teardown.
                            }
                            catch (Exception)
                            {
                                // Optional timing transport cannot become a disconnect path.
                            }
                        }
                        return;
                    }
                    if (!CollaborationProtocolCodec.TryDecode(plaintext, out var message, out error)
                        || message is null
                        || ChannelFor(message) != channel
                        || TransferIdFor(message) != transferId)
                    {
                        Fail(error ?? "secure_record_context_mismatch");
                        return;
                    }

                    EnsureBinding(message);
                    EnsureAuthorized(message);
                    if (message is TransferReceipt receipt)
                    {
                        if (adaptiveBulkPath && _adaptiveBulkTransfers.ContainsKey(transferId))
                        {
                            var baseline = GetBaselineFileTransferAllocation(out var statistics);
                            _nativeBulkCapacity.ObserveReceipt(
                                receipt.TransferId,
                                receipt.DeliveredBytes,
                                baseline.MaximumBulkKbps,
                                statistics);
                        }
                        return;
                    }
                    if (nativeBulkPath)
                        _nativeBulkTransfers.TryAdd(transferId, 0);
                    if (adaptiveBulkPath && message is TransferOffer)
                        _adaptiveBulkTransfers.TryAdd(transferId, 0);
                    MessageReceived?.Invoke(this, message);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            finally
            {
                receiveGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Session teardown.
        }
        catch (UnauthorizedAccessException)
        {
            Fail("permission_denied");
        }
        catch (Exception ex)
        {
            Fail("secure_protocol_error", ex);
        }
    }

    private async Task ProcessHandshakeAsync(ReadOnlyMemory<byte> frame)
    {
        if (!SecureHandshakeCodec.TryDecode(frame.Span, out var decoded, out var error) || decoded is null)
        {
            Fail(error ?? "malformed_handshake_message");
            return;
        }
        if (decoded.SessionId != _media.SessionId.Value)
        {
            Fail("secure_session_binding_mismatch");
            return;
        }

        switch (decoded)
        {
            case SecureClientHello hello when _media.Role == SessionRole.Sharer
                                             && State == HandshakeState.AwaitingClientHello:
                await HandleClientHelloAsync(hello, frame.ToArray());
                break;
            case SecureServerHello hello when _media.Role == SessionRole.Viewer
                                             && State == HandshakeState.AwaitingServerHello:
                await HandleServerHelloAsync(hello, frame.ToArray());
                break;
            case SecureClientKey key when _media.Role == SessionRole.Sharer
                                         && State == HandshakeState.AwaitingClientKey:
                await HandleClientKeyAsync(key, frame.ToArray());
                break;
            case SecureServerFinish finish when _media.Role == SessionRole.Viewer
                                                 && State == HandshakeState.AwaitingServerFinish:
                await HandleServerFinishAsync(finish, frame.ToArray());
                break;
            case SecureClientFinish finish when _media.Role == SessionRole.Sharer
                                                 && State == HandshakeState.AwaitingClientFinish:
                await HandleClientFinishAsync(finish, frame.ToArray());
                break;
            case SecureServerAck ack when _media.Role == SessionRole.Viewer
                                          && State == HandshakeState.AwaitingServerAck:
                HandleServerAck(ack);
                break;
            default:
                Fail("unexpected_handshake_message");
                break;
        }
    }

    private async Task HandleClientHelloAsync(SecureClientHello hello, byte[] frame)
    {
        SecureHandshakeCodec.ValidateHello(hello, _media.SessionId.Value);
        HybridIdentityVerifier.VerifyBinding(hello.Identity, _expectedPeerFingerprint);
        _clientHelloFrame = frame;
        _x25519Private = CreateX25519PrivateKey();
        _mlKemPrivate = PostQuantumCryptography.CreateMlKem768PrivateKey();

        var unsigned = new SecureServerHello
        {
            SessionId = _media.SessionId.Value,
            Nonce = RandomNumberGenerator.GetBytes(32),
            X25519PublicKey = _x25519Private.GeneratePublicKey().GetEncoded(),
            MlKem768PublicKey = _mlKemPrivate.ExportEncapsulationKey(),
            Identity = await _identity.GetPublicIdentityAsync(_lifetime.Token),
            Ed25519Signature = [],
            MLDsa65Signature = [],
        };
        var unsignedFrame = SecureHandshakeCodec.Encode(unsigned);
        var signatureHash = SecureHandshakeCodec.TranscriptHash(_clientHelloFrame, unsignedFrame);
        var signature = await _identity.SignTranscriptAsync(
            signatureHash,
            SecureSessionProtocol.ServerHelloSignatureContext,
            _lifetime.Token);
        _serverHelloFrame = SecureHandshakeCodec.Encode(unsigned with
        {
            Ed25519Signature = signature.Ed25519Signature,
            MLDsa65Signature = signature.MLDsa65Signature,
        });
        State = HandshakeState.AwaitingClientKey;
        await _media.SendDataAsync(_serverHelloFrame, _lifetime.Token, DataMessagePriority.Interactive);
    }

    private async Task HandleServerHelloAsync(SecureServerHello hello, byte[] frame)
    {
        SecureHandshakeCodec.ValidateHello(hello, _media.SessionId.Value);
        var unsigned = hello with { Ed25519Signature = [], MLDsa65Signature = [] };
        var signatureHash = SecureHandshakeCodec.TranscriptHash(
            Required(_clientHelloFrame),
            SecureHandshakeCodec.Encode(unsigned));
        HybridIdentityVerifier.VerifyAndFingerprint(
            hello.Identity,
            _expectedPeerFingerprint,
            signatureHash,
            SecureSessionProtocol.ServerHelloSignatureContext,
            new HybridIdentitySignature(hello.Ed25519Signature, hello.MLDsa65Signature));
        _serverHelloFrame = frame;

        var encapsulation = PostQuantumCryptography.EncapsulateMlKem768(
            hello.MlKem768PublicKey);
        var ciphertext = encapsulation.Ciphertext;
        var pqSecret = encapsulation.SharedSecret;
        var classicalSecret = CalculateX25519Secret(hello.X25519PublicKey);
        try
        {
            var unsignedKey = new SecureClientKey
            {
                SessionId = _media.SessionId.Value,
                MlKem768Ciphertext = ciphertext,
                Ed25519Signature = [],
                MLDsa65Signature = [],
                Confirmation = [],
            };
            var clientSignatureHash = SecureHandshakeCodec.TranscriptHash(
                Required(_clientHelloFrame),
                _serverHelloFrame,
                SecureHandshakeCodec.Encode(unsignedKey));
            var signature = await _identity.SignTranscriptAsync(
                clientSignatureHash,
                SecureSessionProtocol.ClientKeySignatureContext,
                _lifetime.Token);
            var signedKey = unsignedKey with
            {
                Ed25519Signature = signature.Ed25519Signature,
                MLDsa65Signature = signature.MLDsa65Signature,
            };
            var keyTranscript = SecureHandshakeCodec.TranscriptHash(
                Required(_clientHelloFrame),
                _serverHelloFrame,
                SecureHandshakeCodec.Encode(signedKey));
            _masterSecret = SecureSessionKeySchedule.DeriveMasterSecret(
                _media.SessionId,
                pqSecret,
                classicalSecret,
                keyTranscript);
            _clientKeyFrame = SecureHandshakeCodec.Encode(signedKey with
            {
                Confirmation = ComputeConfirmation(_masterSecret, "viewer", "client-key", keyTranscript),
            });
            State = HandshakeState.AwaitingServerFinish;
            await _media.SendDataAsync(_clientKeyFrame, _lifetime.Token, DataMessagePriority.Interactive);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pqSecret);
            CryptographicOperations.ZeroMemory(classicalSecret);
        }
    }

    private async Task HandleClientKeyAsync(SecureClientKey key, byte[] frame)
    {
        if (key.MlKem768Ciphertext.Length != PostQuantumCryptography.MlKem768CiphertextBytes)
            throw new CryptographicException("The ML-KEM-768 ciphertext has an invalid length.");
        var clientHello = DecodeRequired<SecureClientHello>(Required(_clientHelloFrame));
        var unsigned = key with { Ed25519Signature = [], MLDsa65Signature = [], Confirmation = [] };
        var signatureHash = SecureHandshakeCodec.TranscriptHash(
            Required(_clientHelloFrame),
            Required(_serverHelloFrame),
            SecureHandshakeCodec.Encode(unsigned));
        HybridIdentityVerifier.VerifyAndFingerprint(
            clientHello.Identity,
            _expectedPeerFingerprint,
            signatureHash,
            SecureSessionProtocol.ClientKeySignatureContext,
            new HybridIdentitySignature(key.Ed25519Signature, key.MLDsa65Signature));

        var keyTranscript = SecureHandshakeCodec.TranscriptHash(
            Required(_clientHelloFrame),
            Required(_serverHelloFrame),
            SecureHandshakeCodec.Encode(key with { Confirmation = [] }));
        var pqSecret = Required(_mlKemPrivate).Decapsulate(key.MlKem768Ciphertext);
        var classicalSecret = CalculateX25519Secret(clientHello.X25519PublicKey);
        try
        {
            _masterSecret = SecureSessionKeySchedule.DeriveMasterSecret(
                _media.SessionId,
                pqSecret,
                classicalSecret,
                keyTranscript);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pqSecret);
            CryptographicOperations.ZeroMemory(classicalSecret);
        }
        VerifyConfirmation(_masterSecret, "viewer", "client-key", keyTranscript, key.Confirmation);
        _clientKeyFrame = frame;
        var finishTranscript = SecureHandshakeCodec.TranscriptHash(
            Required(_clientHelloFrame),
            Required(_serverHelloFrame),
            _clientKeyFrame);
        _serverFinishFrame = SecureHandshakeCodec.Encode(new SecureServerFinish
        {
            SessionId = _media.SessionId.Value,
            Confirmation = ComputeConfirmation(_masterSecret, "sharer", "server-finish", finishTranscript),
        });
        State = HandshakeState.AwaitingClientFinish;
        await _media.SendDataAsync(_serverFinishFrame, _lifetime.Token, DataMessagePriority.Interactive);
    }

    private async Task HandleServerFinishAsync(SecureServerFinish finish, byte[] frame)
    {
        var finishTranscript = SecureHandshakeCodec.TranscriptHash(
            Required(_clientHelloFrame),
            Required(_serverHelloFrame),
            Required(_clientKeyFrame));
        VerifyConfirmation(Required(_masterSecret), "sharer", "server-finish", finishTranscript, finish.Confirmation);
        _serverFinishFrame = frame;
        var clientTranscript = SecureHandshakeCodec.TranscriptHash(
            Required(_clientHelloFrame),
            Required(_serverHelloFrame),
            Required(_clientKeyFrame),
            _serverFinishFrame);
        _clientFinishFrame = SecureHandshakeCodec.Encode(new SecureClientFinish
        {
            SessionId = _media.SessionId.Value,
            Confirmation = ComputeConfirmation(
                Required(_masterSecret), "viewer", "client-finish", clientTranscript),
        });
        State = HandshakeState.AwaitingServerAck;
        await _media.SendDataAsync(_clientFinishFrame, _lifetime.Token, DataMessagePriority.Interactive);
    }

    private async Task HandleClientFinishAsync(SecureClientFinish finish, byte[] frame)
    {
        var clientTranscript = SecureHandshakeCodec.TranscriptHash(
            Required(_clientHelloFrame),
            Required(_serverHelloFrame),
            Required(_clientKeyFrame),
            Required(_serverFinishFrame));
        VerifyConfirmation(Required(_masterSecret), "viewer", "client-finish", clientTranscript, finish.Confirmation);
        _clientFinishFrame = frame;
        var ackTranscript = SecureHandshakeCodec.TranscriptHash(
            Required(_clientHelloFrame),
            Required(_serverHelloFrame),
            Required(_clientKeyFrame),
            Required(_serverFinishFrame),
            _clientFinishFrame);
        var ack = new SecureServerAck
        {
            SessionId = _media.SessionId.Value,
            Confirmation = ComputeConfirmation(Required(_masterSecret), "sharer", "server-ack", ackTranscript),
        };
        // Install receive protection before the acknowledgement can make the viewer ready. Some
        // data-channel implementations deliver synchronously enough for the viewer's first secure
        // input record to race the return from SendDataAsync.
        EstablishSecureSession(raiseReady: false);
        await _media.SendDataAsync(SecureHandshakeCodec.Encode(ack), _lifetime.Token, DataMessagePriority.Interactive);
        RaiseReady();
    }

    private void HandleServerAck(SecureServerAck ack)
    {
        var ackTranscript = SecureHandshakeCodec.TranscriptHash(
            Required(_clientHelloFrame),
            Required(_serverHelloFrame),
            Required(_clientKeyFrame),
            Required(_serverFinishFrame),
            Required(_clientFinishFrame));
        VerifyConfirmation(Required(_masterSecret), "sharer", "server-ack", ackTranscript, ack.Confirmation);
        EstablishSecureSession(raiseReady: true);
    }

    private void EstablishSecureSession(bool raiseReady)
    {
        if (State == HandshakeState.Secure) return;
        _protector = new SessionTrafficProtector(Required(_masterSecret), _media.SessionId, _media.Role);
        _media.SetTrafficProtector(_protector);
        var peerIdentity = _media.Role == SessionRole.Viewer
            ? DecodeRequired<SecureServerHello>(Required(_serverHelloFrame)).Identity
            : DecodeRequired<SecureClientHello>(Required(_clientHelloFrame)).Identity;
        Security = new SecureSessionInfo
        {
            ProtocolVersion = SecureSessionProtocol.ProtocolVersion,
            HandshakeSuite = SecureSessionProtocol.HandshakeSuite,
            IdentitySuite = SecureSessionProtocol.IdentitySuite,
            TrafficProtection = SecureSessionProtocol.TrafficProtection,
            KeyDerivation = SecureSessionProtocol.KeyDerivation,
            PeerIdentityFingerprint = peerIdentity.Fingerprint,
            EstablishedAt = DateTimeOffset.UtcNow,
            PeerAuthenticated = true,
            PostQuantumProtected = true,
            ConnectionPath = _media.GetStatistics().ConnectionPath,
        };
        State = HandshakeState.Secure;
        ClearHandshakeSecrets(keepProtector: true);
        if (raiseReady) RaiseReady();
    }

    private void RaiseReady()
    {
        if (_media.Role == SessionRole.Viewer
            && Interlocked.Exchange(ref _nativeBulkStarted, 1) == 0)
        {
            _nativeBulkStartupTask = StartNativeBulkNegotiationAsync();
        }
        Ready?.Invoke(this, EventArgs.Empty);
        if (_media.Role == SessionRole.Viewer
            && (_media.IsVideoFrameTelemetryNegotiated
                || _media.IsInputAcknowledgementNegotiated)
            && Interlocked.Exchange(ref _clockSyncStarted, 1) == 0)
        {
            _ = SynchronizePeerClockAsync();
        }
    }

    private async Task StartNativeBulkNegotiationAsync()
    {
        try
        {
            if (!CanAttemptNativeBulk() || await WaitForDirectLanPeerAsync() is null) return;

            byte[] clientHello;
            await _nativeBulkNegotiationGate.WaitAsync(_lifetime.Token);
            try
            {
                _nativeBulkEndpoint ??= await _nativeBulkTransportFactory!
                    .CreateEndpointAsync(_lifetime.Token);
                clientHello = NativeBulkNegotiationCodec.EncodeClientHello(
                    _nativeBulkEndpoint.CertificateSha256);
            }
            finally
            {
                _nativeBulkNegotiationGate.Release();
            }

            await SendNativeBulkNegotiationAsync(clientHello, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Session teardown.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Native bulk startup was unavailable");
        }
    }

    private async Task HandleNativeBulkNegotiationAsync(ReadOnlyMemory<byte> plaintext)
    {
        if (!CanAttemptNativeBulk()) return;
        if (_media.Role == SessionRole.Sharer)
        {
            if (!NativeBulkNegotiationCodec.TryDecodeClientHello(
                    plaintext.Span,
                    out var peerCertificateSha256))
            {
                return;
            }

            var remoteAddress = await WaitForDirectLanPeerAsync();
            if (remoteAddress is null) return;

            byte[] serverOffer;
            await _nativeBulkNegotiationGate.WaitAsync(_lifetime.Token);
            try
            {
                if (Volatile.Read(ref _nativeBulkTransport) is not null
                    || _nativeBulkListener is not null)
                {
                    return;
                }

                _nativeBulkEndpoint ??= await _nativeBulkTransportFactory!
                    .CreateEndpointAsync(_lifetime.Token);
                var localAddress = remoteAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any;
                _nativeBulkListener = await _nativeBulkEndpoint.ListenAsync(
                    localAddress,
                    peerCertificateSha256,
                    _lifetime.Token);
                _nativeBulkAcceptTask = AcceptNativeBulkAsync(_nativeBulkListener);
                serverOffer = NativeBulkNegotiationCodec.EncodeServerOffer(
                    _nativeBulkEndpoint.CertificateSha256,
                    _nativeBulkListener.LocalEndPoint.Port);
            }
            finally
            {
                _nativeBulkNegotiationGate.Release();
            }

            await SendNativeBulkNegotiationAsync(serverOffer, _lifetime.Token);
            return;
        }

        if (!NativeBulkNegotiationCodec.TryDecodeServerOffer(
                plaintext.Span,
                out var serverCertificateSha256,
                out var serverPort))
        {
            return;
        }

        var serverAddress = await WaitForDirectLanPeerAsync();
        if (serverAddress is null) return;
        await _nativeBulkNegotiationGate.WaitAsync(_lifetime.Token);
        try
        {
            if (Volatile.Read(ref _nativeBulkTransport) is not null) return;
            _nativeBulkEndpoint ??= await _nativeBulkTransportFactory!
                .CreateEndpointAsync(_lifetime.Token);
            var transport = await _nativeBulkEndpoint.ConnectAsync(
                new IPEndPoint(serverAddress, serverPort),
                serverCertificateSha256,
                _lifetime.Token);
            ActivateNativeBulkTransport(transport);
        }
        finally
        {
            _nativeBulkNegotiationGate.Release();
        }
    }

    private async Task AcceptNativeBulkAsync(INativeBulkTransportListener listener)
    {
        try
        {
            var transport = await listener.AcceptAsync(_lifetime.Token);
            ActivateNativeBulkTransport(transport);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Session teardown.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Native bulk listener was unavailable");
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _nativeBulkListener, null, listener),
                    listener))
            {
                try { await listener.DisposeAsync(); }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Native bulk listener cleanup completed with an error");
                }
            }
        }
    }

    private void ActivateNativeBulkTransport(IReliableRemoteSessionTransport transport)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _nativeBulkTransport, transport, null) is not null)
        {
            _ = DisposeRejectedNativeBulkAsync(transport);
            return;
        }

        _nativeBulkReadTask = ReadNativeBulkAsync(transport);
    }

    private async Task ReadNativeBulkAsync(IReliableRemoteSessionTransport transport)
    {
        try
        {
            await foreach (var payload in transport.ReadAllAsync(
                               RemoteTransportChannel.FileTransfer,
                               _lifetime.Token))
            {
                await ProcessInboundAsync(
                    payload,
                    requireDirectFilePath: false,
                    nativeBulkPath: true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Session teardown.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Native bulk path ended; the interactive session remains active");
        }
        finally
        {
            Interlocked.CompareExchange(ref _nativeBulkTransport, null, transport);
            try { await transport.DisposeAsync(); }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Native bulk transport cleanup completed with an error");
            }
        }
    }

    private async Task DisposeRejectedNativeBulkAsync(IReliableRemoteSessionTransport transport)
    {
        try { await transport.DisposeAsync(); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Duplicate native bulk transport cleanup completed with an error");
        }
    }

    private async Task SendNativeBulkNegotiationAsync(
        byte[] plaintext,
        CancellationToken cancellationToken)
    {
        await _telemetrySendGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsReady || _protector is null) return;
            var protectedFrame = _protector.Protect(
                SecureChannelKind.Telemetry,
                Guid.Empty,
                plaintext);
            await _media.SendDataAsync(
                protectedFrame,
                cancellationToken,
                DataMessagePriority.Interactive);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            _telemetrySendGate.Release();
        }
    }

    private bool CanAttemptNativeBulk() =>
        Permissions.HasFlag(SessionPermission.FileTransfer)
        && _media.IsNativeBulkTransportNegotiated
        && _nativeBulkTransportFactory?.IsSupported == true;

    private async ValueTask<IPAddress?> WaitForDirectLanPeerAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (_media.GetStatistics().ConnectionPath == ConnectionPath.DirectLan
                && _media.GetSelectedRemoteAddress() is { } address)
            {
                return address;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), _time, _lifetime.Token);
        }
        return null;
    }

    private async Task SynchronizePeerClockAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            PeerClockEstimate? best = null;
            try
            {
                for (var sample = 0; sample < 5 && !_lifetime.IsCancellationRequested; sample++)
                {
                    var requestId = checked((ulong)Interlocked.Increment(ref _nextClockRequestId));
                    var localSentAt = GetUnixMicroseconds(_time.GetUtcNow());
                    var completion = new TaskCompletionSource<ClockReply>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_clockGate)
                    {
                        _pendingClockRequest = new PendingClockRequest(requestId, localSentAt, completion);
                    }

                    try
                    {
                        await SendClockTelemetryAsync(
                            ClockSyncCodec.EncodeRequest(requestId, localSentAt),
                            _lifetime.Token);
                        var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(1), _lifetime.Token);
                        if (PeerClockEstimator.TryCalculate(
                                localSentAt,
                                reply.RemoteReceivedAtUnixMicroseconds,
                                reply.RemoteSentAtUnixMicroseconds,
                                reply.LocalReceivedAtUnixMicroseconds,
                                out var calculatedEstimate)
                            && (best is null
                                || calculatedEstimate.UncertaintyMicroseconds < best.Value.UncertaintyMicroseconds))
                        {
                            best = calculatedEstimate;
                        }
                    }
                    catch (TimeoutException)
                    {
                        // Optional diagnostics must never disconnect or stall the media session.
                        break;
                    }
                    finally
                    {
                        lock (_clockGate)
                        {
                            if (_pendingClockRequest?.RequestId == requestId)
                                _pendingClockRequest = null;
                        }
                    }

                    if (sample < 4)
                        await Task.Delay(TimeSpan.FromMilliseconds(25), _lifetime.Token);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Timing is observability-only and cannot become a session failure path.
            }

            if (best is { } estimate)
                _media.SetPeerClockEstimate(estimate);

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task HandleClockTelemetryAsync(ReadOnlyMemory<byte> plaintext)
    {
        var receivedAt = GetUnixMicroseconds(_time.GetUtcNow());
        if (_media.Role == SessionRole.Sharer
            && ClockSyncCodec.TryDecodeRequest(
                plaintext.Span,
                out var requestId,
                out var viewerSentAt))
        {
            var sentAt = GetUnixMicroseconds(_time.GetUtcNow());
            await SendClockTelemetryAsync(
                ClockSyncCodec.EncodeReply(requestId, viewerSentAt, receivedAt, sentAt),
                _lifetime.Token);
            return;
        }

        if (_media.Role != SessionRole.Viewer
            || !ClockSyncCodec.TryDecodeReply(
                plaintext.Span,
                out var replyId,
                out var localSentAt,
                out var remoteReceivedAt,
                out var remoteSentAt))
        {
            return;
        }

        PendingClockRequest? pending;
        lock (_clockGate)
        {
            pending = _pendingClockRequest;
            if (pending is null
                || pending.RequestId != replyId
                || pending.LocalSentAtUnixMicroseconds != localSentAt)
            {
                return;
            }
            _pendingClockRequest = null;
        }

        pending.Completion.TrySetResult(new ClockReply(
            remoteReceivedAt,
            remoteSentAt,
            receivedAt));
    }

    private async Task SendClockTelemetryAsync(byte[] plaintext, CancellationToken cancellationToken)
    {
        await _telemetrySendGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsReady || _protector is null) return;
            var protectedFrame = _protector.Protect(
                SecureChannelKind.Telemetry,
                Guid.Empty,
                plaintext);
            await _media.SendDataAsync(
                protectedFrame,
                cancellationToken,
                DataMessagePriority.Interactive);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            _telemetrySendGate.Release();
        }
    }

    private static long GetUnixMicroseconds(DateTimeOffset timestamp) =>
        (timestamp.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    private sealed record PendingClockRequest(
        ulong RequestId,
        long LocalSentAtUnixMicroseconds,
        TaskCompletionSource<ClockReply> Completion);

    private readonly record struct ClockReply(
        long RemoteReceivedAtUnixMicroseconds,
        long RemoteSentAtUnixMicroseconds,
        long LocalReceivedAtUnixMicroseconds);

    private byte[] CalculateX25519Secret(byte[] peerPublicKey)
    {
        if (peerPublicKey.Length != X25519PublicKeyParameters.KeySize)
            throw new CryptographicException("The X25519 public key has an invalid length.");
        var secret = new byte[X25519PrivateKeyParameters.SecretSize];
        Required(_x25519Private).GenerateSecret(new X25519PublicKeyParameters(peerPublicKey), secret, 0);
        return secret;
    }

    private static X25519PrivateKeyParameters CreateX25519PrivateKey() =>
        new(RandomNumberGenerator.GetBytes(X25519PrivateKeyParameters.KeySize));

    private static byte[] ComputeConfirmation(
        ReadOnlySpan<byte> masterSecret,
        string direction,
        string label,
        ReadOnlySpan<byte> transcriptHash)
    {
        var key = SecureSessionKeySchedule.DeriveAuthenticationKey(masterSecret, direction);
        var labelBytes = Encoding.UTF8.GetBytes($"PeerOnQ {label} v1");
        var input = new byte[labelBytes.Length + transcriptHash.Length];
        labelBytes.CopyTo(input, 0);
        transcriptHash.CopyTo(input.AsSpan(labelBytes.Length));
        try { return HMACSHA512.HashData(key, input); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static void VerifyConfirmation(
        ReadOnlySpan<byte> masterSecret,
        string direction,
        string label,
        ReadOnlySpan<byte> transcriptHash,
        ReadOnlySpan<byte> confirmation)
    {
        var expected = ComputeConfirmation(masterSecret, direction, label, transcriptHash);
        try
        {
            if (confirmation.Length != expected.Length
                || !CryptographicOperations.FixedTimeEquals(expected, confirmation))
                throw new CryptographicException("Secure handshake key confirmation failed.");
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    private static T DecodeRequired<T>(byte[] frame) where T : SecureHandshakeMessage
    {
        if (!SecureHandshakeCodec.TryDecode(frame, out var message, out _) || message is not T typed)
            throw new CryptographicException("The secure handshake transcript is invalid.");
        return typed;
    }

    private CollaborationMessage BindOutbound(CollaborationMessage message)
    {
        if (message.SessionId != Guid.Empty && message.SessionId != _media.SessionId.Value)
            throw new InvalidOperationException("A collaboration message cannot be rebound to another session.");
        if (message.PermissionGeneration is > 0 and not InitialPermissionGeneration)
            throw new InvalidOperationException("A collaboration message has a stale permission generation.");
        return message with
        {
            SessionId = _media.SessionId.Value,
            PermissionGeneration = InitialPermissionGeneration,
        };
    }

    private void EnsureBinding(CollaborationMessage message)
    {
        if (message.SessionId != _media.SessionId.Value)
            throw new InvalidDataException("session_binding_mismatch");
        if (message.PermissionGeneration != InitialPermissionGeneration)
            throw new InvalidDataException("permission_generation_mismatch");
    }

    private void EnsureAuthorized(CollaborationMessage message)
    {
        var required = message switch
        {
            ClipboardTextUpdate or ClipboardStateChange => SessionPermission.ClipboardText,
            RemoteInputMessage => SessionPermission.ControlInput,
            _ => SessionPermission.FileTransfer,
        };
        if (!Permissions.HasFlag(required))
            throw new UnauthorizedAccessException("The operation is outside this session's immutable permission scope.");
    }

    private void EnsureDirectFilePath(SecureChannelKind channel)
    {
        if (channel != SecureChannelKind.FileTransfer) return;
        var path = _media.GetStatistics().ConnectionPath;
        if (path is not (ConnectionPath.DirectLan or ConnectionPath.DirectInternet))
            throw new InvalidOperationException("direct_p2p_required");
    }

    private IReliableRemoteSessionTransport? SelectNativeBulkTransport(
        CollaborationMessage message,
        Guid transferId)
    {
        if (transferId == Guid.Empty) return null;
        var transport = Volatile.Read(ref _nativeBulkTransport);
        if (transport is not null && !IsNativeBulkPathCurrent(transport))
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _nativeBulkTransport, null, transport),
                    transport))
            {
                _ = CloseSupersededNativeBulkAsync(transport);
            }
            transport = null;
        }
        if (message is TransferOffer && transport is not null)
            _nativeBulkTransfers.TryAdd(transferId, 0);

        if (!_nativeBulkTransfers.ContainsKey(transferId)) return null;
        return transport
               ?? throw new IOException("native_bulk_path_unavailable");
    }

    private async ValueTask WaitForNativeBulkBeforeOfferAsync(CancellationToken cancellationToken)
    {
        if (!CanAttemptNativeBulk()
            || _media.GetStatistics().ConnectionPath != ConnectionPath.DirectLan)
        {
            return;
        }

        var startedAt = _time.GetTimestamp();
        while (Volatile.Read(ref _nativeBulkTransport) is null
               && CanAttemptNativeBulk()
               && _media.GetStatistics().ConnectionPath == ConnectionPath.DirectLan
               && _time.GetElapsedTime(startedAt) < NativeBulkOfferWait)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), _time, cancellationToken);
        }
    }

    private bool IsAdaptiveBulkPathAvailable(Guid transferId)
    {
        if (_nativeBulkTransfers.ContainsKey(transferId))
            return Volatile.Read(ref _nativeBulkTransport) is not null;
        return _media.IsBulkDataLaneNegotiated;
    }

    private bool IsNativeBulkPathCurrent(IReliableRemoteSessionTransport transport)
    {
        if (_media.GetStatistics().ConnectionPath != ConnectionPath.DirectLan
            || _media.GetSelectedRemoteAddress() is not { } selectedAddress
            || transport.RemoteEndPoint is not IPEndPoint remoteEndPoint)
        {
            return false;
        }

        return selectedAddress.Equals(remoteEndPoint.Address)
               || selectedAddress.MapToIPv6().Equals(remoteEndPoint.Address.MapToIPv6());
    }

    private async Task CloseSupersededNativeBulkAsync(IReliableRemoteSessionTransport transport)
    {
        try { await transport.CloseAsync(_lifetime.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Session teardown or the read loop already owned closure.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Superseded native bulk path closed with a transport error");
        }
    }

    private static SecureChannelKind ChannelFor(CollaborationMessage message) => message switch
    {
        RemoteInputMessage => SecureChannelKind.Input,
        ClipboardTextUpdate or ClipboardStateChange => SecureChannelKind.Clipboard,
        TransferOffer or TransferAccept or TransferReject or TransferChunk or TransferPause
            or TransferResume or TransferCancel or TransferComplete or TransferFailed
            or TransferReceipt => SecureChannelKind.FileTransfer,
        _ => throw new InvalidDataException("unsupported_collaboration_message"),
    };

    private static Guid TransferIdFor(CollaborationMessage message) => message switch
    {
        TransferOffer value => value.TransferId,
        TransferAccept value => value.TransferId,
        TransferReject value => value.TransferId,
        TransferChunk value => value.TransferId,
        TransferPause value => value.TransferId,
        TransferResume value => value.TransferId,
        TransferCancel value => value.TransferId,
        TransferComplete value => value.TransferId,
        TransferFailed value => value.TransferId,
        TransferReceipt value => value.TransferId,
        _ => Guid.Empty,
    };

    private SemaphoreSlim SendGateFor(SecureChannelKind channel) => channel switch
    {
        SecureChannelKind.Input => _inputSendGate,
        SecureChannelKind.Clipboard => _clipboardSendGate,
        SecureChannelKind.FileTransfer => _fileSendGate,
        SecureChannelKind.Telemetry => _telemetrySendGate,
        _ => throw new InvalidDataException("unsupported_secure_channel"),
    };

    private SemaphoreSlim ReceiveGateFor(SecureChannelKind channel) => channel switch
    {
        SecureChannelKind.Input => _inputReceiveGate,
        SecureChannelKind.Clipboard => _clipboardReceiveGate,
        SecureChannelKind.FileTransfer => _fileReceiveGate,
        SecureChannelKind.Telemetry => _telemetryReceiveGate,
        _ => throw new InvalidDataException("unsupported_secure_channel"),
    };

    private TransferAllocationPolicy GetFileTransferAllocation(bool adaptiveBulkPath)
    {
        var baseline = GetBaselineFileTransferAllocation(out var statistics);
        return adaptiveBulkPath ? _nativeBulkCapacity.Apply(baseline, statistics) : baseline;
    }

    private TransferAllocationPolicy GetBaselineFileTransferAllocation(out MediaStatistics statistics)
    {
        statistics = _media.GetStatistics();
        var hasLiveMedia = (Permissions & SessionPermission.ViewScreen) != 0
                           && (statistics.FramesCaptured > 0
                               || statistics.FramesEncoded > 0
                               || statistics.FramesRendered > 0
                               || statistics.CurrentBitrateKbps > 0);
        if (!hasLiveMedia)
        {
            statistics = statistics with
            {
                CurrentBitrateKbps = 0,
                TargetBitrateKbps = 0,
            };
        }

        return ConnectionPolicyProvider.GetTransferAllocation(
            TransferPriorityMode,
            statistics,
            hasInteractiveTraffic: Permissions.HasFlag(SessionPermission.ControlInput));
    }

    private void OnMediaSecurityError(object? sender, string reason) => Fail(reason);

    private void Fail(string reason, Exception? exception = null)
    {
        lock (_stateGate)
        {
            if (_state is HandshakeState.Failed or HandshakeState.Closed) return;
            _state = HandshakeState.Failed;
        }
        _media.SetTrafficProtector(null);
        if (exception is null)
            _log.LogWarning("Secure session rejected: {Reason}", reason);
        else
            _log.LogWarning(exception, "Secure session rejected: {Reason}", reason);
        ClearHandshakeSecrets(keepProtector: false);
        ProtocolError?.Invoke(this, reason);
    }

    private void ClearHandshakeSecrets(bool keepProtector)
    {
        _x25519Private = null;
        _mlKemPrivate?.Dispose();
        _mlKemPrivate = null;
        if (_masterSecret is not null)
        {
            CryptographicOperations.ZeroMemory(_masterSecret);
            _masterSecret = null;
        }
        _clientHelloFrame = null;
        _serverHelloFrame = null;
        _clientKeyFrame = null;
        _serverFinishFrame = null;
        _clientFinishFrame = null;
        if (!keepProtector)
        {
            _protector?.Dispose();
            _protector = null;
            Security = null;
        }
    }

    private static T Required<T>(T? value) where T : class =>
        value ?? throw new CryptographicException("The secure handshake state is incomplete.");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        State = HandshakeState.Closed;
        await _lifetime.CancelAsync();
        _media.DataChannelReady -= OnTransportReady;
        _media.DataMessageReceived -= OnDataMessage;
        _media.SecurityError -= OnMediaSecurityError;
        if (_fileRelay is not null) _fileRelay.FileRelayReceived -= OnFileRelayReceived;

        var nativeListener = Interlocked.Exchange(ref _nativeBulkListener, null);
        if (nativeListener is not null) await nativeListener.DisposeAsync();
        foreach (var task in new[] { _nativeBulkStartupTask, _nativeBulkAcceptTask, _nativeBulkReadTask })
        {
            if (task is null) continue;
            try { await task; }
            catch (OperationCanceledException) { /* session teardown */ }
            catch (Exception ex) { _log.LogDebug(ex, "Native bulk cleanup completed after a transport error"); }
        }
        var nativeTransport = Interlocked.Exchange(ref _nativeBulkTransport, null);
        if (nativeTransport is not null) await nativeTransport.DisposeAsync();
        if (_nativeBulkEndpoint is not null) await _nativeBulkEndpoint.DisposeAsync();

        foreach (var sendGate in new[]
                 {
                     _inputSendGate,
                     _clipboardSendGate,
                     _fileSendGate,
                     _telemetrySendGate,
                 })
        {
            await sendGate.WaitAsync();
            sendGate.Release();
        }
        _media.SetTrafficProtector(null);
        foreach (var receiveGate in new[]
                 {
                     _handshakeReceiveGate,
                     _inputReceiveGate,
                     _clipboardReceiveGate,
                     _fileReceiveGate,
                     _telemetryReceiveGate,
                 })
        {
            await receiveGate.WaitAsync();
            receiveGate.Release();
        }
        ClearHandshakeSecrets(keepProtector: false);
        _inputSendGate.Dispose();
        _clipboardSendGate.Dispose();
        _fileSendGate.Dispose();
        _telemetrySendGate.Dispose();
        _handshakeReceiveGate.Dispose();
        _inputReceiveGate.Dispose();
        _clipboardReceiveGate.Dispose();
        _fileReceiveGate.Dispose();
        _telemetryReceiveGate.Dispose();
        _nativeBulkNegotiationGate.Dispose();
        _lifetime.Dispose();
    }

    private enum HandshakeState
    {
        WaitingForTransport,
        SendingClientHello,
        AwaitingClientHello,
        AwaitingServerHello,
        AwaitingClientKey,
        AwaitingServerFinish,
        AwaitingClientFinish,
        AwaitingServerAck,
        Secure,
        Failed,
        Closed,
    }

    private sealed class DeliveryReceiptState
    {
        public long DeliveredBytes { get; set; }
        public long LastReportedBytes { get; set; }
        public long LastSentBytes { get; set; }
        public long PendingSinceTimestamp { get; set; }
        public bool HasPendingBytes { get; set; }
        public bool SenderActive { get; set; }
    }
}

internal sealed class AdaptiveFileTransferPacer(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MaximumRefreshDelay = TimeSpan.FromMilliseconds(250);
    private const double MaximumSchedulerCreditSeconds = 0.016;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private long _lastTimestamp;
    private double _availableBytes;
    private int _previousRateKbps;
    private bool _initialized;

    public async ValueTask WaitAsync(
        int payloadBytes,
        Func<TransferAllocationPolicy> policyProvider,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadBytes);
        ArgumentNullException.ThrowIfNull(policyProvider);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var policy = policyProvider();
            if (policy.MaximumBulkKbps <= 0)
            {
                Reset();
                return;
            }

            TimeSpan requiredDelay;
            lock (_gate)
            {
                var now = _time.GetTimestamp();
                var bytesPerSecond = policy.MaximumBulkKbps * 1000d / 8d;
                // Throughput is controlled by the refill rate. Preserve at most one Windows timer
                // quantum of scheduler overshoot beyond one application record so coarse wakes do not
                // turn a 150 Mbps policy into a materially lower payload rate. The initial credit
                // remains exactly one record, so a fast producer still cannot front-load a large
                // bulk queue ahead of input.
                var bucketCapacity = payloadBytes + (bytesPerSecond * MaximumSchedulerCreditSeconds);
                if (!_initialized)
                {
                    _initialized = true;
                    _lastTimestamp = now;
                    _previousRateKbps = policy.MaximumBulkKbps;
                    _availableBytes = payloadBytes;
                }
                else
                {
                    var previousBytesPerSecond = _previousRateKbps * 1000d / 8d;
                    var elapsed = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
                    _availableBytes = Math.Min(
                        bucketCapacity,
                        _availableBytes + (elapsed * previousBytesPerSecond));
                    _lastTimestamp = now;
                    _previousRateKbps = policy.MaximumBulkKbps;
                }

                if (_availableBytes >= payloadBytes)
                {
                    _availableBytes -= payloadBytes;
                    return;
                }

                requiredDelay = TimeSpan.FromSeconds(
                    (payloadBytes - _availableBytes) / bytesPerSecond);
            }

            var delay = requiredDelay > MaximumRefreshDelay
                ? MaximumRefreshDelay
                : requiredDelay < TimeSpan.FromMilliseconds(1)
                    ? TimeSpan.FromMilliseconds(1)
                    : requiredDelay;
            await Task.Delay(delay, _time, cancellationToken);
        }
    }

    private void Reset()
    {
        lock (_gate)
        {
            _initialized = false;
            _lastTimestamp = 0;
            _availableBytes = 0;
            _previousRateKbps = 0;
        }
    }
}
