using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.App.Linux;

public sealed partial class MainWindow : Window
{
    private LinuxAppServices? _services;
    private SessionId? _currentSession;
    private RemoteInputSession? _remoteInput;
    private WriteableBitmap? _remoteBitmap;
    private int _frameWidth;
    private int _frameHeight;
    private readonly object _pendingFrameGate = new();
    private PendingFrame? _pendingFrame;
    private bool _frameDispatchScheduled;
    private bool _changingInputToggle;
    private bool _sessionActionRunning;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Deactivated += OnWindowDeactivated;
        Closing += OnWindowClosing;
        Closed += (_, _) => _remoteBitmap?.Dispose();
    }

    public async Task AttachAsync(LinuxAppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        LocalIdText.Text = services.Identity.PublicId.ToString();
        services.Signaling.StateChanged += OnSignalingStateChanged;
        services.Coordinator.SessionChanged += OnSessionChanged;
        services.Coordinator.SessionClosed += OnSessionClosed;
        services.Coordinator.SessionRemoteFrameReceived += OnRemoteFrameReceived;
        services.Coordinator.CollaborationAvailable += OnCollaborationAvailable;

        SetSessionStatus("Secure identity is ready. Connecting to the signaling server…", busy: true);
        await TryConnectSignalingAsync();
    }

    public void ShowStartupError(Exception exception)
    {
        LocalIdText.Text = "Identity unavailable";
        SetConnectionState(SignalingConnectionState.Faulted);
        SetSessionStatus("The Linux client could not initialize.", busy: false);
        ShowError(ToUserMessage(exception));
        RetryServerButton.IsVisible = false;
    }

    private async Task TryConnectSignalingAsync()
    {
        if (_services is null) return;
        ClearError();
        RetryServerButton.IsVisible = false;
        try
        {
            await _services.ConnectAsync();
            SetSessionStatus("Ready. Enter the PeerOnQ ID of a Windows device.", busy: false);
        }
        catch (Exception exception)
        {
            SetSessionStatus("The signaling server is unavailable.", busy: false);
            ShowError(ToUserMessage(exception));
            RetryServerButton.IsVisible = true;
        }

        UpdateActions();
    }

    private async void OnRetryServerClick(object? sender, RoutedEventArgs e) =>
        await TryConnectSignalingAsync();

    private async void OnStartSessionClick(object? sender, RoutedEventArgs e)
    {
        if (_services is null || _sessionActionRunning) return;
        if (!PeerOnQId.TryParse(RemoteIdTextBox.Text, out var target))
        {
            ShowError("Enter a valid PeerOnQ ID, for example LNK-000-000-000-000.");
            RemoteIdTextBox.Focus();
            return;
        }

        if (target == _services.Identity.PublicId)
        {
            ShowError("Choose another device; this is the local PeerOnQ ID.");
            return;
        }

        ClearError();
        _sessionActionRunning = true;
        UpdateActions();
        SetSessionStatus("Sending an attended-session request…", busy: true);
        try
        {
            var fullControl = SessionModeComboBox.SelectedIndex == 1;
            var mode = fullControl ? SessionMode.FullControl : SessionMode.ViewOnly;
            var permissions = fullControl
                ? SessionPermission.ViewScreen | SessionPermission.ControlInput
                : SessionPermission.ViewScreen;
            _currentSession = await _services.Coordinator.RequestSessionAsync(
                target,
                mode,
                permissions,
                SessionAccessKind.Attended);
        }
        catch (Exception exception)
        {
            _currentSession = null;
            SetSessionStatus("The session request did not start.", busy: false);
            ShowError(ToUserMessage(exception));
        }
        finally
        {
            _sessionActionRunning = false;
            UpdateActions();
        }
    }

    private async void OnEndSessionClick(object? sender, RoutedEventArgs e)
    {
        if (_services is null || _currentSession is not { } sessionId || _sessionActionRunning) return;
        _sessionActionRunning = true;
        UpdateActions();
        await DisableRemoteInputAsync();
        try
        {
            await _services.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedByViewer);
        }
        catch (Exception exception)
        {
            ShowError(ToUserMessage(exception));
        }
        finally
        {
            _sessionActionRunning = false;
            UpdateActions();
        }
    }

    private void OnSignalingStateChanged(object? sender, SignalingConnectionState state) =>
        Dispatcher.UIThread.Post(() =>
        {
            SetConnectionState(state);
            RetryServerButton.IsVisible = state is SignalingConnectionState.Disconnected or SignalingConnectionState.Faulted;
            UpdateActions();
        });

    private void OnSessionChanged(object? sender, ActiveSessionInfo session) =>
        Dispatcher.UIThread.Post(() => ApplySessionState(session));

    private void ApplySessionState(ActiveSessionInfo session)
    {
        if (session.Role != SessionRole.Viewer) return;
        if (_currentSession is null)
            _currentSession = session.SessionId;
        if (_currentSession != session.SessionId) return;

        var description = session.State switch
        {
            SessionState.ResolvingDevice => "Finding the remote device…",
            SessionState.RequestingPermission => "Sending the approval request…",
            SessionState.AwaitingPermission => "Waiting for the Windows owner to approve…",
            SessionState.Negotiating => "Negotiating an encrypted peer-to-peer session…",
            SessionState.Connecting => "Connecting the remote video stream…",
            SessionState.ConnectedViewOnly => $"Connected to {session.PeerDisplayName}.",
            SessionState.Reconnecting => "Connection interrupted; reconnecting…",
            SessionState.Ending => "Ending the session…",
            SessionState.Failed => "The remote session failed.",
            SessionState.Ended => "The remote session ended.",
            _ => "Preparing the remote session…",
        };
        var busy = session.State is >= SessionState.ResolvingDevice and <= SessionState.Connecting
                   || session.State is SessionState.Reconnecting or SessionState.Ending;
        SetSessionStatus(description, busy);

        if (session.State == SessionState.ConnectedViewOnly && _services is not null)
        {
            var collaboration = _services.Coordinator.CollaborationFor(session.SessionId);
            if (collaboration is not null)
                BindCollaboration(collaboration);
        }

        UpdateActions();
    }

    private void OnSessionClosed(object? sender, (SessionId Id, SessionEndReason Reason) closed) =>
        Dispatcher.UIThread.Post(async () =>
        {
            if (_currentSession != closed.Id) return;
            await DisableRemoteInputAsync();
            _remoteInput = null;
            _currentSession = null;
            RemoteInputToggle.IsEnabled = false;
            SetSessionStatus($"Session ended ({FormatEndReason(closed.Reason)}).", busy: false);
            UpdateActions();
        });

    private void OnCollaborationAvailable(object? sender, SessionCollaborationContext collaboration) =>
        Dispatcher.UIThread.Post(() => BindCollaboration(collaboration));

    private void BindCollaboration(SessionCollaborationContext collaboration)
    {
        if (_currentSession != collaboration.SessionId || collaboration.RemoteInput is null) return;
        if (ReferenceEquals(_remoteInput, collaboration.RemoteInput)) return;

        _remoteInput = collaboration.RemoteInput;
        _remoteInput.Warning += (_, warning) => Dispatcher.UIThread.Post(() => ShowError(
            warning == "input_revocation_delivery_pending"
                ? "Remote-input revocation is pending; local forwarding is already off."
                : "Remote input was interrupted."));
        _remoteInput.ControlPermissionChanged += (_, status) => Dispatcher.UIThread.Post(async () =>
        {
            if (!status.IsAvailable)
            {
                await DisableRemoteInputAsync();
                ShowError("The Windows owner revoked remote-control permission.");
            }
            UpdateActions();
        });
        UpdateActions();
    }

    private void OnRemoteFrameReceived(object? sender, SessionRemoteFrame sessionFrame)
    {
        if (_currentSession != sessionFrame.SessionId) return;
        if (!FramePixelConverter.TryConvertToBgra(sessionFrame.Frame, out var pixels)) return;
        lock (_pendingFrameGate)
        {
            _pendingFrame = new PendingFrame(
                sessionFrame.SessionId,
                sessionFrame.Frame.Width,
                sessionFrame.Frame.Height,
                pixels);
            if (_frameDispatchScheduled) return;
            _frameDispatchScheduled = true;
        }
        Dispatcher.UIThread.Post(RenderPendingFrame);
    }

    private void RenderPendingFrame()
    {
        PendingFrame? pending;
        lock (_pendingFrameGate)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
            _frameDispatchScheduled = false;
        }
        if (pending is not null)
            RenderFrame(pending.SessionId, pending.Width, pending.Height, pending.Pixels);
    }

    private void RenderFrame(SessionId sessionId, int width, int height, byte[] pixels)
    {
        if (_currentSession != sessionId || _services is null) return;
        if (_remoteBitmap is null || _frameWidth != width || _frameHeight != height)
        {
            _remoteBitmap?.Dispose();
            _remoteBitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormats.Bgra8888,
                AlphaFormat.Opaque);
            _frameWidth = width;
            _frameHeight = height;
            RemoteImage.Source = _remoteBitmap;
        }

        using (var frameBuffer = _remoteBitmap.Lock())
        {
            var sourceRowBytes = width * 4;
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    pixels,
                    row * sourceRowBytes,
                    IntPtr.Add(frameBuffer.Address, row * frameBuffer.RowBytes),
                    sourceRowBytes);
            }
        }

        RemoteImage.IsVisible = true;
        EmptyViewportContent.IsVisible = false;
        _services.Coordinator.ReportFrameRendered(sessionId);
    }

    private async void OnRemoteInputChanged(object? sender, RoutedEventArgs e)
    {
        if (_changingInputToggle) return;
        if (RemoteInputToggle.IsChecked != true)
        {
            await DisableRemoteInputAsync();
            return;
        }

        if (_remoteInput is null)
        {
            SetInputToggle(false);
            return;
        }

        try
        {
            ClearError();
            await _remoteInput.SetLocalCaptureEnabledAsync(true);
            InputActiveBadge.IsVisible = true;
            RemoteViewport.Focus();
        }
        catch (Exception exception)
        {
            ShowError(ToUserMessage(exception));
            SetInputToggle(false);
        }
    }

    private async Task DisableRemoteInputAsync()
    {
        InputActiveBadge.IsVisible = false;
        if (_remoteInput is { IsLocallyEnabled: true } input)
        {
            try
            {
                await input.SetLocalCaptureEnabledAsync(false);
            }
            catch
            {
                // Local forwarding is disabled before the best-effort ReleaseAll message is sent.
            }
        }
        SetInputToggle(false);
    }

    private async void OnWindowDeactivated(object? sender, EventArgs e) =>
        await DisableRemoteInputAsync();

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        await DisableRemoteInputAsync();
        try
        {
            if (_services is not null)
                await _services.DisposeAsync();
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void OnRemotePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!TryGetInputPosition(e, allowOutside: false, out var input, out var x, out var y)) return;
        RunInput(() => input.SendPointerMoveAsync(x, y));
    }

    private void OnRemotePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!TryGetInputPosition(e, allowOutside: false, out var input, out var x, out var y)) return;
        var button = ToRemoteButton(e.GetCurrentPoint(RemoteViewport).Properties.PointerUpdateKind);
        if (button == RemotePointerButton.None) return;
        RemoteViewport.Focus();
        e.Pointer.Capture(RemoteViewport);
        e.Handled = true;
        RunInput(() => input.SendPointerButtonAsync(x, y, button, isPressed: true));
    }

    private void OnRemotePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!TryGetInputPosition(e, allowOutside: true, out var input, out var x, out var y)) return;
        var button = ToRemoteButton(e.GetCurrentPoint(RemoteViewport).Properties.PointerUpdateKind);
        if (button == RemotePointerButton.None) return;
        e.Pointer.Capture(null);
        e.Handled = true;
        RunInput(() => input.SendPointerButtonAsync(x, y, button, isPressed: false));
    }

    private void OnRemotePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!TryGetInputPosition(e, allowOutside: false, out var input, out var x, out var y)) return;
        var vertical = (int)Math.Clamp(Math.Round(e.Delta.Y * 120), -1200, 1200);
        var horizontal = (int)Math.Clamp(Math.Round(e.Delta.X * 120), -1200, 1200);
        if (vertical != 0)
            RunInput(() => input.SendPointerWheelAsync(x, y, vertical));
        if (horizontal != 0)
            RunInput(() => input.SendPointerWheelAsync(x, y, horizontal, isHorizontal: true));
        e.Handled = vertical != 0 || horizontal != 0;
    }

    private void OnRemoteKeyDown(object? sender, KeyEventArgs e)
    {
        const KeyModifiers releaseModifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift;
        if (e.Key == Key.Escape
            && (e.KeyModifiers & releaseModifiers) == releaseModifiers)
        {
            _ = DisableRemoteInputAsync();
            e.Handled = true;
            return;
        }

        SendKey(e, isPressed: true);
    }

    private void OnRemoteKeyUp(object? sender, KeyEventArgs e) => SendKey(e, isPressed: false);

    private void SendKey(KeyEventArgs e, bool isPressed)
    {
        if (_remoteInput is not { IsLocallyEnabled: true } input
            || !WindowsVirtualKeyMapper.TryMap(e.Key, out var virtualKey, out var extended))
        {
            return;
        }

        e.Handled = true;
        RunInput(() => input.SendKeyAsync(virtualKey, isPressed, extended));
    }

    private bool TryGetInputPosition(
        PointerEventArgs e,
        bool allowOutside,
        out RemoteInputSession input,
        out double normalizedX,
        out double normalizedY)
    {
        input = null!;
        normalizedX = normalizedY = 0;
        if (_remoteInput is not { IsLocallyEnabled: true } activeInput
            || _frameWidth <= 0 || _frameHeight <= 0)
        {
            return false;
        }

        var bounds = RemoteViewport.Bounds;
        var scale = Math.Min(bounds.Width / _frameWidth, bounds.Height / _frameHeight);
        var renderedWidth = _frameWidth * scale;
        var renderedHeight = _frameHeight * scale;
        var offsetX = (bounds.Width - renderedWidth) / 2;
        var offsetY = (bounds.Height - renderedHeight) / 2;
        var position = e.GetPosition(RemoteViewport);
        if (!allowOutside && (position.X < offsetX || position.X > offsetX + renderedWidth
            || position.Y < offsetY || position.Y > offsetY + renderedHeight)
        )
            return false;

        input = activeInput;
        normalizedX = Math.Clamp((position.X - offsetX) / renderedWidth, 0, 1);
        normalizedY = Math.Clamp((position.Y - offsetY) / renderedHeight, 0, 1);
        return true;
    }

    private static RemotePointerButton ToRemoteButton(PointerUpdateKind updateKind) => updateKind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => RemotePointerButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => RemotePointerButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => RemotePointerButton.Middle,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => RemotePointerButton.X1,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => RemotePointerButton.X2,
        _ => RemotePointerButton.None,
    };

    private void RunInput(Func<Task> send) => _ = RunInputCoreAsync(send);

    private async Task RunInputCoreAsync(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowError(ToUserMessage(exception)));
        }
    }

    private void SetConnectionState(SignalingConnectionState state)
    {
        ConnectionStatusText.Text = state switch
        {
            SignalingConnectionState.Registered => "Server connected",
            SignalingConnectionState.Connecting => "Connecting",
            SignalingConnectionState.Registering => "Authenticating",
            SignalingConnectionState.Reconnecting => "Reconnecting",
            SignalingConnectionState.Faulted => "Connection failed",
            _ => "Disconnected",
        };
        ConnectionDot.Fill = state == SignalingConnectionState.Registered
            ? (IBrush)Avalonia.Application.Current!.FindResource("PeerOnQAccentBrush")!
            : (IBrush)Avalonia.Application.Current!.FindResource("PeerOnQMutedTextBrush")!;
    }

    private void UpdateActions()
    {
        var registered = _services?.Signaling.State == SignalingConnectionState.Registered;
        StartSessionButton.IsEnabled = registered && _currentSession is null && !_sessionActionRunning;
        EndSessionButton.IsEnabled = _currentSession is not null && !_sessionActionRunning;
        RemoteInputToggle.IsEnabled = _currentSession is not null
                                             && _remoteInput is { IsControlPermissionAvailable: true }
                                             && !_sessionActionRunning;
    }

    private void SetSessionStatus(string text, bool busy)
    {
        SessionStatusText.Text = text;
        ActivityProgress.IsVisible = busy;
    }

    private void SetInputToggle(bool value)
    {
        _changingInputToggle = true;
        RemoteInputToggle.IsChecked = value;
        _changingInputToggle = false;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.IsVisible = false;
    }

    private static string ToUserMessage(Exception exception) => exception switch
    {
        PlatformNotSupportedException => exception.Message,
        UnauthorizedAccessException => "The requested permission is no longer available.",
        OperationCanceledException => "The operation timed out or was cancelled.",
        _ => "The operation could not be completed. Check the server connection and try again.",
    };

    private static string FormatEndReason(SessionEndReason reason) => reason switch
    {
        SessionEndReason.EndedByViewer => "ended here",
        SessionEndReason.EndedBySharer => "ended by owner",
        SessionEndReason.PermissionDeclined => "request declined",
        SessionEndReason.PermissionBlocked => "device blocked",
        SessionEndReason.PermissionTimeout => "approval timed out",
        SessionEndReason.DeviceOffline => "device offline",
        SessionEndReason.SignalingLost => "server connection lost",
        SessionEndReason.MediaLost => "media connection lost",
        SessionEndReason.AuthenticationMismatch => "security verification failed",
        _ => reason.ToString(),
    };

    private sealed record PendingFrame(SessionId SessionId, int Width, int Height, byte[] Pixels);
}
