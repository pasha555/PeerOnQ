using System.Diagnostics;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.Application.Collaboration;

public sealed record RemoteInputPermissionStatus(bool IsAvailable, string ReasonCode);

/// <summary>
/// Sends and receives remote input only inside the immutable ControlInput permission scope.
/// Every frame is bound to the signaling session, the current connection generation, and an
/// explicitly acknowledged viewer-focus generation. Pointer motion is coalesced and all sends
/// use the ordered WebRTC data channel through a bounded local queue.
/// </summary>
public sealed class RemoteInputSession : IAsyncDisposable
{
    public const int CurrentInputProtocolVersion = 2;

    private const int MaximumReceivedCommandsPerSecond = 2_000;
    private const int MaximumPendingSends = 256;
    private const int MaximumPendingMeasurements = 32;
    private const int MaximumPointerCommandsPerSecond = 240;
    private static readonly TimeSpan FocusAcknowledgmentTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinimumPointerSendInterval =
        TimeSpan.FromSeconds(1d / MaximumPointerCommandsPerSecond);
    private static readonly TimeSpan MinimumMeasurementInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MeasurementTimeout = TimeSpan.FromSeconds(5);

    private readonly SessionId _sessionId;
    private readonly SessionRole _role;
    private readonly ICollaborationTransport _transport;
    private readonly IRemoteInputSink? _sink;
    private readonly RemoteInputFocusCoordinator? _focusCoordinator;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _transportSendGate = new(1, 1);
    private readonly SemaphoreSlim _focusChangeGate = new(1, 1);
    private readonly Lock _pointerGate = new();
    private readonly Lock _receiveGate = new();
    private readonly Lock _stateGate = new();
    private readonly Lock _measurementGate = new();
    private readonly Dictionary<long, PendingInputMeasurement> _pendingMeasurements = [];
    private RemoteInputEvent? _pendingPointer;
    private TaskCompletionSource<bool>? _focusAcknowledgment;
    private long _pendingFocusGeneration;
    private bool _pointerPumpRunning;
    private long _sendSequence;
    private long _lastReceivedSequence;
    private long _lastPointerSendTimestamp;
    private long _lastMeasurementTimestamp;
    private long _sessionGeneration = 1;
    private long _focusGeneration;
    private long _activeRemoteFocusGeneration;
    private long _lastRemoteFocusGeneration;
    private long _receiveWindowStarted = Stopwatch.GetTimestamp();
    private int _receivedInWindow;
    private int _pendingSends;
    private int _locallyEnabled;
    private int _connectionInterrupted;
    private int _permissionRevoked;
    private int _protocolFailed;
    private int _revocationPending;
    private int _disposed;

    public RemoteInputSession(
        SessionId sessionId,
        SessionRole role,
        ICollaborationTransport transport,
        IRemoteInputSink? sink = null,
        RemoteInputFocusCoordinator? focusCoordinator = null,
        TimeProvider? timeProvider = null)
    {
        if (sessionId == default) throw new ArgumentException("A session binding is required.", nameof(sessionId));
        if (!transport.Permissions.HasFlag(SessionPermission.ControlInput))
            throw new UnauthorizedAccessException("Input transport requires the immutable ControlInput permission.");
        if (role == SessionRole.Sharer && sink is null)
            throw new ArgumentNullException(nameof(sink), "A sharer requires a platform input sink.");

        _sessionId = sessionId;
        _role = role;
        _transport = transport;
        _sink = sink;
        _focusCoordinator = focusCoordinator;
        _time = timeProvider ?? TimeProvider.System;
        _transport.MessageReceived += OnMessageReceived;
        _transport.ProtocolError += OnProtocolError;
        _transport.Ready += OnTransportReady;
    }

    public bool IsLocallyEnabled => Volatile.Read(ref _locallyEnabled) == 1;
    public bool IsControlPermissionAvailable => Volatile.Read(ref _permissionRevoked) == 0;

    public event EventHandler<string>? Warning;
    public event EventHandler<RemoteInputPermissionStatus>? ControlPermissionChanged;

    /// <summary>
    /// Viewer-side capture switch. Enabling succeeds only after the sharer acknowledges a new
    /// focus generation. Turning capture off clears local forwarding before sending ReleaseAll.
    /// </summary>
    public async Task SetLocalCaptureEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_role != SessionRole.Viewer)
            throw new InvalidOperationException("Only the viewer captures local input.");

        await _focusChangeGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!enabled)
            {
                await DisableLocalCaptureAsync(cancellationToken);
                return;
            }

            if (!IsControlPermissionAvailable)
                throw new UnauthorizedAccessException("The remote owner revoked input control for this session.");
            if (!_transport.IsReady || Volatile.Read(ref _connectionInterrupted) != 0)
                throw new InvalidOperationException("The authorized input channel is not connected.");
            if (IsLocallyEnabled) return;

            var focusGeneration = Interlocked.Increment(ref _focusGeneration);
            var sessionGeneration = Volatile.Read(ref _sessionGeneration);
            var acknowledgment = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateGate)
            {
                if (_focusAcknowledgment is not null)
                    throw new InvalidOperationException("An input-focus request is already pending.");
                _focusAcknowledgment = acknowledgment;
                _pendingFocusGeneration = focusGeneration;
            }

            try
            {
                using var focusTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                focusTimeout.CancelAfter(FocusAcknowledgmentTimeout);
                await SendMessageAsync(sequence => new RemoteInputFocusRequest
                {
                    InputVersion = CurrentInputProtocolVersion,
                    SessionId = _sessionId.Value,
                    SessionGeneration = sessionGeneration,
                    FocusGeneration = focusGeneration,
                    Sequence = sequence,
                    Enabled = true,
                }, focusTimeout.Token).WaitAsync(focusTimeout.Token);

                var accepted = await acknowledgment.Task.WaitAsync(focusTimeout.Token);
                if (!accepted || !IsControlPermissionAvailable)
                    throw new UnauthorizedAccessException("The remote owner did not authorize input focus.");
                if (sessionGeneration != Volatile.Read(ref _sessionGeneration)
                    || Volatile.Read(ref _connectionInterrupted) != 0)
                    throw new InvalidOperationException("The input connection changed while focus was being authorized.");

                Volatile.Write(ref _locallyEnabled, 1);
            }
            finally
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_focusAcknowledgment, acknowledgment))
                    {
                        _focusAcknowledgment = null;
                        _pendingFocusGeneration = 0;
                    }
                }
            }
        }
        finally
        {
            _focusChangeGate.Release();
        }
    }

    public Task SendPointerMoveAsync(
        double normalizedX,
        double normalizedY,
        CancellationToken cancellationToken = default)
    {
        var input = new RemoteInputEvent
        {
            Kind = RemoteInputEventKind.PointerMove,
            NormalizedX = normalizedX,
            NormalizedY = normalizedY,
        };
        EnsureCanSend(input);

        lock (_pointerGate)
        {
            _pendingPointer = input;
            if (_pointerPumpRunning) return Task.CompletedTask;
            _pointerPumpRunning = true;
        }

        return PumpPointerAsync(cancellationToken);
    }

    public Task SendPointerButtonAsync(
        double normalizedX,
        double normalizedY,
        RemotePointerButton button,
        bool isPressed,
        CancellationToken cancellationToken = default) =>
        SendInputAsync(new RemoteInputEvent
        {
            Kind = RemoteInputEventKind.PointerButton,
            NormalizedX = normalizedX,
            NormalizedY = normalizedY,
            Button = button,
            IsPressed = isPressed,
        }, cancellationToken);

    public Task SendPointerWheelAsync(
        double normalizedX,
        double normalizedY,
        int delta,
        bool isHorizontal = false,
        CancellationToken cancellationToken = default) =>
        SendInputAsync(new RemoteInputEvent
        {
            Kind = RemoteInputEventKind.PointerWheel,
            NormalizedX = normalizedX,
            NormalizedY = normalizedY,
            WheelDelta = delta,
            IsHorizontalWheel = isHorizontal,
        }, cancellationToken);

    public Task SendKeyAsync(
        ushort virtualKey,
        bool isPressed,
        bool isExtendedKey,
        CancellationToken cancellationToken = default) =>
        SendInputAsync(new RemoteInputEvent
        {
            Kind = RemoteInputEventKind.Key,
            VirtualKey = virtualKey,
            IsPressed = isPressed,
            IsExtendedKey = isExtendedKey,
        }, cancellationToken);

    /// <summary>Sharer-side, monotonic permission reduction for the current session.</summary>
    public async Task RevokeControlAsync(
        string reasonCode = "owner_revoked",
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_role != SessionRole.Sharer)
            throw new InvalidOperationException("Only the sharing device can revoke remote control.");
        if (string.IsNullOrWhiteSpace(reasonCode)) throw new ArgumentException("A reason code is required.", nameof(reasonCode));

        if (Interlocked.Exchange(ref _permissionRevoked, 1) == 0)
            ControlPermissionChanged?.Invoke(this, new RemoteInputPermissionStatus(false, reasonCode));

        Interlocked.Exchange(ref _activeRemoteFocusGeneration, 0);
        _focusCoordinator?.Release(_sessionId);
        _sink!.DisableAndReleaseAll();
        Interlocked.Exchange(ref _revocationPending, 1);
        if (_transport.IsReady)
        {
            using var deliveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deliveryTimeout.CancelAfter(FocusAcknowledgmentTimeout);
            try
            {
                await SendPendingRevocationAsync(reasonCode, deliveryTimeout.Token)
                    .WaitAsync(deliveryTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                Warning?.Invoke(this, "input_revocation_delivery_pending");
            }
        }
    }

    public void ConnectionInterrupted()
    {
        if (Interlocked.Exchange(ref _connectionInterrupted, 1) == 0)
            Interlocked.Increment(ref _sessionGeneration);

        Interlocked.Exchange(ref _locallyEnabled, 0);
        Interlocked.Exchange(ref _activeRemoteFocusGeneration, 0);
        _focusCoordinator?.Release(_sessionId);
        Interlocked.Exchange(ref _protocolFailed, 0);
        lock (_pointerGate) _pendingPointer = null;
        ClearPendingMeasurements();
        CompletePendingFocus(accepted: false);
        _sink?.DisableAndReleaseAll();
    }

    public void ConnectionResumed()
    {
        // The sharer's coordinator restores only the effective approved scope. The viewer must
        // explicitly request and receive a fresh focus-generation acknowledgment.
        Interlocked.Exchange(ref _locallyEnabled, 0);
        Interlocked.Exchange(ref _connectionInterrupted, 0);
        if (_role == SessionRole.Sharer && Volatile.Read(ref _revocationPending) != 0 && _transport.IsReady)
            _ = SendPendingRevocationAsync("owner_revoked", CancellationToken.None);
    }

    private async Task DisableLocalCaptureAsync(CancellationToken cancellationToken)
    {
        var wasEnabled = Interlocked.Exchange(ref _locallyEnabled, 0) == 1;
        lock (_pointerGate) _pendingPointer = null;
        ClearPendingMeasurements();
        CompletePendingFocus(accepted: false);
        if (!wasEnabled || !_transport.IsReady) return;

        var focusGeneration = Volatile.Read(ref _focusGeneration);
        await SendMessageAsync(sequence => CreateReleaseAll(sequence, focusGeneration), cancellationToken);
    }

    private async Task PumpPointerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                RemoteInputEvent? input;
                lock (_pointerGate)
                {
                    input = _pendingPointer;
                    _pendingPointer = null;
                    if (input is null)
                    {
                        _pointerPumpRunning = false;
                        return;
                    }
                }

                if (!IsLocallyEnabled) continue;
                await WaitForPointerSendSlotAsync(cancellationToken);
                await SendInputAsync(input, cancellationToken);
            }
        }
        catch
        {
            lock (_pointerGate) _pointerPumpRunning = false;
            throw;
        }
    }

    private async Task WaitForPointerSendSlotAsync(CancellationToken cancellationToken)
    {
        var previous = Volatile.Read(ref _lastPointerSendTimestamp);
        if (previous > 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(previous);
            var remaining = MinimumPointerSendInterval - elapsed;
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken);
        }

        Volatile.Write(ref _lastPointerSendTimestamp, Stopwatch.GetTimestamp());
    }

    private async Task SendInputAsync(RemoteInputEvent input, CancellationToken cancellationToken)
    {
        EnsureCanSend(input);
        var focusGeneration = Volatile.Read(ref _focusGeneration);
        var sampledSequence = 0L;
        try
        {
            await SendMessageAsync(sequence =>
            {
                long? measurementSentAt = null;
                if (TryBeginMeasurement(sequence, out var sentAt))
                {
                    sampledSequence = sequence;
                    measurementSentAt = sentAt;
                }

                return new RemoteInputCommand
                {
                    InputVersion = CurrentInputProtocolVersion,
                    SessionId = _sessionId.Value,
                    SessionGeneration = Volatile.Read(ref _sessionGeneration),
                    FocusGeneration = focusGeneration,
                    Sequence = sequence,
                    Input = input,
                    MeasurementSentAtUnixMicroseconds = measurementSentAt,
                };
            }, cancellationToken);
        }
        catch
        {
            if (sampledSequence > 0) RemovePendingMeasurement(sampledSequence);
            throw;
        }
    }

    private async Task SendMessageAsync(
        Func<long, CollaborationMessage> createMessage,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _pendingSends) > MaximumPendingSends)
        {
            Interlocked.Decrement(ref _pendingSends);
            Interlocked.Exchange(ref _locallyEnabled, 0);
            throw new InvalidOperationException("The bounded remote-input send queue is full.");
        }

        try
        {
            await _transportSendGate.WaitAsync(cancellationToken);
            try
            {
                await _transport.SendAsync(createMessage(NextSequence()), cancellationToken);
            }
            finally
            {
                _transportSendGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSends);
        }
    }

    private void EnsureCanSend(RemoteInputEvent input)
    {
        ThrowIfDisposed();
        if (_role != SessionRole.Viewer || !IsLocallyEnabled)
            throw new InvalidOperationException("Local input capture is disabled.");
        if (!IsControlPermissionAvailable)
            throw new UnauthorizedAccessException("The remote owner revoked input control for this session.");
        if (!_transport.IsReady || Volatile.Read(ref _connectionInterrupted) != 0)
            throw new InvalidOperationException("The authorized input channel is not connected.");
        if (!input.IsValid())
            throw new ArgumentOutOfRangeException(nameof(input), "Remote input values are outside the accepted bounds.");
    }

    private void OnMessageReceived(object? sender, CollaborationMessage message)
    {
        if (message is not RemoteInputMessage inputMessage) return;
        if (!TryAcceptEnvelope(inputMessage, out var rejection))
        {
            FailClosed(rejection);
            return;
        }

        switch (inputMessage)
        {
            case RemoteInputFocusRequest request:
                if (_role != SessionRole.Sharer || _sink is null)
                {
                    FailClosed("input_direction_rejected");
                    return;
                }
                HandleFocusRequest(request);
                break;

            case RemoteInputFocusResult result:
                if (_role != SessionRole.Viewer)
                {
                    FailClosed("input_direction_rejected");
                    return;
                }
                HandleFocusResult(result);
                break;

            case RemoteInputFocusLost lost:
                if (_role != SessionRole.Viewer)
                {
                    FailClosed("input_direction_rejected");
                    return;
                }
                HandleFocusLost(lost);
                break;

            case RemoteInputPermissionRevoked revoked:
                if (_role != SessionRole.Viewer)
                {
                    FailClosed("input_direction_rejected");
                    return;
                }
                HandlePermissionRevoked(revoked);
                break;

            case RemoteInputAcknowledgement acknowledgment:
                if (_role != SessionRole.Viewer)
                {
                    Warning?.Invoke(this, "input_measurement_direction_rejected");
                    return;
                }
                HandleInputAcknowledgement(acknowledgment);
                break;

            case RemoteInputReleaseAll release:
                if (_role != SessionRole.Sharer || _sink is null)
                {
                    FailClosed("input_direction_rejected");
                    return;
                }
                if (release.FocusGeneration != Volatile.Read(ref _activeRemoteFocusGeneration))
                {
                    FailClosed("input_focus_generation_rejected");
                    return;
                }
                Interlocked.Exchange(ref _activeRemoteFocusGeneration, 0);
                _focusCoordinator?.Release(_sessionId);
                _sink.ReleaseAll();
                break;

            case RemoteInputCommand command:
                if (_role != SessionRole.Sharer || _sink is null)
                {
                    FailClosed("input_direction_rejected");
                    return;
                }
                if (Volatile.Read(ref _permissionRevoked) != 0
                    || Volatile.Read(ref _protocolFailed) != 0
                    || (_focusCoordinator is not null && !_focusCoordinator.IsOwner(_sessionId))
                    || command.FocusGeneration != Volatile.Read(ref _activeRemoteFocusGeneration))
                {
                    FailClosed("input_focus_generation_rejected");
                    return;
                }
                if (!command.Input.IsValid() || !_sink.TryInject(command.Input))
                {
                    FailClosed("input_command_rejected");
                    return;
                }
                if (_transport.IsInputAcknowledgementNegotiated
                    && command.MeasurementSentAtUnixMicroseconds is > 0)
                {
                    _ = SendInputAcknowledgementSafelyAsync(
                        command,
                        GetUnixMicroseconds(_time.GetUtcNow()));
                }
                break;
        }
    }

    private void HandleFocusRequest(RemoteInputFocusRequest request)
    {
        if (!request.Enabled)
        {
            Interlocked.Exchange(ref _activeRemoteFocusGeneration, 0);
            _focusCoordinator?.Release(_sessionId);
            _sink!.ReleaseAll();
            _ = SendFocusResultSafelyAsync(request.FocusGeneration, accepted: true, "released");
            return;
        }

        var accepted = Volatile.Read(ref _permissionRevoked) == 0
                       && Volatile.Read(ref _protocolFailed) == 0
                       && request.FocusGeneration > Volatile.Read(ref _lastRemoteFocusGeneration);
        if (accepted)
        {
            _focusCoordinator?.Acquire(_sessionId, LoseRemoteFocus);
            _sink!.RestoreApprovedScope(SessionPermission.ControlInput);
            Volatile.Write(ref _lastRemoteFocusGeneration, request.FocusGeneration);
            Volatile.Write(ref _activeRemoteFocusGeneration, request.FocusGeneration);
        }
        else
        {
            _sink!.DisableAndReleaseAll();
        }

        _ = SendFocusResultSafelyAsync(
            request.FocusGeneration,
            accepted,
            accepted ? "accepted" : Volatile.Read(ref _permissionRevoked) != 0 ? "permission_revoked" : "stale_focus");
    }

    private void HandleFocusResult(RemoteInputFocusResult result)
    {
        TaskCompletionSource<bool>? acknowledgment;
        lock (_stateGate)
        {
            acknowledgment = result.FocusGeneration == _pendingFocusGeneration
                ? _focusAcknowledgment
                : null;
        }

        if (acknowledgment is null)
        {
            FailClosed("input_focus_result_rejected");
            return;
        }

        acknowledgment.TrySetResult(result.Accepted);
        if (!result.Accepted)
            Warning?.Invoke(this, result.ReasonCode);
    }

    private void HandleFocusLost(RemoteInputFocusLost lost)
    {
        if (lost.FocusGeneration != Volatile.Read(ref _focusGeneration)) return;
        Interlocked.Exchange(ref _locallyEnabled, 0);
        lock (_pointerGate) _pendingPointer = null;
        ClearPendingMeasurements();
        CompletePendingFocus(accepted: false);
        Warning?.Invoke(this, lost.ReasonCode);
    }

    private void LoseRemoteFocus()
    {
        var focusGeneration = Interlocked.Exchange(ref _activeRemoteFocusGeneration, 0);
        _sink?.ReleaseAll();
        if (focusGeneration > 0 && _transport.IsReady)
            _ = SendFocusLostSafelyAsync(focusGeneration);
    }

    private async Task SendFocusLostSafelyAsync(long focusGeneration)
    {
        try
        {
            await SendMessageAsync(sequence => new RemoteInputFocusLost
            {
                InputVersion = CurrentInputProtocolVersion,
                SessionId = _sessionId.Value,
                SessionGeneration = Volatile.Read(ref _sessionGeneration),
                FocusGeneration = focusGeneration,
                Sequence = sequence,
                ReasonCode = "focus_transferred_to_another_session",
            }, CancellationToken.None);
        }
        catch (Exception)
        {
            FailClosed("input_focus_transfer_failed");
        }
    }

    private void HandlePermissionRevoked(RemoteInputPermissionRevoked revoked)
    {
        if (Interlocked.Exchange(ref _permissionRevoked, 1) == 0)
            ControlPermissionChanged?.Invoke(this, new RemoteInputPermissionStatus(false, revoked.ReasonCode));
        Interlocked.Exchange(ref _locallyEnabled, 0);
        lock (_pointerGate) _pendingPointer = null;
        ClearPendingMeasurements();
        CompletePendingFocus(accepted: false);
    }

    private void HandleInputAcknowledgement(RemoteInputAcknowledgement acknowledgment)
    {
        if (!_transport.IsInputAcknowledgementNegotiated
            || acknowledgment.FocusGeneration != Volatile.Read(ref _focusGeneration)
            || acknowledgment.AcknowledgedSequence <= 0
            || acknowledgment.ViewerSentAtUnixMicroseconds <= 0
            || acknowledgment.InjectedAtUnixMicroseconds <= 0)
        {
            return;
        }

        PendingInputMeasurement pending;
        lock (_measurementGate)
        {
            if (!_pendingMeasurements.Remove(acknowledgment.AcknowledgedSequence, out pending))
                return;
        }

        if (acknowledgment.ViewerSentAtUnixMicroseconds != pending.SentAtUnixMicroseconds)
        {
            Warning?.Invoke(this, "input_measurement_mismatch");
            return;
        }

        if (InputLatencyEstimator.TryCalculate(
                pending.SentAtUnixMicroseconds,
                acknowledgment.InjectedAtUnixMicroseconds,
                pending.ClockEstimate,
                out var measurement))
        {
            _transport.ReportInputLatency(measurement);
        }
    }

    private async Task SendInputAcknowledgementSafelyAsync(
        RemoteInputCommand command,
        long injectedAtUnixMicroseconds)
    {
        try
        {
            await SendMessageAsync(sequence => new RemoteInputAcknowledgement
            {
                InputVersion = CurrentInputProtocolVersion,
                SessionId = _sessionId.Value,
                SessionGeneration = Volatile.Read(ref _sessionGeneration),
                FocusGeneration = command.FocusGeneration,
                Sequence = sequence,
                AcknowledgedSequence = command.Sequence,
                ViewerSentAtUnixMicroseconds = command.MeasurementSentAtUnixMicroseconds!.Value,
                InjectedAtUnixMicroseconds = injectedAtUnixMicroseconds,
            }, CancellationToken.None);
        }
        catch (Exception)
        {
            Warning?.Invoke(this, "input_measurement_delivery_failed");
        }
    }

    private bool TryBeginMeasurement(long sequence, out long sentAtUnixMicroseconds)
    {
        sentAtUnixMicroseconds = 0;
        if (!_transport.IsInputAcknowledgementNegotiated
            || _transport.PeerClockEstimate is not { UncertaintyMicroseconds: >= 0 and <= 50_000 } estimate)
        {
            return false;
        }

        var nowTimestamp = _time.GetTimestamp();
        lock (_measurementGate)
        {
            foreach (var expired in _pendingMeasurements
                         .Where(entry => _time.GetElapsedTime(entry.Value.StartedTimestamp, nowTimestamp) > MeasurementTimeout)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _pendingMeasurements.Remove(expired);
            }

            if (_pendingMeasurements.Count >= MaximumPendingMeasurements
                || (_lastMeasurementTimestamp > 0
                    && _time.GetElapsedTime(_lastMeasurementTimestamp, nowTimestamp) < MinimumMeasurementInterval))
            {
                return false;
            }

            sentAtUnixMicroseconds = GetUnixMicroseconds(_time.GetUtcNow());
            if (sentAtUnixMicroseconds <= 0) return false;
            _pendingMeasurements.Add(
                sequence,
                new PendingInputMeasurement(sentAtUnixMicroseconds, nowTimestamp, estimate));
            _lastMeasurementTimestamp = nowTimestamp;
            return true;
        }
    }

    private void RemovePendingMeasurement(long sequence)
    {
        lock (_measurementGate) _pendingMeasurements.Remove(sequence);
    }

    private void ClearPendingMeasurements()
    {
        lock (_measurementGate)
        {
            _pendingMeasurements.Clear();
            _lastMeasurementTimestamp = 0;
        }
    }

    private static long GetUnixMicroseconds(DateTimeOffset timestamp) =>
        (timestamp.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    private bool TryAcceptEnvelope(RemoteInputMessage message, out string rejection)
    {
        rejection = string.Empty;
        if (message.InputVersion != CurrentInputProtocolVersion)
        {
            rejection = "input_protocol_version_rejected";
            return false;
        }
        if (message.SessionId != _sessionId.Value
            || message.SessionGeneration != Volatile.Read(ref _sessionGeneration))
        {
            rejection = "input_session_binding_rejected";
            return false;
        }
        if (message.Sequence <= 0)
        {
            rejection = "input_sequence_rejected";
            return false;
        }

        lock (_receiveGate)
        {
            var elapsed = Stopwatch.GetElapsedTime(_receiveWindowStarted);
            if (elapsed >= TimeSpan.FromSeconds(1))
            {
                _receiveWindowStarted = Stopwatch.GetTimestamp();
                _receivedInWindow = 0;
            }

            if (message.Sequence <= _lastReceivedSequence)
            {
                rejection = "input_sequence_rejected";
                return false;
            }
            if (++_receivedInWindow > MaximumReceivedCommandsPerSecond)
            {
                rejection = "input_rate_rejected";
                return false;
            }

            _lastReceivedSequence = message.Sequence;
        }

        return true;
    }

    private async Task SendFocusResultSafelyAsync(long focusGeneration, bool accepted, string reasonCode)
    {
        try
        {
            await SendMessageAsync(sequence => new RemoteInputFocusResult
            {
                InputVersion = CurrentInputProtocolVersion,
                SessionId = _sessionId.Value,
                SessionGeneration = Volatile.Read(ref _sessionGeneration),
                FocusGeneration = focusGeneration,
                Sequence = sequence,
                Accepted = accepted,
                ReasonCode = reasonCode,
            }, CancellationToken.None);
        }
        catch (Exception)
        {
            FailClosed("input_focus_acknowledgment_failed");
        }
    }

    private async Task SendPendingRevocationAsync(string reasonCode, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _revocationPending) == 0 || !_transport.IsReady) return;
        try
        {
            await SendMessageAsync(sequence => new RemoteInputPermissionRevoked
            {
                InputVersion = CurrentInputProtocolVersion,
                SessionId = _sessionId.Value,
                SessionGeneration = Volatile.Read(ref _sessionGeneration),
                FocusGeneration = Volatile.Read(ref _activeRemoteFocusGeneration),
                Sequence = sequence,
                ReasonCode = reasonCode,
            }, cancellationToken);
            Interlocked.Exchange(ref _revocationPending, 0);
        }
        catch (Exception)
        {
            Warning?.Invoke(this, "input_revocation_delivery_pending");
        }
    }

    private RemoteInputReleaseAll CreateReleaseAll(long sequence, long focusGeneration) => new()
    {
        InputVersion = CurrentInputProtocolVersion,
        SessionId = _sessionId.Value,
        SessionGeneration = Volatile.Read(ref _sessionGeneration),
        FocusGeneration = focusGeneration,
        Sequence = sequence,
    };

    private void FailClosed(string warningCode)
    {
        if (Interlocked.Exchange(ref _protocolFailed, 1) == 1) return;

        Interlocked.Exchange(ref _locallyEnabled, 0);
        Interlocked.Exchange(ref _activeRemoteFocusGeneration, 0);
        _focusCoordinator?.Release(_sessionId);
        lock (_pointerGate) _pendingPointer = null;
        ClearPendingMeasurements();
        CompletePendingFocus(accepted: false);
        _sink?.DisableAndReleaseAll();
        Warning?.Invoke(this, warningCode);
    }

    private void CompletePendingFocus(bool accepted)
    {
        TaskCompletionSource<bool>? acknowledgment;
        lock (_stateGate) acknowledgment = _focusAcknowledgment;
        acknowledgment?.TrySetResult(accepted);
    }

    private void OnProtocolError(object? sender, string reason) => FailClosed($"input_transport_{reason}");

    private void OnTransportReady(object? sender, EventArgs args)
    {
        if (_role == SessionRole.Sharer && Volatile.Read(ref _revocationPending) != 0)
            _ = SendPendingRevocationAsync("owner_revoked", CancellationToken.None);
    }

    private readonly record struct PendingInputMeasurement(
        long SentAtUnixMicroseconds,
        long StartedTimestamp,
        PeerClockEstimate ClockEstimate);

    private long NextSequence() => Interlocked.Increment(ref _sendSequence);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed == 1, this);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return ValueTask.CompletedTask;

        _transport.MessageReceived -= OnMessageReceived;
        _transport.ProtocolError -= OnProtocolError;
        _transport.Ready -= OnTransportReady;
        Interlocked.Exchange(ref _locallyEnabled, 0);
        lock (_pointerGate) _pendingPointer = null;
        ClearPendingMeasurements();
        CompletePendingFocus(accepted: false);
        _focusCoordinator?.Release(_sessionId);
        _sink?.DisableAndReleaseAll();
        return ValueTask.CompletedTask;
    }
}

internal static class InputLatencyEstimator
{
    private const long MaximumClockUncertaintyMicroseconds = 50_000;
    private const long MaximumLatencyMicroseconds = 60_000_000;

    public static bool TryCalculate(
        long viewerSentAtUnixMicroseconds,
        long remoteInjectedAtUnixMicroseconds,
        PeerClockEstimate clockEstimate,
        out InputLatencyMeasurement measurement)
    {
        measurement = default;
        if (viewerSentAtUnixMicroseconds <= 0
            || remoteInjectedAtUnixMicroseconds <= 0
            || clockEstimate.UncertaintyMicroseconds is < 0 or > MaximumClockUncertaintyMicroseconds)
        {
            return false;
        }

        var latencyMicroseconds = (decimal)remoteInjectedAtUnixMicroseconds
                                  - viewerSentAtUnixMicroseconds
                                  - clockEstimate.RemoteMinusLocalOffsetMicroseconds;
        if (latencyMicroseconds is <= 0 or > MaximumLatencyMicroseconds)
            return false;

        measurement = new InputLatencyMeasurement(
            (double)(latencyMicroseconds / 1_000m),
            clockEstimate.UncertaintyMicroseconds / 1_000d);
        return true;
    }
}
