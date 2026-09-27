using CoreGraphics;
using Foundation;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Platform.Apple;
using UIKit;

namespace PeerOnQ.App.Apple;

public sealed class MainViewController : UIViewController
{
    private AppleAppServices? _services;
    private SessionId? _currentSession;
    private RemoteInputSession? _remoteInput;
    private readonly SemaphoreSlim _suspendGate = new(1, 1);
    private readonly Lock _pendingFrameGate = new();
    private PendingFrame? _pendingFrame;
    private bool _frameDispatchScheduled;
    private bool _sessionActionRunning;
    private bool _changingInputSwitch;
    private bool _formattingRemoteId;
    private bool _touchPressed;
    private bool _shutdown;
    private int _videoWidth;
    private int _videoHeight;

    private UIStackView _rootStack = null!;
    private UIScrollView _controlsScroll = null!;
    private UIView _viewport = null!;
    private UIImageView _remoteImage = null!;
    private UILabel _emptyViewport = null!;
    private UILabel _localId = null!;
    private UITextField _remoteId = null!;
    private UISegmentedControl _sessionMode = null!;
    private UIButton _connectButton = null!;
    private UIButton _endButton = null!;
    private UIButton _retryButton = null!;
    private UIActivityIndicatorView _progress = null!;
    private UILabel _status = null!;
    private UILabel _error = null!;
    private UISwitch _inputSwitch = null!;
    private UITextField _textInput = null!;
    private UIButton _sendTextButton = null!;
    private NSLayoutConstraint _portraitControlsHeight = null!;
    private NSLayoutConstraint _wideControlsWidth = null!;

    public override void LoadView()
    {
        View = new UIView { BackgroundColor = AppleTheme.Background };
        BuildInterface();
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        SetStatus("Preparing the protected Apple device identity…", busy: true);
        UpdateActions();
        _ = InitializeAsync();
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        ApplyAdaptiveLayout(View.Bounds.Size);
    }

    public override void TraitCollectionDidChange(UITraitCollection? previousTraitCollection)
    {
        base.TraitCollectionDidChange(previousTraitCollection);
        _controlsScroll.Layer.BorderColor = AppleTheme.Border.GetResolvedColor(TraitCollection).CGColor;
    }

    public override void ViewWillDisappear(bool animated)
    {
        _ = SuspendAsync();
        base.ViewWillDisappear(animated);
    }

    public async Task SuspendAsync()
    {
        await _suspendGate.WaitAsync();
        try
        {
            await DisableRemoteInputAsync();
            _remoteImage.Hidden = true;
            if (_services is not null && _currentSession is { } sessionId)
            {
                try
                {
                    await _services.Coordinator.EndSessionAsync(sessionId, SessionEndReason.EndedByViewer);
                }
                catch
                {
                    // Local control is already disabled. Session disposal remains fail-closed.
                }
            }
        }
        finally
        {
            _suspendGate.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;
        await SuspendAsync();
        DetachServices();
        if (_services is not null) await _services.DisposeAsync();
        _services = null;
    }

    private void BuildInterface()
    {
        _rootStack = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 16,
            LayoutMarginsRelativeArrangement = true,
            DirectionalLayoutMargins = new NSDirectionalEdgeInsets(16, 16, 16, 16),
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        View.AddSubview(_rootStack);
        NSLayoutConstraint.ActivateConstraints(
        [
            _rootStack.TopAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TopAnchor),
            _rootStack.LeadingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.LeadingAnchor),
            _rootStack.TrailingAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.TrailingAnchor),
            _rootStack.BottomAnchor.ConstraintEqualTo(View.SafeAreaLayoutGuide.BottomAnchor),
        ]);

        _controlsScroll = new UIScrollView
        {
            BackgroundColor = AppleTheme.Surface,
            KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive,
            DirectionalLayoutMargins = new NSDirectionalEdgeInsets(20, 20, 20, 20),
        };
        _controlsScroll.Layer.CornerRadius = 12;
        _controlsScroll.Layer.BorderColor = AppleTheme.Border.CGColor;
        _controlsScroll.Layer.BorderWidth = 1;

        var controls = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _controlsScroll.AddSubview(controls);
        NSLayoutConstraint.ActivateConstraints(
        [
            controls.TopAnchor.ConstraintEqualTo(_controlsScroll.ContentLayoutGuide.TopAnchor, 20),
            controls.LeadingAnchor.ConstraintEqualTo(_controlsScroll.ContentLayoutGuide.LeadingAnchor, 20),
            controls.TrailingAnchor.ConstraintEqualTo(_controlsScroll.ContentLayoutGuide.TrailingAnchor, -20),
            controls.BottomAnchor.ConstraintEqualTo(_controlsScroll.ContentLayoutGuide.BottomAnchor, -20),
            controls.WidthAnchor.ConstraintEqualTo(_controlsScroll.FrameLayoutGuide.WidthAnchor, -40),
        ]);

        var title = CreateLabel("PeerOnQ", UIFont.PreferredTitle1, AppleTheme.Text);
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        var subtitle = CreateLabel(
            "Secure attended remote viewing for iPhone and Mac.",
            UIFont.PreferredSubheadline,
            AppleTheme.MutedText);
        controls.AddArrangedSubview(title);
        controls.AddArrangedSubview(subtitle);
        controls.SetCustomSpacing(20, subtitle);

        _localId = CreateLabel("Identity unavailable", UIFont.PreferredHeadline, AppleTheme.Text);
        _localId.AccessibilityLabel = "This device PeerOnQ ID";
        controls.AddArrangedSubview(CreateFieldGroup("This device", _localId));

        _remoteId = CreateTextField("000-000-000-000", "Remote PeerOnQ ID");
        _remoteId.KeyboardType = UIKeyboardType.NumberPad;
        _remoteId.EditingChanged += (_, _) => FormatRemoteId();
        controls.AddArrangedSubview(CreateFieldGroup("Remote device ID", _remoteId));

        _sessionMode = new UISegmentedControl(["View only", "Full control"])
        {
            SelectedSegment = 0,
            AccessibilityLabel = "Requested session mode",
        };
        _sessionMode.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        controls.AddArrangedSubview(CreateFieldGroup("Permission request", _sessionMode));

        _connectButton = CreateButton("Request attended session", primary: true);
        _connectButton.TouchUpInside += async (_, _) => await StartSessionAsync();
        _endButton = CreateButton("End session", primary: false);
        _endButton.TouchUpInside += async (_, _) => await EndSessionAsync();
        var actionRow = new UIStackView([_connectButton, _endButton])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.FillEqually,
            Spacing = 8,
        };
        controls.AddArrangedSubview(actionRow);

        _progress = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium)
        {
            HidesWhenStopped = true,
            Color = AppleTheme.Primary,
        };
        _status = CreateLabel(string.Empty, UIFont.PreferredSubheadline, AppleTheme.Text);
        _status.AccessibilityTraits = UIAccessibilityTrait.UpdatesFrequently;
        var statusRow = new UIStackView([_progress, _status])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 8,
        };
        controls.AddArrangedSubview(statusRow);

        _retryButton = CreateButton("Retry server connection", primary: false);
        _retryButton.TouchUpInside += async (_, _) => await TryConnectSignalingAsync();
        _retryButton.Hidden = true;
        controls.AddArrangedSubview(_retryButton);

        _error = CreateLabel(string.Empty, UIFont.PreferredSubheadline, AppleTheme.Destructive);
        _error.AccessibilityTraits = UIAccessibilityTrait.StaticText;
        _error.Hidden = true;
        controls.AddArrangedSubview(_error);

        var inputLabel = CreateLabel("Enable remote input", UIFont.PreferredBody, AppleTheme.Text);
        _inputSwitch = new UISwitch
        {
            OnTintColor = AppleTheme.Primary,
            AccessibilityLabel = "Enable remote input forwarding",
        };
        _inputSwitch.ValueChanged += async (_, _) => await OnInputSwitchChangedAsync();
        var inputRow = new UIStackView([inputLabel, _inputSwitch])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.EqualSpacing,
            Spacing = 12,
        };
        controls.AddArrangedSubview(inputRow);

        _textInput = CreateTextField("Short ASCII text", "Text to type on the remote device");
        _textInput.AutocorrectionType = UITextAutocorrectionType.No;
        _textInput.SpellCheckingType = UITextSpellCheckingType.No;
        _textInput.EditingChanged += (_, _) => UpdateActions();
        _textInput.ShouldReturn = _ =>
        {
            _ = SendTextAsync();
            return true;
        };
        _sendTextButton = CreateButton("Send text", primary: false);
        _sendTextButton.TouchUpInside += async (_, _) => await SendTextAsync();
        controls.AddArrangedSubview(CreateFieldGroup("Remote keyboard", _textInput));
        controls.AddArrangedSubview(_sendTextButton);

        var safety = CreateLabel(
            "Remote input starts only after the other device approves Full Control and you enable this switch. Leaving the app ends the session.",
            UIFont.PreferredFootnote,
            AppleTheme.MutedText);
        controls.AddArrangedSubview(safety);

        BuildViewport();
        _rootStack.AddArrangedSubview(_controlsScroll);
        _rootStack.AddArrangedSubview(_viewport);
        _portraitControlsHeight = _controlsScroll.HeightAnchor.ConstraintEqualTo(_rootStack.HeightAnchor, 0.52f);
        _wideControlsWidth = _controlsScroll.WidthAnchor.ConstraintEqualTo(_rootStack.WidthAnchor, 0.38f);
    }

    private void BuildViewport()
    {
        _viewport = new UIView
        {
            BackgroundColor = AppleTheme.Viewport,
            AccessibilityLabel = "Remote screen",
            AccessibilityHint = "When remote input is enabled, drag here to move and press the remote pointer.",
            AccessibilityTraits = UIAccessibilityTrait.AllowsDirectInteraction,
        };
        _viewport.Layer.CornerRadius = 12;
        _viewport.ClipsToBounds = true;
        _remoteImage = new UIImageView
        {
            ContentMode = UIViewContentMode.ScaleAspectFit,
            BackgroundColor = AppleTheme.Viewport,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _emptyViewport = CreateLabel(
            "Remote screen appears after the device owner approves.",
            UIFont.PreferredBody,
            UIColor.White.ColorWithAlpha(0.72f));
        _emptyViewport.TextAlignment = UITextAlignment.Center;
        _emptyViewport.TranslatesAutoresizingMaskIntoConstraints = false;
        _viewport.AddSubviews(_remoteImage, _emptyViewport);
        NSLayoutConstraint.ActivateConstraints(
        [
            _remoteImage.TopAnchor.ConstraintEqualTo(_viewport.TopAnchor),
            _remoteImage.LeadingAnchor.ConstraintEqualTo(_viewport.LeadingAnchor),
            _remoteImage.TrailingAnchor.ConstraintEqualTo(_viewport.TrailingAnchor),
            _remoteImage.BottomAnchor.ConstraintEqualTo(_viewport.BottomAnchor),
            _emptyViewport.CenterXAnchor.ConstraintEqualTo(_viewport.CenterXAnchor),
            _emptyViewport.CenterYAnchor.ConstraintEqualTo(_viewport.CenterYAnchor),
            _emptyViewport.LeadingAnchor.ConstraintGreaterThanOrEqualTo(_viewport.LeadingAnchor, 24),
            _emptyViewport.TrailingAnchor.ConstraintLessThanOrEqualTo(_viewport.TrailingAnchor, -24),
            _viewport.HeightAnchor.ConstraintGreaterThanOrEqualTo(220),
        ]);

        var pointer = new UILongPressGestureRecognizer(OnRemotePointerGesture)
        {
            MinimumPressDuration = 0,
            CancelsTouchesInView = true,
        };
        _viewport.AddGestureRecognizer(pointer);
    }

    private async Task InitializeAsync()
    {
        try
        {
            _services = await AppleAppServices.CreateAsync();
            _localId.Text = _services.Identity.PublicId.Display;
            _services.Signaling.StateChanged += OnSignalingStateChanged;
            _services.Coordinator.SessionChanged += OnSessionChanged;
            _services.Coordinator.SessionClosed += OnSessionClosed;
            _services.Coordinator.SessionRemoteFrameReceived += OnRemoteFrameReceived;
            _services.Coordinator.CollaborationAvailable += OnCollaborationAvailable;
            await TryConnectSignalingAsync();
        }
        catch (Exception exception)
        {
            SetStatus("The Apple viewer could not initialize.", busy: false);
            ShowError(ToUserMessage(exception));
            _retryButton.Hidden = true;
            UpdateActions();
        }
    }

    private async Task TryConnectSignalingAsync()
    {
        if (_services is null) return;
        ClearError();
        _retryButton.Hidden = true;
        SetStatus("Connecting to the signaling server…", busy: true);
        try
        {
            await _services.ConnectAsync();
            SetStatus("Ready. Enter the device ID you want to view.", busy: false);
        }
        catch (Exception exception)
        {
            SetStatus("The signaling server is unavailable.", busy: false);
            ShowError(ToUserMessage(exception));
            _retryButton.Hidden = false;
        }
        UpdateActions();
    }

    private async Task StartSessionAsync()
    {
        if (_services is null || _sessionActionRunning) return;
        if (!PeerOnQId.TryParse(_remoteId.Text, out var target))
        {
            ShowError("Enter a valid 12-digit PeerOnQ ID, for example 000-000-000-000.");
            _remoteId.BecomeFirstResponder();
            return;
        }
        if (target == _services.Identity.PublicId)
        {
            ShowError("Choose another device; this is the current Apple device ID.");
            return;
        }

        ClearError();
        _sessionActionRunning = true;
        UpdateActions();
        SetStatus("Sending an attended-session request…", busy: true);
        try
        {
            var fullControl = _sessionMode.SelectedSegment == 1;
            _currentSession = await _services.Coordinator.RequestSessionAsync(
                target,
                fullControl ? SessionMode.FullControl : SessionMode.ViewOnly,
                fullControl
                    ? SessionPermission.ViewScreen | SessionPermission.ControlInput
                    : SessionPermission.ViewScreen,
                SessionAccessKind.Attended);
        }
        catch (Exception exception)
        {
            _currentSession = null;
            SetStatus("The session request did not start.", busy: false);
            ShowError(ToUserMessage(exception));
        }
        finally
        {
            _sessionActionRunning = false;
            UpdateActions();
        }
    }

    private async Task EndSessionAsync()
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
        InvokeOnMainThread(() =>
        {
            if (state is SignalingConnectionState.Disconnected or SignalingConnectionState.Faulted)
                _retryButton.Hidden = false;
            UpdateActions();
        });

    private void OnSessionChanged(object? sender, ActiveSessionInfo session) =>
        InvokeOnMainThread(() => ApplySessionState(session));

    private void ApplySessionState(ActiveSessionInfo session)
    {
        if (session.Role != SessionRole.Viewer) return;
        _currentSession ??= session.SessionId;
        if (_currentSession != session.SessionId) return;

        var message = session.State switch
        {
            SessionState.ResolvingDevice => "Finding the remote device…",
            SessionState.RequestingPermission => "Sending the approval request…",
            SessionState.AwaitingPermission => "Waiting for the remote owner to approve…",
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
        SetStatus(message, busy);

        if (session.State == SessionState.ConnectedViewOnly && _services is not null)
        {
            var collaboration = _services.Coordinator.CollaborationFor(session.SessionId);
            if (collaboration is not null) BindCollaboration(collaboration);
        }
        UpdateActions();
    }

    private void OnSessionClosed(object? sender, (SessionId Id, SessionEndReason Reason) closed) =>
        InvokeOnMainThread(async () =>
        {
            if (_currentSession != closed.Id) return;
            await DisableRemoteInputAsync();
            _remoteInput = null;
            _currentSession = null;
            _videoWidth = _videoHeight = 0;
            _remoteImage.Hidden = true;
            _emptyViewport.Hidden = false;
            SetStatus($"Session ended ({FormatEndReason(closed.Reason)}).", busy: false);
            UpdateActions();
        });

    private void OnCollaborationAvailable(object? sender, SessionCollaborationContext collaboration) =>
        InvokeOnMainThread(() => BindCollaboration(collaboration));

    private void BindCollaboration(SessionCollaborationContext collaboration)
    {
        if (_currentSession != collaboration.SessionId || collaboration.RemoteInput is null) return;
        if (ReferenceEquals(_remoteInput, collaboration.RemoteInput)) return;

        _remoteInput = collaboration.RemoteInput;
        _remoteInput.Warning += (_, _) => InvokeOnMainThread(() => ShowError("Remote input was interrupted."));
        _remoteInput.ControlPermissionChanged += (_, status) => InvokeOnMainThread(async () =>
        {
            if (!status.IsAvailable)
            {
                await DisableRemoteInputAsync();
                ShowError("The remote owner revoked input-control permission.");
            }
            UpdateActions();
        });
        UpdateActions();
    }

    private void OnRemoteFrameReceived(object? sender, SessionRemoteFrame sessionFrame)
    {
        if (_currentSession != sessionFrame.SessionId
            || !AppleFramePixelConverter.TryConvertToBgra(sessionFrame.Frame, out var pixels))
        {
            return;
        }

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
        InvokeOnMainThread(RenderPendingFrame);
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
        if (pending is null || _currentSession != pending.SessionId || _services is null) return;

        using var colorSpace = CGColorSpace.CreateDeviceRGB();
        using var context = new CGBitmapContext(
            pending.Pixels,
            pending.Width,
            pending.Height,
            8,
            pending.Width * 4,
            colorSpace,
            CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.PremultipliedFirst);
        using var image = context.ToImage();
        if (image is null) return;

        var next = new UIImage(image);
        var previous = _remoteImage.Image;
        _remoteImage.Image = next;
        previous?.Dispose();
        _videoWidth = pending.Width;
        _videoHeight = pending.Height;
        _remoteImage.Hidden = false;
        _emptyViewport.Hidden = true;
        _services.Coordinator.ReportFrameRendered(pending.SessionId);
    }

    private async Task OnInputSwitchChangedAsync()
    {
        if (_changingInputSwitch) return;
        if (!_inputSwitch.On)
        {
            await DisableRemoteInputAsync();
            return;
        }
        if (_remoteInput is null)
        {
            SetInputSwitch(false);
            return;
        }

        try
        {
            ClearError();
            await _remoteInput.SetLocalCaptureEnabledAsync(true);
        }
        catch (Exception exception)
        {
            ShowError(ToUserMessage(exception));
            SetInputSwitch(false);
        }
        UpdateActions();
    }

    private async Task DisableRemoteInputAsync()
    {
        _touchPressed = false;
        if (_remoteInput is { IsLocallyEnabled: true } input)
        {
            try { await input.SetLocalCaptureEnabledAsync(false); }
            catch { /* Local forwarding is already disabled before best-effort ReleaseAll. */ }
        }
        SetInputSwitch(false);
        UpdateActions();
    }

    private void OnRemotePointerGesture(UILongPressGestureRecognizer gesture)
    {
        if (_remoteInput is not { IsLocallyEnabled: true } input) return;
        var point = gesture.LocationInView(_viewport);
        var release = gesture.State is UIGestureRecognizerState.Ended or UIGestureRecognizerState.Cancelled;
        if (!RemoteViewportMapper.TryNormalize(
                point.X,
                point.Y,
                _viewport.Bounds.Width,
                _viewport.Bounds.Height,
                _videoWidth,
                _videoHeight,
                release,
                out var x,
                out var y))
        {
            return;
        }

        switch (gesture.State)
        {
            case UIGestureRecognizerState.Began:
                _touchPressed = true;
                RunInput(() => input.SendPointerButtonAsync(x, y, RemotePointerButton.Left, true));
                break;
            case UIGestureRecognizerState.Changed:
                RunInput(() => input.SendPointerMoveAsync(x, y));
                break;
            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
                if (_touchPressed)
                    RunInput(() => input.SendPointerButtonAsync(x, y, RemotePointerButton.Left, false));
                _touchPressed = false;
                break;
        }
    }

    private async Task SendTextAsync()
    {
        if (_remoteInput is not { IsLocallyEnabled: true } input) return;
        var text = _textInput.Text ?? string.Empty;
        if (text.Length == 0) return;
        if (text.Length > 256 || text.Any(character => !AppleInputMapper.TryMapAscii(character, out _, out _)))
        {
            ShowError("Remote text is limited to 256 printable ASCII characters.");
            return;
        }

        _sendTextButton.Enabled = false;
        try
        {
            foreach (var character in text)
            {
                AppleInputMapper.TryMapAscii(character, out var virtualKey, out var shift);
                if (shift) await input.SendKeyAsync(0x10, true, false);
                await input.SendKeyAsync(virtualKey, true, false);
                await input.SendKeyAsync(virtualKey, false, false);
                if (shift) await input.SendKeyAsync(0x10, false, false);
            }
            _textInput.Text = string.Empty;
        }
        catch (Exception exception)
        {
            ShowError(ToUserMessage(exception));
            await DisableRemoteInputAsync();
        }
        finally
        {
            UpdateActions();
        }
    }

    private void FormatRemoteId()
    {
        if (_formattingRemoteId) return;
        var formatted = PeerOnQId.FormatDisplayInput(_remoteId.Text);
        if (formatted == _remoteId.Text) return;
        _formattingRemoteId = true;
        _remoteId.Text = formatted;
        _formattingRemoteId = false;
    }

    private void UpdateActions()
    {
        var registered = _services?.Signaling.State == SignalingConnectionState.Registered;
        var active = _currentSession is not null;
        _connectButton.Enabled = registered && !active && !_sessionActionRunning;
        _endButton.Enabled = active && !_sessionActionRunning;
        _remoteId.Enabled = !active && !_sessionActionRunning;
        _sessionMode.Enabled = !active && !_sessionActionRunning;
        _inputSwitch.Enabled = _remoteInput is { IsControlPermissionAvailable: true }
                               && active && !_sessionActionRunning;
        var inputEnabled = _remoteInput is { IsLocallyEnabled: true };
        _textInput.Enabled = inputEnabled;
        _sendTextButton.Enabled = inputEnabled && !string.IsNullOrEmpty(_textInput.Text);
    }

    private void SetInputSwitch(bool value)
    {
        _changingInputSwitch = true;
        _inputSwitch.SetState(value, animated: false);
        _changingInputSwitch = false;
    }

    private void SetStatus(string message, bool busy)
    {
        _status.Text = message;
        _status.AccessibilityLabel = message;
        if (busy) _progress.StartAnimating();
        else _progress.StopAnimating();
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.AccessibilityLabel = $"Error: {message}";
        _error.Hidden = false;
        UIAccessibility.PostNotification(
            UIAccessibilityPostNotification.Announcement,
            new NSString($"Error. {message}"));
    }

    private void ClearError()
    {
        _error.Text = string.Empty;
        _error.Hidden = true;
    }

    private void ApplyAdaptiveLayout(CGSize size)
    {
        var wide = OperatingSystem.IsMacCatalyst() || size.Width >= 760 || size.Width > size.Height * 1.25;
        if (wide && _rootStack.Axis != UILayoutConstraintAxis.Horizontal)
        {
            _portraitControlsHeight.Active = false;
            _rootStack.Axis = UILayoutConstraintAxis.Horizontal;
            _wideControlsWidth.Active = true;
        }
        else if (!wide && _rootStack.Axis != UILayoutConstraintAxis.Vertical)
        {
            _wideControlsWidth.Active = false;
            _rootStack.Axis = UILayoutConstraintAxis.Vertical;
            _portraitControlsHeight.Active = true;
        }
        else if (!wide && !_portraitControlsHeight.Active)
        {
            _portraitControlsHeight.Active = true;
        }
    }

    private void RunInput(Func<Task> action) => _ = RunInputAsync(action);

    private async Task RunInputAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception)
        {
            InvokeOnMainThread(async () =>
            {
                ShowError(ToUserMessage(exception));
                await DisableRemoteInputAsync();
            });
        }
    }

    private void DetachServices()
    {
        if (_services is null) return;
        _services.Signaling.StateChanged -= OnSignalingStateChanged;
        _services.Coordinator.SessionChanged -= OnSessionChanged;
        _services.Coordinator.SessionClosed -= OnSessionClosed;
        _services.Coordinator.SessionRemoteFrameReceived -= OnRemoteFrameReceived;
        _services.Coordinator.CollaborationAvailable -= OnCollaborationAvailable;
    }

    private static UILabel CreateLabel(string text, UIFont font, UIColor color) => new()
    {
        Text = text,
        Font = font,
        TextColor = color,
        Lines = 0,
        AdjustsFontForContentSizeCategory = true,
    };

    private static UITextField CreateTextField(string placeholder, string accessibilityLabel)
    {
        var field = new UITextField
        {
            Placeholder = placeholder,
            AccessibilityLabel = accessibilityLabel,
            BorderStyle = UITextBorderStyle.RoundedRect,
            BackgroundColor = AppleTheme.SurfaceMuted,
            TextColor = AppleTheme.Text,
            Font = UIFont.PreferredBody,
            AdjustsFontForContentSizeCategory = true,
            ClearButtonMode = UITextFieldViewMode.WhileEditing,
            AutocapitalizationType = UITextAutocapitalizationType.None,
            AutocorrectionType = UITextAutocorrectionType.No,
            SpellCheckingType = UITextSpellCheckingType.No,
        };
        field.HeightAnchor.ConstraintGreaterThanOrEqualTo(48).Active = true;
        return field;
    }

    private static UIStackView CreateFieldGroup(string label, UIView field) => new(
        [CreateLabel(label, UIFont.PreferredCaption1, AppleTheme.MutedText), field])
    {
        Axis = UILayoutConstraintAxis.Vertical,
        Alignment = UIStackViewAlignment.Fill,
        Distribution = UIStackViewDistribution.Fill,
        Spacing = 6,
    };

    private static UIButton CreateButton(string title, bool primary)
    {
        var button = UIButton.FromType(UIButtonType.System);
        button.SetTitle(title, UIControlState.Normal);
        button.SetTitleColor(primary ? AppleTheme.OnPrimary : AppleTheme.Primary, UIControlState.Normal);
        button.SetTitleColor(AppleTheme.MutedText, UIControlState.Disabled);
        button.BackgroundColor = primary ? AppleTheme.Primary : AppleTheme.SurfaceMuted;
        button.TitleLabel!.Font = UIFont.PreferredHeadline;
        button.TitleLabel.AdjustsFontForContentSizeCategory = true;
        button.Layer.CornerRadius = 10;
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo(48).Active = true;
        button.TouchDown += (_, _) => button.Alpha = 0.72f;
        button.TouchUpInside += (_, _) => button.Alpha = 1;
        button.TouchUpOutside += (_, _) => button.Alpha = 1;
        button.TouchCancel += (_, _) => button.Alpha = 1;
        return button;
    }

    private static string ToUserMessage(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "The remote device did not authorize that action.",
        TimeoutException => "The operation timed out. Check both devices and retry.",
        OperationCanceledException => "The operation was cancelled.",
        InvalidOperationException when exception.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase)
            => exception.Message,
        DllNotFoundException => "The reviewed Apple VP8 decoder is unavailable in this build.",
        _ => "The secure connection could not complete. Check the server and try again.",
    };

    private static string FormatEndReason(SessionEndReason reason) => reason switch
    {
        SessionEndReason.EndedByViewer => "ended on this device",
        SessionEndReason.EndedBySharer => "ended by remote owner",
        SessionEndReason.PermissionDeclined => "permission declined",
        SessionEndReason.PermissionTimeout => "approval timed out",
        SessionEndReason.AuthenticationMismatch or SessionEndReason.ProtocolError => "security check failed",
        SessionEndReason.SignalingLost or SessionEndReason.MediaLost or SessionEndReason.ReconnectFailed => "connection lost",
        _ => "closed",
    };

    private sealed record PendingFrame(SessionId SessionId, int Width, int Height, byte[] Pixels);
}
