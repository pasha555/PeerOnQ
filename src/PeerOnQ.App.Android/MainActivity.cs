using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Platform.Android;

namespace PeerOnQ.App.Android;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : Activity
{
    private AndroidAppServices? _services;
    private SessionId? _currentSession;
    private RemoteInputSession? _remoteInput;
    private LinearLayout _contentLayout = null!;
    private ScrollView _controlScroll = null!;
    private FrameLayout _viewport = null!;
    private SurfaceView _remoteSurface = null!;
    private TextView _emptyViewport = null!;
    private TextView _localId = null!;
    private EditText _remoteId = null!;
    private Spinner _sessionMode = null!;
    private Button _connectButton = null!;
    private Button _endButton = null!;
    private Button _retryButton = null!;
    private ProgressBar _progress = null!;
    private TextView _status = null!;
    private TextView _error = null!;
    private Switch _inputSwitch = null!;
    private EditText _textInput = null!;
    private Button _sendTextButton = null!;
    private bool _sessionActionRunning;
    private bool _changingInputSwitch;
    private bool _formattingRemoteId;
    private bool _touchPressed;
    private int _videoWidth;
    private int _videoHeight;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        Window?.SetSoftInputMode(SoftInput.AdjustResize);
        SetContentView(Resource.Layout.activity_main);
        BindViews();
        ConfigureControls();
        ApplyAdaptiveLayout(Resources?.Configuration);
        SetStatus("Preparing the protected Android device identity…", busy: true);
        UpdateActions();
        _ = InitializeAsync();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyAdaptiveLayout(newConfig);
    }

    protected override void OnPause()
    {
        _ = DisableRemoteInputAsync();
        base.OnPause();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (!hasFocus) _ = DisableRemoteInputAsync();
    }

    protected override void OnDestroy()
    {
        _ = DisableRemoteInputAsync();
        if (_services is not null)
        {
            _services.Signaling.StateChanged -= OnSignalingStateChanged;
            _services.Coordinator.SessionChanged -= OnSessionChanged;
            _services.Coordinator.SessionClosed -= OnSessionClosed;
            _services.Coordinator.CollaborationAvailable -= OnCollaborationAvailable;
            _services.FramePresented -= OnFramePresented;
            _ = _services.DisposeAsync().AsTask();
        }
        base.OnDestroy();
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is null || _remoteInput is not { IsLocallyEnabled: true } input)
            return base.DispatchKeyEvent(e);

        if (e.KeyCode == Keycode.Escape && e.IsCtrlPressed && e.IsAltPressed && e.IsShiftPressed)
        {
            if (e.Action == KeyEventActions.Down) _ = DisableRemoteInputAsync();
            return true;
        }

        if (!AndroidKeyCodeMapper.TryMap((int)e.KeyCode, out var virtualKey, out var extended))
            return base.DispatchKeyEvent(e);
        if (e.Action == KeyEventActions.Down && e.RepeatCount > 0) return true;

        RunInput(() => input.SendKeyAsync(virtualKey, e.Action == KeyEventActions.Down, extended));
        return true;
    }

    private void BindViews()
    {
        _contentLayout = RequireView<LinearLayout>(Resource.Id.content_layout);
        _controlScroll = RequireView<ScrollView>(Resource.Id.control_scroll);
        _viewport = RequireView<FrameLayout>(Resource.Id.viewport);
        _remoteSurface = RequireView<SurfaceView>(Resource.Id.remote_surface);
        _emptyViewport = RequireView<TextView>(Resource.Id.empty_viewport);
        _localId = RequireView<TextView>(Resource.Id.local_id);
        _remoteId = RequireView<EditText>(Resource.Id.remote_id);
        _sessionMode = RequireView<Spinner>(Resource.Id.session_mode);
        _connectButton = RequireView<Button>(Resource.Id.connect_button);
        _endButton = RequireView<Button>(Resource.Id.end_button);
        _retryButton = RequireView<Button>(Resource.Id.retry_button);
        _progress = RequireView<ProgressBar>(Resource.Id.progress);
        _status = RequireView<TextView>(Resource.Id.status);
        _error = RequireView<TextView>(Resource.Id.error);
        _inputSwitch = RequireView<Switch>(Resource.Id.input_switch);
        _textInput = RequireView<EditText>(Resource.Id.text_input);
        _sendTextButton = RequireView<Button>(Resource.Id.send_text_button);
    }

    private void ConfigureControls()
    {
        var modes = new[] { GetString(Resource.String.view_only), GetString(Resource.String.full_control) };
        var adapter = new ArrayAdapter<string>(this, Resource.Layout.spinner_item, modes);
        adapter.SetDropDownViewResource(Resource.Layout.spinner_item);
        _sessionMode.Adapter = adapter;

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            _remoteId.ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants;
            _textInput.ImportantForAutofill = ImportantForAutofill.NoExcludeDescendants;
        }
        _textInput.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions | InputTypes.TextVariationNormal;
        _remoteId.TextChanged += (_, _) => FormatRemoteId();
        _textInput.TextChanged += (_, _) => UpdateActions();
        _connectButton.Click += async (_, _) => await StartSessionAsync();
        _endButton.Click += async (_, _) => await EndSessionAsync();
        _retryButton.Click += async (_, _) => await TryConnectSignalingAsync();
        _inputSwitch.CheckedChange += async (_, _) => await OnInputSwitchChangedAsync();
        _sendTextButton.Click += async (_, _) => await SendTextAsync();
        _remoteSurface.Touch += OnRemoteSurfaceTouch;
        _remoteSurface.SetZOrderMediaOverlay(false);
    }

    private async Task InitializeAsync()
    {
        try
        {
            _services = await AndroidAppServices.CreateAsync(this, _remoteSurface.Holder!);
            _localId.Text = _services.Identity.PublicId.Display;
            _services.Signaling.StateChanged += OnSignalingStateChanged;
            _services.Coordinator.SessionChanged += OnSessionChanged;
            _services.Coordinator.SessionClosed += OnSessionClosed;
            _services.Coordinator.CollaborationAvailable += OnCollaborationAvailable;
            _services.FramePresented += OnFramePresented;
            await TryConnectSignalingAsync();
        }
        catch (Exception exception)
        {
            SetStatus("The Android viewer could not initialize.", busy: false);
            ShowError(ToUserMessage(exception));
            _retryButton.Visibility = ViewStates.Gone;
            UpdateActions();
        }
    }

    private async Task TryConnectSignalingAsync()
    {
        if (_services is null) return;
        ClearError();
        _retryButton.Visibility = ViewStates.Gone;
        SetStatus("Connecting to the signaling server…", busy: true);
        try
        {
            await _services.ConnectAsync();
            SetStatus("Ready. Enter the ID of the device you want to view.", busy: false);
        }
        catch (Exception exception)
        {
            SetStatus("The signaling server is unavailable.", busy: false);
            ShowError(ToUserMessage(exception));
            _retryButton.Visibility = ViewStates.Visible;
        }
        UpdateActions();
    }

    private async Task StartSessionAsync()
    {
        if (_services is null || _sessionActionRunning) return;
        if (!PeerOnQId.TryParse(_remoteId.Text, out var target))
        {
            ShowError("Enter a valid 12-digit PeerOnQ ID, for example 000-000-000-000.");
            _remoteId.RequestFocus();
            return;
        }
        if (target == _services.Identity.PublicId)
        {
            ShowError("Choose another device; this is the Android device ID.");
            return;
        }

        ClearError();
        _sessionActionRunning = true;
        UpdateActions();
        SetStatus("Sending an attended-session request…", busy: true);
        try
        {
            var fullControl = _sessionMode.SelectedItemPosition == 1;
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
        RunOnUiThread(() =>
        {
            if (state is SignalingConnectionState.Disconnected or SignalingConnectionState.Faulted)
                _retryButton.Visibility = ViewStates.Visible;
            UpdateActions();
        });

    private void OnSessionChanged(object? sender, ActiveSessionInfo session) =>
        RunOnUiThread(() => ApplySessionState(session));

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
        _remoteSurface.KeepScreenOn = session.State == SessionState.ConnectedViewOnly;

        if (session.State == SessionState.ConnectedViewOnly && _services is not null)
        {
            var collaboration = _services.Coordinator.CollaborationFor(session.SessionId);
            if (collaboration is not null) BindCollaboration(collaboration);
        }
        UpdateActions();
    }

    private void OnSessionClosed(object? sender, (SessionId Id, SessionEndReason Reason) closed) =>
        RunOnUiThread(async () =>
        {
            if (_currentSession != closed.Id) return;
            await DisableRemoteInputAsync();
            _remoteInput = null;
            _currentSession = null;
            _videoWidth = _videoHeight = 0;
            _emptyViewport.Visibility = ViewStates.Visible;
            _remoteSurface.KeepScreenOn = false;
            SetStatus($"Session ended ({FormatEndReason(closed.Reason)}).", busy: false);
            UpdateActions();
        });

    private void OnCollaborationAvailable(object? sender, SessionCollaborationContext collaboration) =>
        RunOnUiThread(() => BindCollaboration(collaboration));

    private void BindCollaboration(SessionCollaborationContext collaboration)
    {
        if (_currentSession != collaboration.SessionId || collaboration.RemoteInput is null) return;
        if (ReferenceEquals(_remoteInput, collaboration.RemoteInput)) return;

        _remoteInput = collaboration.RemoteInput;
        _remoteInput.Warning += (_, _) => RunOnUiThread(() => ShowError("Remote input was interrupted."));
        _remoteInput.ControlPermissionChanged += (_, status) => RunOnUiThread(async () =>
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

    private void OnFramePresented(object? sender, AndroidPresentedFrame presented) =>
        RunOnUiThread(() =>
        {
            if (_currentSession != presented.SessionId) return;
            _videoWidth = presented.Frame.Width;
            _videoHeight = presented.Frame.Height;
            _emptyViewport.Visibility = ViewStates.Gone;
            UpdateRemoteSurfaceLayout();
        });

    private async Task OnInputSwitchChangedAsync()
    {
        if (_changingInputSwitch) return;
        if (!_inputSwitch.Checked)
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
            _viewport.RequestFocus();
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

    private void OnRemoteSurfaceTouch(object? sender, View.TouchEventArgs args)
    {
        var motion = args.Event;
        if (motion is null || _remoteInput is not { IsLocallyEnabled: true } input)
        {
            args.Handled = false;
            return;
        }

        var action = motion.ActionMasked;
        var allowOutside = action is MotionEventActions.Up or MotionEventActions.Cancel;
        if (!TryNormalize(motion.GetX(), motion.GetY(), allowOutside, out var x, out var y))
        {
            args.Handled = false;
            return;
        }

        switch (action)
        {
            case MotionEventActions.Down:
                _touchPressed = true;
                RunInput(() => input.SendPointerButtonAsync(x, y, RemotePointerButton.Left, isPressed: true));
                args.Handled = true;
                break;
            case MotionEventActions.Move:
                RunInput(() => input.SendPointerMoveAsync(x, y));
                args.Handled = true;
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                if (_touchPressed)
                    RunInput(() => input.SendPointerButtonAsync(x, y, RemotePointerButton.Left, isPressed: false));
                _touchPressed = false;
                args.Handled = true;
                break;
            default:
                args.Handled = false;
                break;
        }
    }

    private bool TryNormalize(
        float pointerX,
        float pointerY,
        bool allowOutside,
        out double x,
        out double y)
    {
        x = y = 0;
        if (_videoWidth <= 0 || _videoHeight <= 0 || _remoteSurface.Width <= 0 || _remoteSurface.Height <= 0)
            return false;
        var scale = Math.Min(_remoteSurface.Width / (double)_videoWidth, _remoteSurface.Height / (double)_videoHeight);
        var renderedWidth = _videoWidth * scale;
        var renderedHeight = _videoHeight * scale;
        var offsetX = (_remoteSurface.Width - renderedWidth) / 2;
        var offsetY = (_remoteSurface.Height - renderedHeight) / 2;
        if (!allowOutside && (pointerX < offsetX || pointerX > offsetX + renderedWidth
            || pointerY < offsetY || pointerY > offsetY + renderedHeight))
            return false;
        x = Math.Clamp((pointerX - offsetX) / renderedWidth, 0, 1);
        y = Math.Clamp((pointerY - offsetY) / renderedHeight, 0, 1);
        return true;
    }

    private async Task SendTextAsync()
    {
        if (_remoteInput is not { IsLocallyEnabled: true } input) return;
        var text = _textInput.Text ?? string.Empty;
        if (text.Length == 0) return;
        if (text.Any(character => !TryMapAscii(character, out _, out _)))
        {
            ShowError("Short text can contain printable ASCII characters only.");
            return;
        }

        _sendTextButton.Enabled = false;
        try
        {
            foreach (var character in text)
            {
                TryMapAscii(character, out var virtualKey, out var shift);
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

    private static bool TryMapAscii(char character, out ushort virtualKey, out bool shift)
    {
        shift = false;
        if (character is >= 'a' and <= 'z')
        {
            virtualKey = (ushort)char.ToUpperInvariant(character);
            return true;
        }
        if (character is >= 'A' and <= 'Z')
        {
            virtualKey = character;
            shift = true;
            return true;
        }
        if (character is >= '0' and <= '9')
        {
            virtualKey = character;
            return true;
        }

        var direct = character switch
        {
            ' ' => 0x20,
            ',' => 0xbc,
            '.' => 0xbe,
            '-' => 0xbd,
            '=' => 0xbb,
            '[' => 0xdb,
            ']' => 0xdd,
            '\\' => 0xdc,
            ';' => 0xba,
            '\'' => 0xde,
            '/' => 0xbf,
            '`' => 0xc0,
            _ => 0,
        };
        if (direct != 0)
        {
            virtualKey = (ushort)direct;
            return true;
        }

        const string shifted = "!@#$%^&*()_+{}|:\"<>?~";
        const string bases = "1234567890-=[]\\;',./`";
        var index = shifted.IndexOf(character, StringComparison.Ordinal);
        if (index >= 0)
        {
            shift = true;
            return TryMapAscii(bases[index], out virtualKey, out _);
        }

        virtualKey = 0;
        return false;
    }

    private void FormatRemoteId()
    {
        if (_formattingRemoteId) return;
        var formatted = PeerOnQId.FormatDisplayInput(_remoteId.Text);
        if (formatted == _remoteId.Text) return;
        _formattingRemoteId = true;
        _remoteId.Text = formatted;
        _remoteId.SetSelection(formatted.Length);
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
        _inputSwitch.Checked = value;
        _changingInputSwitch = false;
    }

    private void SetStatus(string message, bool busy)
    {
        _status.Text = message;
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = ViewStates.Visible;
    }

    private void ClearError()
    {
        _error.Text = string.Empty;
        _error.Visibility = ViewStates.Gone;
    }

    private void ApplyAdaptiveLayout(Configuration? configuration)
    {
        if (configuration is null) return;
        var wide = configuration.Orientation == global::Android.Content.Res.Orientation.Landscape
                   || configuration.SmallestScreenWidthDp >= 600;
        _contentLayout.Orientation = wide
            ? global::Android.Widget.Orientation.Horizontal
            : global::Android.Widget.Orientation.Vertical;
        var margin = (int)(8 * (Resources?.DisplayMetrics?.Density ?? 1));
        if (wide)
        {
            _controlScroll.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 0.9f);
            var viewportParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1.4f);
            viewportParameters.SetMargins(margin, margin, margin, margin);
            _viewport.LayoutParameters = viewportParameters;
        }
        else
        {
            _controlScroll.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f);
            var viewportParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.1f);
            viewportParameters.SetMargins(margin, margin, margin, margin);
            _viewport.LayoutParameters = viewportParameters;
        }
        _viewport.Post(UpdateRemoteSurfaceLayout);
    }

    private void UpdateRemoteSurfaceLayout()
    {
        if (_videoWidth <= 0 || _videoHeight <= 0 || _viewport.Width <= 0 || _viewport.Height <= 0)
            return;
        var scale = Math.Min(_viewport.Width / (double)_videoWidth, _viewport.Height / (double)_videoHeight);
        var width = Math.Max(1, (int)Math.Round(_videoWidth * scale));
        var height = Math.Max(1, (int)Math.Round(_videoHeight * scale));
        _remoteSurface.LayoutParameters = new FrameLayout.LayoutParams(width, height, GravityFlags.Center);
    }

    private void RunInput(Func<Task> action) => _ = RunInputAsync(action);

    private async Task RunInputAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception)
        {
            RunOnUiThread(async () =>
            {
                ShowError(ToUserMessage(exception));
                await DisableRemoteInputAsync();
            });
        }
    }

    private T RequireView<T>(int id) where T : View =>
        FindViewById<T>(id) ?? throw new InvalidOperationException($"Required Android view {id} is missing.");

    private static string ToUserMessage(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "The remote device did not authorize that action.",
        TimeoutException => "The operation timed out. Check both devices and retry.",
        System.OperationCanceledException => "The operation was cancelled.",
        InvalidOperationException when exception.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase)
            => exception.Message,
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
}
