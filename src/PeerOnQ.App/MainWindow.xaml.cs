using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Platform.Windows.Capture;
using PeerOnQ.Infrastructure.Updates;
using PeerOnQ.Shared.Contracts.V1;
using PeerOnQ.Transport.Protocol;
using PeerOnQ.Infrastructure.Cloud;
using PeerOnQ.Infrastructure.Configuration;
using PeerOnQ.Infrastructure.Diagnostics;
using PeerOnQ.Infrastructure.Persistence;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PeerOnQ.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the Window lifetime; the Closed handler disposes the tray icon and asynchronous application services.")]
public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly Task _initializationTask;
    private AppServices? _services;
    private SharingIndicatorWindow? _indicator;
    private ViewerWindow? _viewer;
    private readonly Dictionary<SessionId, ViewerWindow> _viewers = [];
    private readonly Dictionary<SessionId, SessionCollaborationContext> _collaborations = [];
    private TrayIcon? _tray;
    private SessionId? _currentSession;
    private SessionId? _lastSessionForDiagnostics;
    private ActiveSessionInfo? _currentSessionInfo;
    private SessionCollaborationContext? _collaboration;
    private bool _updatingPrivacyControls;
    private bool _updatingTransferPriority;
    private VerifiedUpdate? _availableUpdate;
    private StagedUpdate? _stagedUpdate;
    private readonly DispatcherQueueTimer _performanceTimer;
    private readonly DispatcherQueueTimer _signalingRetryTimer;
    private bool _signalingConnectInProgress;
    private bool _formattingRemoteId;
    private bool _updatingSavedDevicePicker;
    private bool _minimizedForSharing;
    private bool _hiddenForViewer;
    private int _shutdownState;
    private int _endingCurrentSession;
    private int _sessionRequestInProgress;
    private int _revokingCurrentControl;
    private bool _controlRevoked;
    private readonly HashSet<SessionId> _autoSavedSessions = [];
    private IReadOnlyList<SessionAuditEntry> _sessionHistory = [];
    private IReadOnlyList<SessionAuditEntry> _filteredSessionHistory = [];
    private NetworkDoctorReport? _lastNetworkDoctorReport;
    private string? _latestSupportInvitationUri;
    private static readonly string[] SearchDestinations =
        ["Dashboard", "Devices", "Sessions", "Address Book", "Security", "Settings"];
    private sealed record DeviceDirectoryRow(AddressBookDevice? SavedDevice, TrustedDevice? TrustRecord)
    {
        public PeerOnQId DeviceId => SavedDevice?.DeviceId ?? TrustRecord!.DeviceId;
        public string DisplayName => SavedDevice?.DisplayName ?? TrustRecord!.DisplayName;
        public string OperatingSystem => string.IsNullOrWhiteSpace(SavedDevice?.OperatingSystem)
            ? "Authenticated OS unavailable"
            : SavedDevice.OperatingSystem;
        public string TrustLabel => TrustRecord?.TrustState == DeviceTrustState.Trusted
            ? $"Trusted: {TrustRecord.AllowedPermissions}"
            : "Saved locally - not trusted";
        public DateTimeOffset? LastActivityAt => SavedDevice?.LastSeenAt ?? TrustRecord?.LastUsedAt;
        public string ActivityLabel => LastActivityAt is { } lastSeen
            ? $"Last connected {lastSeen.ToLocalTime():g}"
            : SavedDevice is not null ? "Saved manually" : "Trusted device";
    }

    public MainWindow()
    {
        InitializeComponent();
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        }

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _performanceTimer = _dispatcher.CreateTimer();
        _performanceTimer.Interval = TimeSpan.FromSeconds(2);
        _performanceTimer.Tick += (_, _) => RefreshPerformanceStatus();
        _signalingRetryTimer = _dispatcher.CreateTimer();
        _signalingRetryTimer.Interval = TimeSpan.FromSeconds(10);
        _signalingRetryTimer.Tick += async (_, _) => await EnsureSignalingConnectedAsync();
        AppWindow.Closing += OnAppWindowClosing;

        _initializationTask = InitializeAsync(_shutdownCancellation.Token);
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var prompt = new PermissionDialogHost(_dispatcher, () => Content?.XamlRoot);
            var services = await AppServices.CreateAsync(prompt);
            prompt.Logger = services.LoggerFactory.CreateLogger<PermissionDialogHost>();
            if (cancellationToken.IsCancellationRequested)
            {
                await services.DisposeAsync();
                return;
            }

            if (services.IsLanDevelopmentClient)
            {
                QualityPicker.SelectedItem = QualityPicker.Items.OfType<ComboBoxItem>()
                    .Single(item => Equals(item.Tag, nameof(QualityProfile.Quality)));
                ResolutionPicker.SelectedItem = ResolutionPicker.Items.OfType<ComboBoxItem>()
                    .Single(item => Equals(item.Tag, nameof(CaptureResolution.P2160)));
            }

            _services = services;
            App.Services = _services;
            ApplySelectedQualityProfile();

            RefreshRoutingEnrollmentUi();
            DeviceNameText.Text = $"Name: {_services.Identity.DisplayName}";
            DashboardGreetingText.Text = $"{GreetingForCurrentTime()}, {_services.Identity.DisplayName}";
            DashboardOsText.Text = Environment.OSVersion.VersionString;
            DashboardVersionText.Text = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            AboutVersionText.Text = $"PeerOnQ {DashboardVersionText.Text}";
            LocalDataPathText.Text = _services.Paths.Root;
            if (App.IsPortableSupport)
            {
                UnattendedConnectionToggle.IsOn = false;
                UnattendedConnectionToggle.IsEnabled = false;
                RemoteUnattendedCredentialPanel.Visibility = Visibility.Collapsed;
                AboutVersionText.Text += " · Portable Support · Development / Unsigned";
                DashboardVersionText.Text += " · portable support (development / unsigned)";
                StatusText.Text = "Portable Support mode uses a temporary profile. Unattended access is unavailable.";
            }

            LoadDisplays();

            _services.Signaling.StateChanged += (_, state) =>
                _dispatcher.TryEnqueue(() =>
                {
                    UpdateHeaderConnectionStatus(state == SignalingConnectionState.Registered);
                    DashboardAgentStatusText.Text = state == SignalingConnectionState.Registered
                        ? "Agent ready - signaling connected"
                        : "Agent ready - signaling disconnected";
                });
            _services.Signaling.ErrorReceived += (_, error) =>
                _dispatcher.TryEnqueue(() => ShowError(FormatSignalingError(error)));
            UpdateHeaderConnectionStatus(
                _services.Signaling.State == SignalingConnectionState.Registered);
            if (_services.CloudPlatform is not null)
            {
                _services.CloudPlatform.EnrollmentCompleted += (_, _) =>
                    _dispatcher.TryEnqueue(() =>
                    {
                        RefreshRoutingEnrollmentUi();
                        _ = EnsureSignalingConnectedAsync();
                    });
            }

            _services.Coordinator.SessionChanged += OnSessionChanged;
            _services.Coordinator.SessionClosed += OnSessionClosed;
            _services.Coordinator.SessionRemoteFrameReceived += OnRemoteFrame;
            _services.Coordinator.RemoteDisplaysChanged += OnRemoteDisplays;
            _services.Coordinator.CollaborationAvailable += OnCollaborationAvailable;
            _services.UnattendedAccess.SecurityNotification += (_, message) =>
                _dispatcher.TryEnqueue(() => StatusText.Text = message);

            _tray = new TrayIcon(
                WindowNative.GetWindowHandle(this),
                "PeerOnQ",
                EndCurrentSessionAsync,
                RevokeCurrentControlAsync,
                OpenSettingsFromTray,
                Close);

            if (_services.RoutingIdentityReady)
                DashboardAgentStatusText.Text = "Agent ready - signaling disconnected";
            StatusText.Text = AppServices.DataDirectoryOverride is null
                ? $"Data folder: {_services.Paths.Root}"
                : $"Data folder ({AppServices.DataDirectoryEnvironmentVariableName}): {_services.Paths.Root}";
            SettingsDiagnosticsText.Text =
                $"Version {DashboardVersionText.Text} | Data: {_services.Paths.Root} | " +
                "Diagnostics stay local unless you explicitly approve one upload.";
            SendDiagnosticsButton.IsEnabled = _services.CloudPlatform is not null;
            DiagnosticsUploadStatusText.Text = _services.CloudPlatform is null
                ? "Cloud diagnostics is not configured in this development build. Local export remains available."
                : "Cloud upload is available only after a per-bundle confirmation.";
            await RefreshGroupsAsync();
            await RefreshAddressBookAsync();
            await RefreshTrustedDevicesAsync();
            await RefreshDashboardSavedDevicesAsync();
            await RefreshBlockedDevicesAsync();
            await RefreshSessionHistoryAsync();
            await RefreshUnattendedStatusAsync();
            await RefreshSupportInvitationsAsync();
            await RefreshPrivacyAndAuditAsync();
            App.MarkReady();
            UpdateStatusText.Text = _services.Updates is null
                ? "Verified client updates are unavailable because this development package has no trusted server update configuration."
                : "Verified client updates are ready. Check the server for a newer signed version.";
            CheckUpdateButton.IsEnabled = _services.Updates is not null;
            RefreshPerformanceStatus();
            _performanceTimer.Start();
            _signalingRetryTimer.Start();
            _ = EnsureSignalingConnectedAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The first close is held until initialization exits and any created services are
            // disposed by the shutdown path below.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                ShowError($"Startup failed: {ex.Message}");
        }
    }

    private void LoadDisplays()
    {
        if (_services is null) return;
        _services.Coordinator.PreferredCaptureTarget = DisplayEnumerator.ListCaptureTargets().FirstOrDefault();
    }

    private void OnCopyId(object sender, RoutedEventArgs e)
    {
        if (_services is null || !_services.RoutingIdentityReady)
        {
            ShowError("This device is still enrolling. No public routing ID is available yet.");
            return;
        }

        var package = new DataPackage();
        package.SetText(_services.Identity.PublicId.Display);
        Clipboard.SetContent(package);
        StatusText.Text = "Device ID copied.";
    }

    private async void OnOpenAccountPortal(object sender, RoutedEventArgs e)
    {
        try
        {
#if DEBUG
            const bool AllowDevelopmentLoopback = true;
#else
            const bool AllowDevelopmentLoopback = false;
#endif
            var portalUri = CloudEndpointConfiguration.ResolveAccountPortalUri(
                typeof(App).Assembly, AllowDevelopmentLoopback);
            if (await Windows.System.Launcher.LaunchUriAsync(portalUri))
                StatusText.Text = "Opened Account Portal in your default browser.";
            else
                ShowError("Could not open Account Portal. Open it from peeronq.com in your browser.");
        }
        catch (Exception)
        {
            ShowError("Could not open Account Portal. Open it from peeronq.com in your browser.");
        }
    }

    private void OnOpenLocalDataFolder(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        try
        {
            Directory.CreateDirectory(_services.Paths.Root);
            Process.Start(new ProcessStartInfo
            {
                FileName = _services.Paths.Root,
                UseShellExecute = true,
            });
            StatusText.Text = "Opened the local PeerOnQ data folder.";
        }
        catch (Exception ex)
        {
            ShowError($"Could not open the local data folder: {ex.Message}");
        }
    }

    private async Task EnsureSignalingConnectedAsync()
    {
        if (_services is null
            || !_services.RoutingIdentityReady
            || _signalingConnectInProgress
            || _services.Signaling.State == SignalingConnectionState.Registered)
            return;

        _signalingConnectInProgress = true;
        try
        {
            await _services.Signaling.ConnectAsync(_services.Identity);
            StatusText.Text = "Registered with the signaling server.";
            DashboardErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            UpdateHeaderConnectionStatus(isConnected: false);
            DashboardAgentStatusText.Text = "Agent ready - reconnecting to signaling";
            StatusText.Text = "Secure signaling unavailable. PeerOnQ will retry automatically.";
            _services.LoggerFactory.CreateLogger<MainWindow>()
                .LogWarning(ex, "Automatic signaling connection attempt failed");
        }
        finally
        {
            _signalingConnectInProgress = false;
        }
    }

    private async void OnRequestSession(object sender, RoutedEventArgs e)
    {
        if (_services is null || !_services.RoutingIdentityReady)
        {
            ShowError("Cloud enrollment must complete before remote connections are enabled.");
            return;
        }
        if (Interlocked.CompareExchange(ref _sessionRequestInProgress, 1, 0) != 0) return;

        var unattendedRequested = UnattendedConnectionToggle.IsOn;
        var supportRequested = SupportInvitationToggle.IsOn;
        ConnectButton.IsEnabled = false;
        try
        {
            if (supportRequested)
            {
                if (!SupportInvitationLink.TryParse(SupportInvitationLinkBox.Text, out var invitation))
                {
                    ShowError("Paste a valid PeerOnQ support invitation link.");
                    return;
                }
                RemoteIdBox.Text = invitation.TargetDevice.Display;
                await TryRequestSupportSessionAsync(
                    invitation,
                    string.IsNullOrEmpty(SupportInvitationPasswordBox.Password)
                        ? null
                        : SupportInvitationPasswordBox.Password);
            }
            else
            {
                var (mode, permissions, accessKind, unattendedPassword) = BuildRequestedSessionScope();
                await TryRequestSessionAsync(
                    RemoteIdBox.Text,
                    mode,
                    permissions,
                    accessKind,
                    unattendedPassword,
                    remoteScopeSelectionRequired: accessKind == SessionAccessKind.Attended);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            if (unattendedRequested)
                RemoteUnattendedPasswordBox.Password = string.Empty;
            if (supportRequested)
            {
                SupportInvitationPasswordBox.Password = string.Empty;
                SupportInvitationLinkBox.Text = string.Empty;
            }
            Interlocked.Exchange(ref _sessionRequestInProgress, 0);
            ConnectButton.IsEnabled = _services?.RoutingIdentityReady == true;
        }
    }

    private void RefreshRoutingEnrollmentUi()
    {
        if (_services is null) return;
        var ready = _services.RoutingIdentityReady;
        DeviceIdText.Text = ready ? _services.Identity.PublicId.Display : "Enrollment pending";
        CopyIdButton.IsEnabled = ready;
        ConnectButton.IsEnabled = ready;
        if (!ready)
        {
            UpdateHeaderConnectionStatus(isConnected: false);
            DashboardAgentStatusText.Text =
                "Offline - public routing is disabled until secure enrollment completes";
        }
    }

    private void UpdateHeaderConnectionStatus(bool isConnected)
    {
        HeaderConnectionText.Text = isConnected ? "Connected" : "Disconnected";
        HeaderConnectedDot.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        HeaderDisconnectedDot.Visibility = isConnected ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatSignalingError(SignalingErrorNotification error)
    {
        if (error.Code != SignalingErrorCodes.CapabilityMismatch)
            return $"{error.Code}: {error.Message}";

        var computer = error.CapabilitySide switch
        {
            "requester" => "This computer",
            "target" => "The other computer",
            _ => "One computer",
        };
        if (error.RequiredCapabilities?.Contains(
                EndpointCapabilityNames.HybridPostQuantumSecure,
                StringComparer.Ordinal) == true)
        {
            return $"{computer}'s Windows build does not provide the ML-KEM/ML-DSA security required by PeerOnQ. " +
                   "Install all Windows updates on that computer, then exit PeerOnQ from the tray and reopen it. " +
                   "PeerOnQ will not downgrade session encryption.";
        }

        return $"{computer} does not support every capability required by the selected connection mode. " +
               "Try View Only, or update and restart PeerOnQ on that computer.";
    }

    private void OnRemoteIdTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_formattingRemoteId || sender is not TextBox textBox) return;

        var original = textBox.Text;
        var caret = Math.Clamp(textBox.SelectionStart, 0, original.Length);
        var digitsBeforeCaret = 0;
        for (var index = 0; index < caret; index++)
        {
            if (char.IsAsciiDigit(original[index])) digitsBeforeCaret++;
        }
        var formatted = PeerOnQId.FormatDisplayInput(original);
        if (formatted.Equals(original, StringComparison.Ordinal))
        {
            if (ReferenceEquals(textBox, RemoteIdBox)) SynchronizeSavedDevicePicker(formatted);
            return;
        }

        _formattingRemoteId = true;
        try
        {
            textBox.Text = formatted;
            textBox.SelectionStart = CaretAfterDigits(formatted, digitsBeforeCaret);
            textBox.SelectionLength = 0;
        }
        finally
        {
            _formattingRemoteId = false;
        }
        if (ReferenceEquals(textBox, RemoteIdBox)) SynchronizeSavedDevicePicker(formatted);
    }

    private static int CaretAfterDigits(string formatted, int digitCount)
    {
        if (digitCount <= 0) return 0;

        var remaining = digitCount;
        for (var index = 0; index < formatted.Length; index++)
        {
            if (!char.IsAsciiDigit(formatted[index])) continue;
            remaining--;
            if (remaining == 0) return index + 1;
        }

        return formatted.Length;
    }

    private async Task<bool> TryRequestSessionAsync(
        string remoteId,
        SessionMode mode,
        SessionPermission permissions,
        SessionAccessKind accessKind,
        string? unattendedPassword,
        bool remoteScopeSelectionRequired = false)
    {
        if (_services is null) return false;

        if (!PeerOnQId.TryParse(remoteId, out var target))
        {
            ShowError("That is not a valid PeerOnQ ID. Expected 000-000-000-000.");
            return false;
        }

        if (_services.Signaling.State != SignalingConnectionState.Registered)
        {
            ShowError("The secure signaling connection is unavailable. PeerOnQ is reconnecting automatically.");
            return false;
        }

        var effectiveMode = mode;
        var effectivePermissions = permissions;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                DashboardErrorBar.IsOpen = false;
                SessionText.Text = accessKind == SessionAccessKind.Unattended
                    ? $"Requesting password authorization from {target.MaskedDisplay}..."
                    : $"Sending connection request to {target.MaskedDisplay}...";
                var sessionId = remoteScopeSelectionRequired
                    ? await _services.Coordinator.RequestRemoteSelectedSessionAsync(target)
                    : await _services.Coordinator.RequestSessionAsync(
                        target,
                        effectiveMode,
                        effectivePermissions,
                        accessKind,
                        unattendedPassword);
                _currentSession = sessionId;
                SessionText.Text = accessKind == SessionAccessKind.Unattended
                    ? $"Authenticating unattended access to {target.MaskedDisplay}..."
                    : $"Waiting for {target.MaskedDisplay} to accept...";
                EndSessionButton.IsEnabled = true;
                return true;
            }
            catch (UnattendedPermissionMismatchException mismatch) when (
                attempt == 0
                && accessKind == SessionAccessKind.Unattended
                && TryGetSessionMode(mismatch.AllowedPermissions, out var allowedMode))
            {
                if (!await ConfirmUnattendedModeChangeAsync(
                        effectivePermissions,
                        mismatch.AllowedPermissions))
                {
                    SessionText.Text =
                        $"Password connection canceled. Select {DescribeUnattendedPermissions(mismatch.AllowedPermissions)} or change the remote unattended policy.";
                    return false;
                }

                effectiveMode = allowedMode;
                effectivePermissions = mismatch.AllowedPermissions;
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                return false;
            }
        }

        return false;
    }

    private async Task<bool> TryRequestSupportSessionAsync(
        SupportInvitationLink invitation,
        string? password)
    {
        if (_services is null) return false;
        if (_services.Signaling.State != SignalingConnectionState.Registered)
        {
            ShowError("The secure signaling connection is unavailable. PeerOnQ is reconnecting automatically.");
            return false;
        }

        try
        {
            DashboardErrorBar.IsOpen = false;
            SessionText.Text = $"Verifying support invitation for {invitation.TargetDevice.MaskedDisplay}...";
            var sessionId = await _services.Coordinator.RequestSupportSessionAsync(
                invitation.TargetDevice,
                invitation.Mode,
                invitation.Permissions,
                invitation.Token,
                password);
            _currentSession = sessionId;
            SessionText.Text = $"Waiting for explicit support approval from {invitation.TargetDevice.MaskedDisplay}...";
            EndSessionButton.IsEnabled = true;
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return false;
        }
    }

    private async Task<bool> ConfirmUnattendedModeChangeAsync(
        SessionPermission requestedPermissions,
        SessionPermission allowedPermissions)
    {
        if (Content?.XamlRoot is not { } root) return false;

        var requested = DescribeUnattendedPermissions(requestedPermissions);
        var allowed = DescribeUnattendedPermissions(allowedPermissions);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Use the remote computer's allowed mode?",
            Content = $"The remote computer allows {allowed} for password access, but you selected {requested}. PeerOnQ can connect using {allowed} without weakening the remote security policy.",
            PrimaryButtonText = $"Connect using {allowed}",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnEndSession(object sender, RoutedEventArgs e)
    {
        await EndCurrentSessionAsync();
    }

    private async void OnRevokeControl(object sender, RoutedEventArgs e)
    {
        await RevokeCurrentControlAsync();
    }

    private async Task RevokeCurrentControlAsync()
    {
        if (_services is null || _currentSession is not { } sessionId
            || _currentSessionInfo?.Role != SessionRole.Sharer
            || !_currentSessionInfo.Permissions.HasFlag(SessionPermission.ControlInput)
            || _controlRevoked
            || Interlocked.CompareExchange(ref _revokingCurrentControl, 1, 0) != 0)
            return;

        try
        {
            if (!await _services.Coordinator.RevokeControlAsync(sessionId)) return;

            _controlRevoked = true;
            RevokeControlButton.IsEnabled = false;
            _indicator?.MarkControlRevoked();
            SessionText.Text = $"{SessionText.Text} · remote control revoked";
            CollaborationStatusText.Text = "ControlInput revoked. View permission remains active.";
            StatusText.Text = "Remote keyboard and mouse control was revoked for this session.";
            _tray?.SetActiveSession(true, SessionText.Text, canRevokeControl: false);
        }
        catch (Exception ex)
        {
            ShowError($"Control could not be revoked: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _revokingCurrentControl, 0);
        }
    }

    private async Task EndCurrentSessionAsync()
    {
        if (_services is null || _currentSession is not { } sessionId
            || Interlocked.CompareExchange(ref _endingCurrentSession, 1, 0) != 0)
            return;

        try
        {
            var reason = _currentSessionInfo?.Role == SessionRole.Sharer
                ? SessionEndReason.EndedBySharer
                : SessionEndReason.EndedByViewer;
            await _services.Coordinator.EndSessionAsync(sessionId, reason);
        }
        finally
        {
            Interlocked.Exchange(ref _endingCurrentSession, 0);
        }
    }

    private async void OnSessionFilterChanged(object sender, SelectionChangedEventArgs e) =>
        await RefreshSessionHistoryAsync();

    private async Task RefreshSessionHistoryAsync()
    {
        if (_services is null || SessionHistoryList is null) return;

        _sessionHistory = await _services.SessionAudit.RecentAsync(200);
        IEnumerable<SessionAuditEntry> filtered = _sessionHistory;

        if ((SessionDirectionFilter.SelectedItem as ComboBoxItem)?.Tag is string direction
            && Enum.TryParse<SessionRole>(direction, out var role))
            filtered = filtered.Where(entry => entry.Role == role);

        if ((SessionModeFilter.SelectedItem as ComboBoxItem)?.Tag is string modeValue
            && Enum.TryParse<SessionMode>(modeValue, out var mode))
            filtered = filtered.Where(entry => entry.Mode == mode);

        if ((SessionTimeFilter.SelectedItem as ComboBoxItem)?.Tag is string dayValue
            && int.TryParse(dayValue, out var days))
        {
            var threshold = DateTimeOffset.Now.AddDays(-days);
            filtered = filtered.Where(entry => entry.StartedAt >= threshold);
        }

        _filteredSessionHistory = filtered.ToArray();
        SessionHistoryList.Items.Clear();
        foreach (var entry in _filteredSessionHistory)
            SessionHistoryList.Items.Add(CreateSessionHistoryRow(entry));

        var empty = _filteredSessionHistory.Count == 0;
        SessionHistoryList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        SessionHistoryEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ListViewItem CreateSessionHistoryRow(SessionAuditEntry entry)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.PeerDisplayName) ? "Remote device" : entry.PeerDisplayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var direction = entry.Role == SessionRole.Viewer ? "Outgoing" : "Incoming";
        var peerId = PeerOnQId.FormatMaskedDisplay(entry.PeerMaskedId);
        var detail = new TextBlock
        {
            Text = $"{peerId}  •  {direction}  •  {entry.Mode}  •  {entry.StartedAt.ToLocalTime():g}",
            TextWrapping = TextWrapping.Wrap,
        };
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(title);
        text.Children.Add(detail);

        var outcome = new TextBlock
        {
            Text = entry.EndedAt is null ? "Active" : entry.EndReason.ToString(),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var row = new Grid { Padding = new Thickness(8, 10, 8, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(text);
        Grid.SetColumn(outcome, 1);
        row.Children.Add(outcome);

        return new ListViewItem { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private async void OnExportSessions(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var picker = new FileSavePicker { SuggestedFileName = $"peeronq-sessions-{DateTime.UtcNow:yyyyMMdd}" };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        static string Csv(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        var lines = new List<string>
        {
            "started_at,ended_at,direction,peer_name,peer_masked_id,mode,end_reason",
        };
        lines.AddRange(_filteredSessionHistory.Select(entry => string.Join(",",
            Csv(entry.StartedAt.ToString("O")),
            Csv(entry.EndedAt?.ToString("O") ?? string.Empty),
            Csv(entry.Role == SessionRole.Viewer ? "outgoing" : "incoming"),
            Csv(entry.PeerDisplayName),
            Csv(entry.PeerMaskedId),
            Csv(entry.Mode.ToString()),
            Csv(entry.EndReason.ToString()))));
        await File.WriteAllLinesAsync(file.Path, lines, new System.Text.UTF8Encoding(false));
        StatusText.Text = "Sanitized session history exported.";
    }

    private void OnSessionChanged(object? sender, ActiveSessionInfo info)
    {
        if (info.State == SessionState.ConnectedViewOnly)
            PublishPresenceState(PresenceStateV1.InSession);

        _dispatcher.TryEnqueue(() =>
        {
            if (_currentSession != info.SessionId) _controlRevoked = false;
            _currentSession = info.SessionId;
            _lastSessionForDiagnostics = info.SessionId;
            _currentSessionInfo = info;
            EndSessionButton.IsEnabled = true;
            RevokeControlButton.IsEnabled = info.State == SessionState.ConnectedViewOnly
                                            && info.Role == SessionRole.Sharer
                                            && info.Permissions.HasFlag(SessionPermission.ControlInput)
                                            && !_controlRevoked;
            var stateText = DisplaySessionState(info);
            if (info.Security is { PeerAuthenticated: true, PostQuantumProtected: true })
                stateText = $"{stateText} · Post-Quantum Protected";
            if (_controlRevoked) stateText = $"{stateText} · Control revoked";
            var permissionText = _controlRevoked
                ? $"{info.Permissions & ~SessionPermission.ControlInput} (ControlInput revoked)"
                : info.Permissions.ToString();

            var access = info.AccessKind == SessionAccessKind.Unattended ? "UNATTENDED - " : string.Empty;
            var peerId = PeerOnQId.FormatMaskedDisplay(info.PeerMaskedId);
            SessionText.Text = info.Role == SessionRole.Sharer
                ? $"{access}Session with {info.PeerDisplayName} ({peerId}) - {stateText} - {permissionText}"
                : $"{access}Connected to {info.PeerDisplayName} ({peerId}) - {stateText} - {permissionText}";
            TrustCurrentButton.IsEnabled = info.PeerKeyFingerprint is not null;

            if (info.Role == SessionRole.Viewer
                && info.State == SessionState.ConnectedViewOnly
                && info.PeerDeviceId is not null
                && _autoSavedSessions.Add(info.SessionId))
            {
                _ = RecordConnectedDeviceAsync(info);
            }

            _tray?.SetActiveSession(
                true,
                SessionText.Text,
                canRevokeControl: RevokeControlButton.IsEnabled);

            _viewers.GetValueOrDefault(info.SessionId)?.UpdateConnectionState(info.State);

            if (info.State != SessionState.ConnectedViewOnly) return;

            if (info.Mode == SessionMode.FileTransferOnly)
            {
                NavigateTo("FileTransfer");
                return;
            }

            if (info.Role == SessionRole.Sharer && info.Permissions.HasFlag(SessionPermission.ViewScreen))
            {
                MinimizeForSharing();
                _indicator ??= new SharingIndicatorWindow(
                    info,
                    onStop: async () =>
                    {
                        await EndCurrentSessionAsync();
                    },
                    onRevokeControl: RevokeCurrentControlAsync);
                _indicator.Activate();
            }
            else if (info.Permissions.HasFlag(SessionPermission.ViewScreen))
            {
                var created = false;
                if (!_viewers.TryGetValue(info.SessionId, out var viewer))
                {
                    viewer = new ViewerWindow(
                        info,
                        () => _services?.Coordinator.StatisticsOf(info.SessionId) ?? MediaStatistics.Empty,
                        onEnd: async () =>
                        {
                            if (_services is not null)
                            {
                                await _services.Coordinator.EndSessionAsync(info.SessionId, SessionEndReason.EndedByViewer);
                            }
                        },
                        remoteInput: _services?.Coordinator.CollaborationFor(info.SessionId)?.RemoteInput,
                        fileTransfers: _services?.Coordinator.CollaborationFor(info.SessionId)?.FileTransfers,
                        onSelectDisplay: async displayId =>
                        {
                            if (_services is not null)
                            {
                                await _services.Coordinator.RequestRemoteDisplayAsync(info.SessionId, displayId);
                            }
                        },
                        onRendered: frame =>
                            _services?.Coordinator.ReportFrameRendered(info.SessionId, frame),
                        timeline: () => _services?.Coordinator.TimelineOf(info.SessionId) ?? [],
                        logger: _services?.LoggerFactory.CreateLogger<ViewerWindow>(),
                        onActivated: SelectActiveSession,
                        onClosed: OnViewerClosed);
                    _viewers.Add(info.SessionId, viewer);
                    created = true;
                }
                _viewer = viewer;
                if (created) viewer.Activate();
                HideForViewer();
                if (_services?.Coordinator.CollaborationFor(info.SessionId)?.IsReady == true)
                {
                    viewer.EnableAuthorizedInput();
                }
            }
        });
    }

    private void OnSessionClosed(object? sender, (SessionId Id, SessionEndReason Reason) e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _autoSavedSessions.Remove(e.Id);
            if (_viewers.Remove(e.Id, out var closedViewer))
                closedViewer.CloseAfterSessionEnded();
            _collaborations.Remove(e.Id);
            // A previous session can finish its asynchronous UI notification after a new
            // session is already active. Never let that stale close tear down the new viewer,
            // indicator, collaboration controls, tray command, or presence state.
            if (_currentSession is { } currentSession && currentSession != e.Id)
            {
                _ = RefreshSessionHistoryAsync();
                return;
            }

            var replacement = _services?.Coordinator.ActiveSessions
                .FirstOrDefault(session => session.SessionId != e.Id);
            if (replacement is not null)
            {
                SelectActiveSession(replacement.SessionId);
                _ = RefreshSessionHistoryAsync();
                return;
            }

            var closedSession = _currentSessionInfo;
            PublishPresenceState(PresenceStateV1.Online);
            _currentSession = null;
            _currentSessionInfo = null;
            _collaboration = null;
            _controlRevoked = false;
            EndSessionButton.IsEnabled = false;
            RevokeControlButton.IsEnabled = false;
            if (e.Reason == SessionEndReason.PermissionDeclined
                && closedSession?.AccessKind == SessionAccessKind.Unattended)
            {
                const string declinedMessage =
                    "Password access was declined. Check the password and make sure the selected connection mode is allowed under Security > Unattended Access on the remote computer.";
                SessionText.Text = declinedMessage;
                ShowError(declinedMessage);
            }
            else
            {
                SessionText.Text = $"Session ended: {e.Reason}";
            }

            _indicator?.CloseAfterSessionEnded();
            _indicator = null;
            RestoreAfterSharing();
            RestoreAfterViewer();
            _viewer = null;

            SendFileButton.IsEnabled = false;
            SendFolderButton.IsEnabled = false;
            TransferPriorityPicker.IsEnabled = false;
            TrustCurrentButton.IsEnabled = false;
            CollaborationStatusText.Text = "No authorized input, file-transfer, or clipboard channel.";
            TransferList.Items.Clear();
            TransferList.Visibility = Visibility.Collapsed;
            TransferEmptyState.Visibility = Visibility.Visible;

            _tray?.SetActiveSession(false, "PeerOnQ - no active session");
            _ = RefreshSessionHistoryAsync();
        });
    }

    private void OnRemoteFrame(object? sender, SessionRemoteFrame notification)
    {
        if (_viewers.TryGetValue(notification.SessionId, out var viewer))
            viewer.Present(notification.Frame);
    }

    private void OnRemoteDisplays(object? sender, RemoteDisplaysNotification notification) =>
        _viewers.GetValueOrDefault(notification.SessionId)?
            .SetRemoteDisplays(notification.Displays, notification.ActiveDisplayId);

    private void SelectActiveSession(SessionId sessionId)
    {
        if (_services is null) return;
        var info = _services.Coordinator.ActiveSessions.FirstOrDefault(item => item.SessionId == sessionId);
        if (info is null) return;

        _currentSession = sessionId;
        _currentSessionInfo = info;
        _viewer = _viewers.GetValueOrDefault(sessionId);
        _collaboration = _collaborations.GetValueOrDefault(sessionId);
        _controlRevoked = false;
        EndSessionButton.IsEnabled = true;
        SessionText.Text = $"Active session: {info.PeerDisplayName} ({PeerOnQId.FormatMaskedDisplay(info.PeerMaskedId)}) - {DisplaySessionState(info)} - {info.Permissions}";
        _ = _services.SecurityAudit.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = SecurityAuditEventType.TechnicianSessionFocused,
            OccurredAt = DateTimeOffset.UtcNow,
            SessionId = sessionId.ToString(),
            PeerMaskedId = info.PeerMaskedId,
            PermissionSet = info.Permissions,
            Outcome = "focused",
        });
        if (_collaboration is { } context)
        {
            RefreshCollaborationAvailability(context);
            RefreshTransferList(Guid.Empty);
        }
    }

    private void PublishPresenceState(PresenceStateV1 state)
    {
        if (_services?.CloudPlatform is not { } cloudPlatform) return;
        _ = cloudPlatform.SetPresenceStateAsync(state);
    }

    private void OnUnattendedConnectionToggled(object sender, RoutedEventArgs e)
    {
        var unattended = UnattendedConnectionToggle.IsOn;
        if (unattended && SupportInvitationToggle.IsOn)
            SupportInvitationToggle.IsOn = false;
        RemoteUnattendedCredentialPanel.Visibility = unattended
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConnectionConsentText.Text = unattended
            ? "The remote computer must already have unattended access enabled for this mode. No Accept dialog is shown after the password or trusted-device proof succeeds."
            : "The remote owner chooses View Only or Full Control. Full Control includes screen control and file transfer.";
        if (!unattended)
            RemoteUnattendedPasswordBox.Password = string.Empty;
    }

    private void OnSupportInvitationToggled(object sender, RoutedEventArgs e)
    {
        var enabled = SupportInvitationToggle.IsOn;
        if (enabled && UnattendedConnectionToggle.IsOn)
            UnattendedConnectionToggle.IsOn = false;
        SupportInvitationConnectionPanel.Visibility = enabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        ConnectionConsentText.Text = enabled
            ? "The invitation is verified against expiry, revocation, use count, identity restrictions, and exact permissions. The remote owner must still press Accept."
            : "The remote owner chooses View Only or Full Control. Full Control includes screen control and file transfer.";
        if (!enabled)
        {
            SupportInvitationPasswordBox.Password = string.Empty;
            SupportInvitationLinkBox.Text = string.Empty;
        }
    }

    public void ApplySupportInvitationUri(string value)
    {
        if (!SupportInvitationLink.TryParse(value, out var invitation)) return;
        SupportInvitationLinkBox.Text = value;
        RemoteIdBox.Text = invitation.TargetDevice.Display;
        SupportInvitationToggle.IsOn = true;
        NavigateTo("Dashboard");
        StatusText.Text = "Support invitation loaded. Review the requested profile and select Start Session.";
    }

    internal void ActivateFromSecondaryLaunch(string? supportUri)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (Volatile.Read(ref _shutdownState) != 0) return;
            if (supportUri is not null) ApplySupportInvitationUri(supportUri);

            if (_viewer is not null)
            {
                _viewer.Activate();
                return;
            }

            if (_indicator is not null)
            {
                _indicator.Activate();
                return;
            }

            if (AppWindow.Presenter is OverlappedPresenter
                {
                    State: OverlappedPresenterState.Minimized,
                } presenter)
            {
                presenter.Restore();
            }
            if (_hiddenForViewer)
            {
                AppWindow.Show();
                _hiddenForViewer = false;
            }
            Activate();
        });
    }

    private (SessionMode Mode, SessionPermission Permissions, SessionAccessKind AccessKind, string? Password)
        BuildRequestedSessionScope()
    {
        var selectedMode = SessionMode.FullControl;
        var permissions = Phase1SessionScope.FullControlPermissions;
        if (!UnattendedConnectionToggle.IsOn)
            return (selectedMode, permissions, SessionAccessKind.Attended, null);

        var password = string.IsNullOrWhiteSpace(RemoteUnattendedPasswordBox.Password)
            ? null
            : RemoteUnattendedPasswordBox.Password;
        return (selectedMode, permissions, SessionAccessKind.Unattended, password);
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (DashboardPanel is null || args.SelectedItemContainer?.Tag is not string destination) return;

        DashboardPanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Collapsed;
        SessionsPanel.Visibility = Visibility.Collapsed;
        FileTransferPanel.Visibility = Visibility.Collapsed;
        AddressBookPanel.Visibility = Visibility.Collapsed;
        SecurityPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;

        var selectedPanel = destination switch
        {
            "Dashboard" => DashboardPanel,
            "Devices" => DevicesPanel,
            "Sessions" => SessionsPanel,
            "FileTransfer" => FileTransferPanel,
            "AddressBook" => AddressBookPanel,
            "Security" => SecurityPanel,
            "Settings" => SettingsPanel,
            _ => DashboardPanel,
        };
        selectedPanel.Visibility = Visibility.Visible;
        HeaderPageTitleText.Text = destination switch
        {
            "FileTransfer" => "File Transfer",
            "AddressBook" => "Address Book",
            _ => destination,
        };
        if (destination == "Sessions") _ = RefreshSessionHistoryAsync();
        if (destination == "Devices") _ = RefreshTrustedDevicesAsync();
        if (destination == "AddressBook") _ = RefreshAddressBookAsync();
        if (destination == "Security") _ = RefreshBlockedDevicesAsync();
    }

    private void OnGlobalSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = SearchDestinations
            .Where(page => page.Contains(sender.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private void OnGlobalSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        var destination = SearchDestinations.FirstOrDefault(page =>
            page.Equals(query, StringComparison.OrdinalIgnoreCase)
            || page.StartsWith(query, StringComparison.OrdinalIgnoreCase));
        if (destination is null)
        {
            StatusText.Text = "No PeerOnQ page matches that search.";
            return;
        }

        NavigateTo(destination.Replace(" ", string.Empty, StringComparison.Ordinal));
        sender.Text = destination;
    }

    private void NavigateTo(string destination)
    {
        var item = MainNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, destination, StringComparison.Ordinal));
        if (item is not null) MainNavigation.SelectedItem = item;
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        AppShell.RequestedTheme = AppShell.RequestedTheme == ElementTheme.Dark
            ? ElementTheme.Light
            : ElementTheme.Dark;
        ThemeIcon.Glyph = AppShell.RequestedTheme == ElementTheme.Dark ? "\uE708" : "\uE706";
    }

    private static string GreetingForCurrentTime() => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    private void OnCollaborationAvailable(object? sender, SessionCollaborationContext context)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _collaborations[context.SessionId] = context;
            _viewers.GetValueOrDefault(context.SessionId)?.AttachRemoteInput(context.RemoteInput);
            if (_currentSession == context.SessionId)
            {
                _collaboration = context;
                RefreshCollaborationAvailability(context);
            }
            context.ProtocolError += (_, reason) => _dispatcher.TryEnqueue(() =>
                ShowError($"The collaboration channel paused after a protocol error ({reason}). Reconnect before continuing."));
            context.Ready += (_, _) => _dispatcher.TryEnqueue(() =>
            {
                if (_currentSession == context.SessionId) RefreshCollaborationAvailability(context);
                _viewers.GetValueOrDefault(context.SessionId)?.EnableAuthorizedInput();
            });
            if (context.IsReady) _viewers.GetValueOrDefault(context.SessionId)?.EnableAuthorizedInput();

            if (context.FileTransfers is not null)
            {
                _viewers.GetValueOrDefault(context.SessionId)?.AttachFileTransfers(context.FileTransfers);
                context.FileTransfers.TransferChanged += (_, snapshot) => OnTransferChanged(context, snapshot);
                context.FileTransfers.IncomingOffer += (_, offer) => OnIncomingTransferOffer(context, offer);
                if (_currentSession == context.SessionId) RefreshTransferList(Guid.Empty);
            }
            if (context.Clipboard is not null)
            {
                context.Clipboard.Warning += (_, warning) => _dispatcher.TryEnqueue(() => ShowError(warning));
            }
        });
    }

    private void MinimizeForSharing()
    {
        if (_minimizedForSharing || AppWindow.Presenter is not OverlappedPresenter presenter
            || presenter.State == OverlappedPresenterState.Minimized) return;

        presenter.Minimize();
        _minimizedForSharing = true;
    }

    private void HideForViewer()
    {
        if (_hiddenForViewer) return;

        AppWindow.Hide();
        _hiddenForViewer = true;
    }

    private void OnViewerClosed(SessionId sessionId)
    {
        if (_viewers.Remove(sessionId, out var viewer) && ReferenceEquals(_viewer, viewer))
            _viewer = null;

        if (_viewers.Count == 0) RestoreAfterViewer();
    }

    private void RestoreAfterViewer()
    {
        if (!_hiddenForViewer) return;

        _hiddenForViewer = false;
        if (Volatile.Read(ref _shutdownState) != 0) return;

        AppWindow.Show();
        Activate();
    }

    private void RestoreAfterSharing()
    {
        if (!_minimizedForSharing) return;
        _minimizedForSharing = false;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
            Activate();
        }
    }

    private void OpenSettingsFromTray()
    {
        if (Volatile.Read(ref _shutdownState) != 0) return;

        _minimizedForSharing = false;
        if (AppWindow.Presenter is OverlappedPresenter
            {
                State: OverlappedPresenterState.Minimized,
            } presenter)
        {
            presenter.Restore();
        }

        NavigateTo("Settings");
        Activate();
    }

    private void RefreshCollaborationAvailability(SessionCollaborationContext context)
    {
        if (!ReferenceEquals(_collaboration, context)) return;
        var effectivePermissions = _controlRevoked
            ? context.Permissions & ~SessionPermission.ControlInput
            : context.Permissions;
        SendFileButton.IsEnabled = context.IsReady && context.FileTransfers is not null;
        SendFolderButton.IsEnabled = context.IsReady && context.FileTransfers is not null;
        TransferPriorityPicker.IsEnabled = context.IsReady && context.FileTransfers is not null;
        _updatingTransferPriority = true;
        TransferPriorityPicker.SelectedIndex = context.TransferPriorityMode switch
        {
            TransferPriorityMode.RemoteControlPriority => 0,
            TransferPriorityMode.FileTransferPriority => 2,
            _ => 1,
        };
        _updatingTransferPriority = false;
        TransferPriorityDescription.Text = DescribeTransferPriority(context.TransferPriorityMode);
        CollaborationStatusText.Text = context.IsReady
            ? $"Effective capabilities: {effectivePermissions}. Data uses the encrypted WebRTC channel."
            : $"Effective capabilities: {effectivePermissions}. Establishing the encrypted data channel...";
    }

    private void OnTransferPriorityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTransferPriority
            || _collaboration is null
            || TransferPriorityPicker.SelectedItem is not ComboBoxItem { Tag: string modeValue }
            || !Enum.TryParse<TransferPriorityMode>(modeValue, out var mode))
        {
            return;
        }

        _collaboration.SetTransferPriorityMode(mode);
        TransferPriorityDescription.Text = DescribeTransferPriority(mode);
    }

    private static string DescribeTransferPriority(TransferPriorityMode mode) => mode switch
    {
        TransferPriorityMode.RemoteControlPriority =>
            "Keeps the largest video and input safety margin; file traffic slows first.",
        TransferPriorityMode.FileTransferPriority =>
            "Uses the separate authenticated file relay when available; direct transfers use measured spare bandwidth after reserving live traffic.",
        _ => "Balances file speed with reserved live video, security, mouse, and keyboard traffic.",
    };

    private static string DisplaySessionState(ActiveSessionInfo info) =>
        info.State == SessionState.ConnectedViewOnly
            ? info.Mode switch
            {
                SessionMode.ViewOnly => "Connected - View Only",
                SessionMode.FullControl => "Connected - Full Control",
                SessionMode.FileTransferOnly => "Connected - File Transfer",
                _ => "Connected",
            }
            : info.State.ToString();

    private async void OnSendFile(object sender, RoutedEventArgs e)
    {
        if (_collaboration?.FileTransfers is not { } transfers) return;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null) await transfers.OfferAsync([file.Path]);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void OnSendFolder(object sender, RoutedEventArgs e)
    {
        if (_collaboration?.FileTransfers is not { } transfers) return;
        try
        {
            var folder = await PickFolderAsync();
            if (folder is not null) await transfers.OfferAsync([folder]);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private void OnTransferChanged(SessionCollaborationContext context, TransferSnapshot snapshot) =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_currentSession == context.SessionId) RefreshTransferList(snapshot.TransferId);
        });

    private void RefreshTransferList(Guid preferredId)
    {
        if (_collaboration?.FileTransfers is not { } transfers) return;
        TransferList.Items.Clear();
        foreach (var item in transfers.History.OrderByDescending(item => item.TransferId == preferredId))
        {
            var percent = item.TotalBytes == 0 ? 100 : (double)item.TransferredBytes / item.TotalBytes * 100;
            var direction = item.Incoming ? "Receive" : "Send";
            var speed = item.BytesPerSecond <= 0 ? "-" : $"{FormatBytes((long)item.BytesPerSecond)}/s";
            var remaining = item.Remaining is null
                ? "-"
                : item.Remaining.Value.TotalHours >= 1
                    ? item.Remaining.Value.ToString("hh\\:mm\\:ss")
                    : item.Remaining.Value.ToString("mm\\:ss");
            var row = new ListViewItem
            {
                Tag = item,
                Content = $"{direction}: {item.DisplayName} | {item.Status} | {percent:0.0}% | {speed} | ETA {remaining}",
            };
            TransferList.Items.Add(row);
            if (item.TransferId == preferredId) TransferList.SelectedItem = row;
        }
        var empty = TransferList.Items.Count == 0;
        TransferList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        TransferEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        var selected = SelectedTransfer();
        var active = selected?.Status is TransferStatus.Queued or TransferStatus.Transferring or TransferStatus.Paused;
        PauseTransferButton.IsEnabled = active && selected?.Status != TransferStatus.Paused;
        ResumeTransferButton.IsEnabled = selected?.Status == TransferStatus.Paused;
        CancelTransferButton.IsEnabled = active;
    }

    private TransferSnapshot? SelectedTransfer() =>
        (TransferList.SelectedItem as ListViewItem)?.Tag as TransferSnapshot;

    private async void OnPauseTransfer(object sender, RoutedEventArgs e)
    {
        if (_collaboration?.FileTransfers is { } service && SelectedTransfer() is { } item)
            await service.PauseAsync(item.TransferId);
    }

    private async void OnResumeTransfer(object sender, RoutedEventArgs e)
    {
        if (_collaboration?.FileTransfers is { } service && SelectedTransfer() is { } item)
            await service.ResumeAsync(item.TransferId);
    }

    private async void OnCancelTransfer(object sender, RoutedEventArgs e)
    {
        if (_collaboration?.FileTransfers is { } service && SelectedTransfer() is { } item)
            await service.CancelAsync(item.TransferId);
    }

    private void OnIncomingTransferOffer(SessionCollaborationContext context, TransferOffer offer) =>
        _dispatcher.TryEnqueue(async void () => await HandleIncomingTransferOfferAsync(context, offer));

    private async Task HandleIncomingTransferOfferAsync(SessionCollaborationContext context, TransferOffer offer)
    {
        if (context.FileTransfers is not { } service) return;

        // Full Control is approved once by the remote owner at session start and includes the
        // FileTransfer capability. Do not hide a second destination dialog behind the minimized
        // sharer window: receive directly into a dedicated, local Downloads folder. View-only and
        // standalone file-transfer sessions retain the explicit destination choice below.
        if (IsFullControlFileTransferSession(context.SessionId))
        {
            try
            {
                var destination = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads",
                    "PeerOnQ");
                Directory.CreateDirectory(destination);
                await service.AcceptAsync(offer.TransferId, destination, TransferCollisionPolicy.Rename);
                if (_currentSession == context.SessionId) RefreshTransferList(offer.TransferId);
                return;
            }
            catch (Exception ex)
            {
                try
                {
                    await service.RejectAsync(offer.TransferId, "automatic_destination_failed");
                }
                catch (Exception rejectError)
                {
                    _services?.LoggerFactory.CreateLogger<MainWindow>().LogDebug(
                        rejectError,
                        "Could not reject failed automatic file transfer {TransferId}",
                        offer.TransferId);
                }
                ShowError(ex.Message);
                return;
            }
        }

        await ShowIncomingTransferOfferAsync(context, offer);
    }

    private bool IsFullControlFileTransferSession(SessionId sessionId) =>
        _services?.Coordinator.ActiveSessions.Any(info =>
            info.SessionId == sessionId
            && info.Mode == SessionMode.FullControl
            && info.Permissions.HasFlag(SessionPermission.FileTransfer)) == true;

    private async Task ShowIncomingTransferOfferAsync(SessionCollaborationContext context, TransferOffer offer)
    {
        if (context.FileTransfers is not { } service || Content?.XamlRoot is not { } root) return;
        SelectActiveSession(context.SessionId);
        var collisionPicker = new ComboBox { SelectedIndex = 0 };
        collisionPicker.Items.Add(new ComboBoxItem { Content = "Rename if a file exists", Tag = TransferCollisionPolicy.Rename });
        collisionPicker.Items.Add(new ComboBoxItem { Content = "Overwrite existing files", Tag = TransferCollisionPolicy.Overwrite });
        collisionPicker.Items.Add(new ComboBoxItem { Content = "Skip existing files", Tag = TransferCollisionPolicy.Skip });
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"{offer.DisplayName}\n{offer.Entries.Count} entries, {FormatBytes(offer.TotalBytes)}",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(collisionPicker);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Incoming file transfer",
            Content = body,
            PrimaryButtonText = "Choose destination",
            SecondaryButtonText = "Reject",
            DefaultButton = ContentDialogButton.Secondary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            await service.RejectAsync(offer.TransferId);
            return;
        }
        var destination = await PickFolderAsync();
        if (destination is null)
        {
            await service.RejectAsync(offer.TransferId);
            return;
        }
        var policy = (collisionPicker.SelectedItem as ComboBoxItem)?.Tag as TransferCollisionPolicy?
                     ?? TransferCollisionPolicy.Rename;
        try { await service.AcceptAsync(offer.TransferId, destination, policy); }
        catch (Exception ex)
        {
            await service.RejectAsync(offer.TransferId, "local_validation_failed");
            ShowError(ex.Message);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.##} {units[index]}";
    }

    private async void OnAddressSearchChanged(object sender, TextChangedEventArgs e) => await RefreshAddressBookAsync();
    private async void OnAddressGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = (AddressGroupFilter.SelectedItem as ComboBoxItem)?.Tag is Guid;
        RenameGroupButton.IsEnabled = selected;
        RemoveGroupButton.IsEnabled = selected;
        await RefreshAddressBookAsync();
    }

    private void OnAddressSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = (AddressBookList.SelectedItem as ListViewItem)?.Tag is AddressBookDevice;
        EditAddressButton.IsEnabled = selected;
        RemoveAddressButton.IsEnabled = selected;
    }

    private async Task RefreshAddressBookAsync()
    {
        if (_services is null) return;
        try
        {
            var groupId = (AddressGroupFilter?.SelectedItem as ComboBoxItem)?.Tag as Guid?;
            var items = await _services.AddressBook.SearchAsync(AddressSearchBox?.Text, groupId);
            AddressBookList.Items.Clear();
            foreach (var item in items)
            {
                AddressBookList.Items.Add(new ListViewItem
                {
                    Tag = item.Device,
                    Content = $"{(item.Device.IsFavorite ? "Favorite · " : string.Empty)}{item.Device.DisplayName} | {item.Device.DeviceId.MaskedDisplay} | {item.Presence} | {string.Join(", ", item.Device.Tags)}",
                });
            }
            var empty = AddressBookList.Items.Count == 0;
            AddressBookList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            AddressBookEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EditAddressButton.IsEnabled = false;
            RemoveAddressButton.IsEnabled = false;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void OnSaveAddress(object sender, RoutedEventArgs e)
    {
        if (_services is null || !PeerOnQId.TryParse(RemoteIdBox.Text, out var id))
        {
            ShowError("Enter a valid remote PeerOnQ ID first.");
            return;
        }
        if (Content?.XamlRoot is not { } root) return;
        var existing = (await _services.AddressBook.SearchAsync())
            .Select(item => item.Device)
            .FirstOrDefault(device => device.DeviceId == id);
        await ShowAddressEditorAsync(id, existing, root);
    }

    private async Task ShowAddressEditorAsync(PeerOnQId id, AddressBookDevice? existing, XamlRoot root)
    {
        if (_services is null) return;
        var nameBox = new TextBox { Header = "Name", Text = existing?.DisplayName ?? id.MaskedDisplay };
        var tagsBox = new TextBox { Header = "Tags (comma-separated)", Text = string.Join(", ", existing?.Tags ?? []) };
        var notesBox = new TextBox { Header = "Notes", Text = existing?.Notes ?? string.Empty, AcceptsReturn = true, Height = 80 };
        var favorite = new ToggleSwitch { Header = "Favorite", IsOn = existing?.IsFavorite ?? false };
        var groupPicker = new ComboBox { Header = "Group", PlaceholderText = "No group" };
        foreach (var group in await _services.AddressBook.ListGroupsAsync())
        {
            var item = new ComboBoxItem { Content = group.Name, Tag = group.GroupId };
            groupPicker.Items.Add(item);
            if (existing?.GroupIds.Contains(group.GroupId) == true) groupPicker.SelectedItem = item;
        }
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(nameBox); body.Children.Add(tagsBox); body.Children.Add(notesBox); body.Children.Add(groupPicker); body.Children.Add(favorite);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = existing is null ? "Save device" : "Edit device",
            Content = body,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await _services.AddressBook.SaveDeviceAsync(new AddressBookDevice
            {
                RecordId = existing?.RecordId ?? Guid.NewGuid(),
                DeviceId = id,
                DisplayName = nameBox.Text,
                Tags = tagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Notes = notesBox.Text,
                IsFavorite = favorite.IsOn,
                GroupIds = (groupPicker.SelectedItem as ComboBoxItem)?.Tag is Guid groupId ? [groupId] : [],
            });
            await RefreshAddressBookAsync();
            await RefreshTrustedDevicesAsync();
            await RefreshDashboardSavedDevicesAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void OnEditAddress(object sender, RoutedEventArgs e)
    {
        if ((AddressBookList.SelectedItem as ListViewItem)?.Tag is not AddressBookDevice device
            || Content?.XamlRoot is not { } root) return;
        await ShowAddressEditorAsync(device.DeviceId, device, root);
    }

    private async void OnRemoveAddress(object sender, RoutedEventArgs e)
    {
        if (_services is null
            || (AddressBookList.SelectedItem as ListViewItem)?.Tag is not AddressBookDevice device
            || Content?.XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Remove saved device?",
            Content = "This removes the address-book entry only. A separate trusted-device record is not changed.",
            PrimaryButtonText = "Remove",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _services.AddressBook.RemoveDeviceAsync(device.RecordId);
            await RefreshAddressBookAsync();
            await RefreshDashboardSavedDevicesAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnAddressBookActivated(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if ((AddressBookList.SelectedItem as ListViewItem)?.Tag is not AddressBookDevice device) return;
        RemoteIdBox.Text = device.DeviceId.Display;
        StatusText.Text = $"Selected {device.DisplayName}.";
    }

    private async void OnCreateGroup(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        try
        {
            await _services.AddressBook.SaveGroupAsync(new AddressBookGroup
            {
                GroupId = Guid.NewGuid(),
                Name = NewGroupNameBox.Text,
            });
            NewGroupNameBox.Text = string.Empty;
            await RefreshGroupsAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void OnRenameGroup(object sender, RoutedEventArgs e)
    {
        if (_services is null
            || (AddressGroupFilter.SelectedItem as ComboBoxItem)?.Tag is not Guid groupId
            || Content?.XamlRoot is not { } root) return;
        var name = new TextBox
        {
            Header = "Group name",
            Text = (AddressGroupFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Rename group",
            Content = name,
            PrimaryButtonText = "Rename",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _services.AddressBook.SaveGroupAsync(new AddressBookGroup { GroupId = groupId, Name = name.Text });
            await RefreshGroupsAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void OnRemoveGroup(object sender, RoutedEventArgs e)
    {
        if (_services is null
            || (AddressGroupFilter.SelectedItem as ComboBoxItem)?.Tag is not Guid groupId
            || Content?.XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Delete group?",
            Content = "Saved devices remain in the address book and are removed only from this group.",
            PrimaryButtonText = "Delete",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _services.AddressBook.RemoveGroupAsync(groupId);
            await RefreshGroupsAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task RefreshGroupsAsync()
    {
        if (_services is null) return;
        var selected = (AddressGroupFilter.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        AddressGroupFilter.Items.Clear();
        AddressGroupFilter.Items.Add(new ComboBoxItem { Content = "All groups", Tag = null });
        foreach (var group in await _services.AddressBook.ListGroupsAsync())
            AddressGroupFilter.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group.GroupId });
        AddressGroupFilter.SelectedIndex = 0;
        if (selected is not null)
        {
            var match = AddressGroupFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is Guid id && id == selected);
            if (match is not null) AddressGroupFilter.SelectedItem = match;
        }
    }

    private async void OnTrustCurrent(object sender, RoutedEventArgs e)
    {
        if (_services is null || _currentSessionInfo is not { PeerKeyFingerprint: { } fingerprint } info
            || Content?.XamlRoot is not { } root) return;
        var expiry = new ComboBox { SelectedIndex = 0 };
        expiry.Items.Add(new ComboBoxItem { Content = "30 days", Tag = 30 });
        expiry.Items.Add(new ComboBoxItem { Content = "90 days", Tag = 90 });
        expiry.Items.Add(new ComboBoxItem { Content = "No expiry", Tag = 0 });
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = $"Trust {info.PeerDisplayName} for exactly these permissions: {info.Permissions}. A changed device key will revoke trust automatically.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(expiry);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Approve trusted device",
            Content = body,
            PrimaryButtonText = "Approve",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var days = (expiry.SelectedItem as ComboBoxItem)?.Tag is int selectedDays ? selectedDays : 30;
        try
        {
            await _services.TrustedDevices.ApproveAsync(
                info.PeerDeviceId ?? throw new InvalidOperationException("No verified peer identity is available."),
                info.PeerDisplayName,
                fingerprint,
                info.Permissions,
                days == 0 ? null : DateTimeOffset.UtcNow.AddDays(days),
                localUserConfirmed: true);
            StatusText.Text = "Trusted-device approval saved in encrypted local storage.";
            await RefreshTrustedDevicesAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void OnCreateSupportInvitation(object sender, RoutedEventArgs e)
    {
        if (_services is null || !_services.RoutingIdentityReady) return;
        try
        {
            var modeValue = (SupportInvitationModePicker.SelectedItem as ComboBoxItem)?.Tag as string;
            var lifetimeValue = (SupportInvitationLifetimePicker.SelectedItem as ComboBoxItem)?.Tag as string;
            if (!Enum.TryParse<SessionMode>(modeValue, out var mode)
                || !int.TryParse(lifetimeValue, out var lifetimeMinutes))
                throw new InvalidOperationException("Select a valid support invitation profile and expiry.");

            var issued = await _services.SupportInvitations.IssueAsync(
                _services.Identity.PublicId,
                new SupportInvitationRequest
                {
                    Mode = mode,
                    Permissions = SessionPermissionPolicy.ForMode(mode),
                    Lifetime = TimeSpan.FromMinutes(lifetimeMinutes),
                    MaximumUses = double.IsNaN(SupportInvitationMaxUsesBox.Value)
                        ? 1
                        : (int)Math.Clamp(SupportInvitationMaxUsesBox.Value, 1, 25),
                    Password = string.IsNullOrEmpty(SupportInvitationCreatePasswordBox.Password)
                        ? null
                        : SupportInvitationCreatePasswordBox.Password,
                    SupportNote = SupportInvitationNoteBox.Text,
                });
            _latestSupportInvitationUri = issued.OpenUri;
            SupportInvitationCreatePasswordBox.Password = string.Empty;
            CopySupportInvitationButton.IsEnabled = true;
            SupportInvitationStatusText.Text =
                $"Invitation created · expires {issued.ExpiresAt.ToLocalTime():g} · maximum {issued.MaximumUses} use(s). Copy the link now; the raw token is not stored.";
            await RefreshSupportInvitationsAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OnCopySupportInvitation(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_latestSupportInvitationUri)) return;
        var package = new DataPackage();
        package.SetText(_latestSupportInvitationUri);
        Clipboard.SetContent(package);
        SupportInvitationStatusText.Text = "Latest invitation link copied. Share its optional password through a separate channel.";
    }

    private async void OnRevokeSupportInvitation(object sender, RoutedEventArgs e)
    {
        if (_services is null
            || (SupportInvitationList.SelectedItem as ListViewItem)?.Tag is not SupportInvitationSummary selected)
            return;
        if (await _services.SupportInvitations.RevokeAsync(selected.InvitationId))
        {
            if (_latestSupportInvitationUri is not null)
            {
                _latestSupportInvitationUri = null;
                CopySupportInvitationButton.IsEnabled = false;
            }
            SupportInvitationStatusText.Text = "Invitation revoked. New connection attempts using it are rejected.";
            await RefreshSupportInvitationsAsync();
        }
    }

    private void OnSupportInvitationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = (SupportInvitationList.SelectedItem as ListViewItem)?.Tag as SupportInvitationSummary;
        RevokeSupportInvitationButton.IsEnabled = selected is { IsRevoked: false }
                                                    && selected.ExpiresAt > DateTimeOffset.UtcNow
                                                    && selected.UseCount < selected.MaximumUses;
    }

    private async Task RefreshSupportInvitationsAsync()
    {
        if (_services is null) return;
        var invitations = await _services.SupportInvitations.ListAsync();
        SupportInvitationList.Items.Clear();
        foreach (var invitation in invitations.Take(50))
        {
            var state = invitation.IsRevoked
                ? "Revoked"
                : invitation.ExpiresAt <= DateTimeOffset.UtcNow
                    ? "Expired"
                    : invitation.UseCount >= invitation.MaximumUses ? "Used" : "Active";
            SupportInvitationList.Items.Add(new ListViewItem
            {
                Tag = invitation,
                Content = $"{invitation.Mode} · {state} · {invitation.UseCount}/{invitation.MaximumUses} uses · expires {invitation.ExpiresAt.ToLocalTime():g}",
            });
        }
        RevokeSupportInvitationButton.IsEnabled = false;
    }

    private async void OnEnableUnattended(object sender, RoutedEventArgs e)
    {
        if (App.IsPortableSupport)
        {
            ShowError("Unattended access is unavailable in Portable Support mode.");
            return;
        }
        if (_services is null || Content?.XamlRoot is not { } root) return;
        var password = string.IsNullOrWhiteSpace(UnattendedPasswordBox.Password)
            ? null
            : UnattendedPasswordBox.Password;
        if (password is null && !TrustedDeviceAuthCheck.IsOn)
        {
            ShowError("Enter a strong unattended password or explicitly allow approved trusted devices.");
            UnattendedPasswordBox.Focus(FocusState.Programmatic);
            return;
        }

        var allowedPermissions = SelectedUnattendedPermissions();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Enable unattended access?",
            Content = $"A connecting computer can request {DescribeUnattendedPermissions(allowedPermissions)} without a person accepting the session dialog after its password/recovery proof or separately approved trusted-device proof succeeds. PeerOnQ still verifies device identity, rate limits failures, records audit events, and shows a persistent session indicator. This does not bypass Windows UAC or elevation prompts.",
            PrimaryButtonText = "I understand, enable",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var result = await _services.UnattendedAccess.EnableAsync(new UnattendedSetupRequest
            {
                LocalUserConfirmed = true,
                ExplanationAcknowledged = true,
                StrongPassword = password,
                DeviceAuthenticationEnabled = TrustedDeviceAuthCheck.IsOn,
                AllowedPermissions = allowedPermissions,
            });
            UnattendedPasswordBox.Password = string.Empty;
            await ShowRecoveryCodesAsync(result.RecoveryCodes);
            await RefreshUnattendedStatusAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task ShowRecoveryCodesAsync(IReadOnlyList<string> codes)
    {
        if (Content?.XamlRoot is not { } root) return;
        var codesBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, codes),
            IsReadOnly = true,
            AcceptsReturn = true,
            Height = 230,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Save recovery codes now",
            Content = codesBox,
            CloseButtonText = "I saved them",
        };
        await dialog.ShowAsync();
    }

    private async void OnDisableUnattended(object sender, RoutedEventArgs e)
    {
        if (_services is null || Content?.XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Disable unattended access?",
            Content = "The unattended password and recovery codes will be revoked. Existing trusted-device approvals remain visible under Devices, but cannot connect unattended while this setting is disabled.",
            PrimaryButtonText = "Disable and revoke credentials",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _services.UnattendedAccess.DisableAsync(localUserConfirmed: true);
            await RefreshUnattendedStatusAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task RefreshUnattendedStatusAsync()
    {
        if (_services is null) return;
        if (App.IsPortableSupport)
        {
            UnattendedStatusText.Text = "Unavailable · Portable Support uses an ephemeral profile and requires explicit remote approval.";
            UnattendedPasswordBox.Password = string.Empty;
            UnattendedPasswordBox.IsEnabled = false;
            UnattendedAllowedModePicker.IsEnabled = false;
            TrustedDeviceAuthCheck.IsEnabled = false;
            EnableUnattendedButton.IsEnabled = false;
            DisableUnattendedButton.IsEnabled = false;
            return;
        }
        var status = await _services.UnattendedAccess.GetStatusAsync();
        if (status.Enabled)
        {
            var authentication = (status.PasswordConfigured, status.TrustedDeviceAuthenticationEnabled) switch
            {
                (true, true) => "password/recovery code or approved trusted device",
                (true, false) => "password or recovery code",
                _ => "approved trusted device or recovery code",
            };
            var lockout = status.LockedUntil is { } lockedUntil && lockedUntil > DateTimeOffset.UtcNow
                ? $" Authentication is locked until {lockedUntil.ToLocalTime():t}."
                : string.Empty;
            UnattendedStatusText.Text =
                $"Enabled · {DescribeUnattendedPermissions(status.AllowedPermissions)} · {authentication}. Active sessions remain visibly indicated.{lockout}";
        }
        else
        {
            UnattendedStatusText.Text = "Disabled · all unattended requests are rejected; attended connections still show Accept.";
        }

        SelectUnattendedPermissions(status.AllowedPermissions);
        TrustedDeviceAuthCheck.IsOn = status.TrustedDeviceAuthenticationEnabled;
        UnattendedPasswordBox.IsEnabled = !status.Enabled;
        UnattendedAllowedModePicker.IsEnabled = !status.Enabled;
        TrustedDeviceAuthCheck.IsEnabled = !status.Enabled;
        EnableUnattendedButton.IsEnabled = !status.Enabled;
        DisableUnattendedButton.IsEnabled = status.Enabled;
    }

    private SessionPermission SelectedUnattendedPermissions()
    {
        var mode = (UnattendedAllowedModePicker.SelectedItem as ComboBoxItem)?.Tag as string;
        return mode switch
        {
            "FullControl" => SessionPermissionPolicy.ForMode(SessionMode.FullControl),
            "FileTransferOnly" => SessionPermissionPolicy.ForMode(SessionMode.FileTransferOnly),
            _ => SessionPermissionPolicy.ForMode(SessionMode.ViewOnly),
        };
    }

    private void SelectUnattendedPermissions(SessionPermission permissions)
    {
        UnattendedAllowedModePicker.SelectedIndex = permissions switch
        {
            SessionPermission.ViewScreen | SessionPermission.ControlInput => 1,
            SessionPermission.FileTransfer => 2,
            _ => 0,
        };
    }

    private static string DescribeUnattendedPermissions(SessionPermission permissions) => permissions switch
    {
        SessionPermission.ViewScreen | SessionPermission.ControlInput => "Full Control",
        SessionPermission.FileTransfer => "File Transfer",
        _ => "View Only",
    };

    private static bool TryGetSessionMode(SessionPermission permissions, out SessionMode mode)
    {
        if (permissions == SessionPermissionPolicy.ForMode(SessionMode.ViewOnly))
        {
            mode = SessionMode.ViewOnly;
            return true;
        }
        if (permissions == SessionPermissionPolicy.ForMode(SessionMode.FullControl))
        {
            mode = SessionMode.FullControl;
            return true;
        }
        if (permissions == SessionPermissionPolicy.ForMode(SessionMode.FileTransferOnly))
        {
            mode = SessionMode.FileTransferOnly;
            return true;
        }

        mode = default;
        return false;
    }

    private async void OnAddDevice(object sender, RoutedEventArgs e)
    {
        if (_services is null || Content?.XamlRoot is not { } root) return;

        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = false };
        var nameBox = new TextBox
        {
            Header = "Device name (alias)",
            PlaceholderText = "For example, Office Desktop",
            MaxLength = 80,
        };
        var idBox = new TextBox
        {
            Header = "PeerOnQ ID",
            PlaceholderText = "000-000-000-000",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            MaxLength = 15,
        };
        idBox.TextChanged += OnRemoteIdTextChanged;
        var body = new StackPanel { Spacing = 12, MinWidth = 430 };
        body.Children.Add(error);
        body.Children.Add(new TextBlock
        {
            Text = "Save a device locally for quick access. This does not grant trust or unattended access.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
        });
        body.Children.Add(nameBox);
        body.Children.Add(idBox);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = AppShell.RequestedTheme,
            Title = "Add Device",
            Content = body,
            PrimaryButtonText = "Save Device",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Microsoft.UI.Xaml.Application.Current.Resources["PeerOnQDialogPrimaryButtonStyle"],
        };
        foreach (var resourceKey in new[]
                 {
                     "SystemAccentColor",
                     "SystemControlHighlightAccentBrush",
                     "AccentFillColorDefaultBrush",
                     "AccentFillColorSecondaryBrush",
                     "AccentFillColorTertiaryBrush",
                     "AccentTextFillColorPrimaryBrush",
                     "AccentButtonBackground",
                     "AccentButtonBackgroundPointerOver",
                     "AccentButtonBackgroundPressed",
                     "AccentButtonForeground",
                     "AccentButtonForegroundPointerOver",
                     "AccentButtonForegroundPressed",
                     "TextControlBorderBrushFocused",
                     "TextControlElevationBorderFocusedBrush",
                 })
            dialog.Resources[resourceKey] = Microsoft.UI.Xaml.Application.Current.Resources[resourceKey];
        dialog.PrimaryButtonClick += (dialogSender, args) =>
        {
            var hasValidId = PeerOnQId.TryParse(idBox.Text, out _);
            var message = string.IsNullOrWhiteSpace(nameBox.Text)
                ? "Enter a device name."
                : !hasValidId
                    ? "Enter a valid PeerOnQ ID in 000-000-000-000 format."
                    : null;
            if (message is not null)
            {
                args.Cancel = true;
                error.Message = message;
                error.IsOpen = true;
                if (string.IsNullOrWhiteSpace(nameBox.Text)) nameBox.Focus(FocusState.Programmatic);
                else idBox.Focus(FocusState.Programmatic);
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var deviceId = PeerOnQId.Parse(idBox.Text);
            var existing = (await _services.AddressBook.SearchAsync())
                .Select(item => item.Device)
                .FirstOrDefault(device => device.DeviceId == deviceId);
            var saved = await _services.AddressBook.SaveDeviceAsync(new AddressBookDevice
            {
                RecordId = existing?.RecordId ?? Guid.NewGuid(),
                DeviceId = deviceId,
                DisplayName = nameBox.Text,
                IsFavorite = existing?.IsFavorite ?? false,
                Tags = existing?.Tags ?? [],
                Notes = existing?.Notes,
                LastSeenAt = existing?.LastSeenAt,
                GroupIds = existing?.GroupIds ?? [],
            });
            await RefreshTrustedDevicesAsync();
            await RefreshAddressBookAsync();
            await RefreshDashboardSavedDevicesAsync();
            RemoteIdBox.Text = saved.DeviceId.Display;
            StatusText.Text = $"Saved {saved.DisplayName}. It is ready in Connect to Remote Device; trust was not changed.";
        }
        catch (Exception ex)
        {
            ShowError($"The device could not be saved: {ex.Message}");
        }
    }

    private async Task RefreshTrustedDevicesAsync()
    {
        if (_services is null) return;
        var query = TrustedDeviceSearchBox?.Text?.Trim();
        var savedDevices = (await _services.AddressBook.SearchAsync()).Select(item => item.Device).ToArray();
        var trustRecords = await _services.TrustedDevices.ListAsync();
        var trustById = trustRecords.ToDictionary(device => device.DeviceId);
        var devices = savedDevices
            .Select(device => new DeviceDirectoryRow(device, trustById.GetValueOrDefault(device.DeviceId)))
            .Concat(trustRecords
                .Where(trust => trust.TrustState == DeviceTrustState.Trusted
                                && savedDevices.All(device => device.DeviceId != trust.DeviceId))
                .Select(trust => new DeviceDirectoryRow(null, trust)))
            .OrderByDescending(device => device.LastActivityAt ?? DateTimeOffset.MinValue)
            .ThenBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(query))
        {
            devices = devices.Where(device =>
                    device.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || device.DeviceId.Display.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || device.OperatingSystem.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || device.TrustLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        TrustedDeviceList.Items.Clear();
        foreach (var device in devices)
        {
            TrustedDeviceList.Items.Add(new ListViewItem
            {
                Tag = device,
                Content = $"{device.DisplayName} | {device.DeviceId.Display} | {device.ActivityLabel} | {device.TrustLabel}",
            });
        }
        TrustedDeviceCountText.Text = $"{devices.Length} {(devices.Length == 1 ? "device" : "devices")}";
        var empty = devices.Length == 0;
        TrustedDeviceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        TrustedDeviceEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EditDeviceButton.IsEnabled = false;
        DeleteDeviceButton.IsEnabled = false;
        RevokeTrustedButton.IsEnabled = false;
    }

    private async Task RefreshDashboardSavedDevicesAsync()
    {
        if (_services is null) return;

        var devices = (await _services.AddressBook.SearchAsync())
            .Select(item => item.Device)
            .OrderBy(device => device.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.DeviceId.Display, StringComparer.Ordinal)
            .ToArray();

        _updatingSavedDevicePicker = true;
        try
        {
            SavedDevicePicker.Items.Clear();
            foreach (var device in devices)
            {
                SavedDevicePicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{device.DisplayName} | {device.DeviceId.Display}",
                    Tag = device,
                });
            }
            SavedDevicePicker.Visibility = devices.Length == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        finally
        {
            _updatingSavedDevicePicker = false;
        }

        SynchronizeSavedDevicePicker(RemoteIdBox.Text);
    }

    private void OnSavedDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSavedDevicePicker
            || (SavedDevicePicker.SelectedItem as ComboBoxItem)?.Tag is not AddressBookDevice device)
            return;

        RemoteIdBox.Text = device.DeviceId.Display;
        StatusText.Text = $"Selected {device.DisplayName}. Choose a connection mode and start the session.";
    }

    private void SynchronizeSavedDevicePicker(string remoteId)
    {
        if (_updatingSavedDevicePicker) return;

        ComboBoxItem? matchingItem = null;
        if (PeerOnQId.TryParse(remoteId, out var deviceId))
        {
            matchingItem = SavedDevicePicker.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is AddressBookDevice device && device.DeviceId == deviceId);
        }

        if (ReferenceEquals(SavedDevicePicker.SelectedItem, matchingItem)) return;
        _updatingSavedDevicePicker = true;
        try
        {
            SavedDevicePicker.SelectedItem = matchingItem;
        }
        finally
        {
            _updatingSavedDevicePicker = false;
        }
    }

    private async void OnTrustedDeviceSearchChanged(object sender, TextChangedEventArgs e) =>
        await RefreshTrustedDevicesAsync();

    private void OnTrustedDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = (TrustedDeviceList.SelectedItem as ListViewItem)?.Tag as DeviceDirectoryRow;
        EditDeviceButton.IsEnabled = row?.SavedDevice is not null;
        DeleteDeviceButton.IsEnabled = row is not null;
        RevokeTrustedButton.IsEnabled = row?.TrustRecord?.TrustState == DeviceTrustState.Trusted;
    }

    private async void OnEditDevice(object sender, RoutedEventArgs e)
    {
        if ((TrustedDeviceList.SelectedItem as ListViewItem)?.Tag is not DeviceDirectoryRow
            {
                SavedDevice: { } device,
            }
            || Content?.XamlRoot is not { } root) return;

        await ShowAddressEditorAsync(device.DeviceId, device, root);
    }

    private async void OnDeleteDevice(object sender, RoutedEventArgs e)
    {
        if (_services is null
            || (TrustedDeviceList.SelectedItem as ListViewItem)?.Tag is not DeviceDirectoryRow device
            || Content?.XamlRoot is not { } root) return;

        var revokesTrust = device.TrustRecord?.TrustState == DeviceTrustState.Trusted;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Delete this device?",
            Content = revokesTrust
                ? "This removes the device from Devices and revokes its trusted-device permissions. Session history is retained."
                : "This removes the device from Devices. Session history is retained.",
            PrimaryButtonText = "Delete device",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        DeleteDeviceButton.IsEnabled = false;
        try
        {
            if (revokesTrust)
                await _services.TrustedDevices.RevokeAsync(device.DeviceId);
            if (device.SavedDevice is not null)
                await _services.AddressBook.RemoveDeviceAsync(device.SavedDevice.RecordId);
            await RefreshTrustedDevicesAsync();
            await RefreshAddressBookAsync();
            await RefreshDashboardSavedDevicesAsync();
            StatusText.Text = $"Removed {device.DeviceId.MaskedDisplay} from Devices.";
        }
        catch (Exception ex)
        {
            _services.LoggerFactory.CreateLogger<MainWindow>().LogWarning(
                ex,
                "Failed to delete device {Device}",
                device.DeviceId.Masked);
            DeleteDeviceButton.IsEnabled = true;
            ShowError("The selected device could not be deleted. Try again.");
        }
    }

    private async void OnRevokeTrusted(object sender, RoutedEventArgs e)
    {
        if (_services is null
            || (TrustedDeviceList.SelectedItem as ListViewItem)?.Tag is not DeviceDirectoryRow
            {
                TrustRecord: { } trust,
            }) return;
        await _services.TrustedDevices.RevokeAsync(trust.DeviceId);
        await RefreshTrustedDevicesAsync();
        StatusText.Text = "Trusted device revoked immediately.";
    }

    private async Task RecordConnectedDeviceAsync(ActiveSessionInfo session)
    {
        if (_services is null || session.PeerDeviceId is not { } deviceId) return;

        try
        {
            await _services.AddressBook.RecordConnectedDeviceAsync(
                deviceId,
                session.PeerDisplayName,
                DateTimeOffset.UtcNow);
            await RefreshTrustedDevicesAsync();
            await RefreshAddressBookAsync();
            await RefreshDashboardSavedDevicesAsync();
        }
        catch (Exception ex)
        {
            _autoSavedSessions.Remove(session.SessionId);
            _services.LoggerFactory.CreateLogger<MainWindow>().LogWarning(
                ex,
                "Failed to record connected device {Device}",
                deviceId.Masked);
        }
    }

    private async Task RefreshBlockedDevicesAsync()
    {
        if (_services is null) return;

        BlockedDevicesStatusBar.IsOpen = false;
        IReadOnlyList<PeerOnQId> blockedDevices;
        try
        {
            blockedDevices = await _services.BlockedDevices.ListAsync();
        }
        catch (Exception ex)
        {
            _services.LoggerFactory.CreateLogger<MainWindow>().LogWarning(
                ex,
                "Failed to load blocked devices");
            BlockedDeviceList.Visibility = Visibility.Collapsed;
            BlockedDeviceEmptyText.Visibility = Visibility.Collapsed;
            BlockedDeviceCountText.Text = "Blocked devices could not be loaded.";
            BlockedDevicesStatusBar.Severity = InfoBarSeverity.Error;
            BlockedDevicesStatusBar.Message = "The blocked-device list could not be loaded. Try opening Security again.";
            BlockedDevicesStatusBar.IsOpen = true;
            return;
        }

        BlockedDeviceList.Items.Clear();
        foreach (var deviceId in blockedDevices)
        {
            var row = new Grid
            {
                ColumnSpacing = 16,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var details = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(new TextBlock { Text = deviceId.Display, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            details.Children.Add(new TextBlock
            {
                Text = "Incoming connection requests are declined automatically.",
                TextWrapping = TextWrapping.Wrap,
            });

            var unblockButton = new Button
            {
                Content = "Unblock",
                Tag = deviceId,
                VerticalAlignment = VerticalAlignment.Center,
            };
            unblockButton.Click += OnUnblockDevice;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                unblockButton,
                $"Unblock device {deviceId.Display}");

            Grid.SetColumn(details, 0);
            Grid.SetColumn(unblockButton, 1);
            row.Children.Add(details);
            row.Children.Add(unblockButton);
            BlockedDeviceList.Items.Add(new ListViewItem
            {
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsTabStop = false,
            });
        }

        var empty = blockedDevices.Count == 0;
        BlockedDeviceCountText.Text = empty
            ? "Devices blocked from requesting a session will appear here."
            : $"{blockedDevices.Count} blocked {(blockedDevices.Count == 1 ? "device" : "devices")}.";
        BlockedDeviceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        BlockedDeviceEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnUnblockDevice(object sender, RoutedEventArgs e)
    {
        if (_services is null || sender is not Button { Tag: PeerOnQId deviceId } button) return;

        var dialog = new ContentDialog
        {
            Title = "Unblock this device?",
            Content = $"Device {deviceId.Display} will be allowed to request attended sessions again. Every connection will still require your approval.",
            PrimaryButtonText = "Unblock",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        button.IsEnabled = false;
        try
        {
            await _services.BlockedDevices.UnblockAsync(deviceId);
            await RefreshBlockedDevicesAsync();
            BlockedDevicesStatusBar.Severity = InfoBarSeverity.Success;
            BlockedDevicesStatusBar.Message = $"Device {deviceId.Display} was unblocked.";
            BlockedDevicesStatusBar.IsOpen = true;
            StatusText.Text = $"Device {deviceId.MaskedDisplay} can request attended sessions again.";
        }
        catch (Exception ex)
        {
            _services.LoggerFactory.CreateLogger<MainWindow>().LogWarning(
                ex,
                "Failed to unblock device {Device}",
                deviceId.Masked);
            button.IsEnabled = true;
            BlockedDevicesStatusBar.Severity = InfoBarSeverity.Error;
            BlockedDevicesStatusBar.Message = "The device could not be unblocked. Try again.";
            BlockedDevicesStatusBar.IsOpen = true;
        }
    }

    private void OnQualitySelected(object sender, SelectionChangedEventArgs e)
        => ApplySelectedQualityProfile();

    private void ApplySelectedQualityProfile()
    {
        if (_services is null
            || QualityPicker.SelectedItem is not ComboBoxItem { Tag: string qualityValue }
            || ResolutionPicker.SelectedItem is not ComboBoxItem { Tag: string resolutionValue })
        {
            return;
        }

        if (Enum.TryParse<QualityProfile>(qualityValue, out var quality)
            && Enum.TryParse<CaptureResolution>(resolutionValue, out var resolution))
        {
            _services.Coordinator.Profile = MediaProfile.For(quality) with { Resolution = resolution };
        }
    }

    private async Task RefreshPrivacyAndAuditAsync()
    {
        if (_services is null) return;
        var settings = await _services.PrivacySettings.GetAsync();
        _updatingPrivacyControls = true;
        CrashReportingToggle.IsOn = settings.CrashReportingEnabled;
        for (var index = 0; index < AuditRetentionPicker.Items.Count; index++)
        {
            if (AuditRetentionPicker.Items[index] is ComboBoxItem { Tag: string tag }
                && int.TryParse(tag, out var days)
                && days == settings.AuditRetentionDays)
            {
                AuditRetentionPicker.SelectedIndex = index;
                break;
            }
        }
        if (AuditRetentionPicker.SelectedIndex < 0) AuditRetentionPicker.SelectedIndex = 1;
        _updatingPrivacyControls = false;

        var integrity = await _services.SecurityAudit.VerifyIntegrityAsync();
        AuditIntegrityText.Text = integrity.IsValid
            ? $"Security audit integrity verified ({integrity.VerifiedRecords} chained records)."
            : $"SECURITY AUDIT INTEGRITY FAILURE: {integrity.Failure}";
    }

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        if (_services?.Updates is null)
        {
            ShowError("This build has no trusted release update configuration. Install an official signed build to enable updates.");
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.IsEnabled = false;
        _availableUpdate = null;
        try
        {
            UpdateStatusText.Text = "Checking the signed update manifest over HTTPS…";
            var result = await _services.Updates.CheckAsync();
            _availableUpdate = result.Status is UpdateCheckStatus.Available or UpdateCheckStatus.Required
                ? result.Update
                : null;
            DownloadUpdateButton.IsEnabled = _availableUpdate is not null;
            UpdateStatusText.Text = result.Status switch
            {
                UpdateCheckStatus.Available => $"Version {result.Update!.Version} is available for this device.",
                UpdateCheckStatus.Required => $"Security update {result.Update!.Version} is required. Permission scope and security settings will not change.",
                UpdateCheckStatus.DeferredByRollout => "A newer release exists but is not yet assigned to this staged-rollout device.",
                UpdateCheckStatus.NoUpdate => "PeerOnQ is up to date.",
                _ => $"Update rejected ({result.RejectionReason}): {result.Detail}",
            };
            if (result.Status == UpdateCheckStatus.Rejected) ShowError(UpdateStatusText.Text);
        }
        catch (Exception ex)
        {
            ShowError($"Update check failed: {ex.Message}");
            UpdateStatusText.Text = "Update check failed. No package was downloaded or executed.";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void OnDownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (_services?.Updates is null || _availableUpdate is null) return;
        DownloadUpdateButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                UpdateProgressBar.Value = value.Percentage;
                UpdateStatusText.Text = $"Downloading verified update: {value.Percentage:F0}%";
            });
            _stagedUpdate = await _services.Updates.DownloadAsync(_availableUpdate, progress);
            InstallUpdateButton.IsEnabled = true;
            UpdateStatusText.Text = "Download complete. SHA-256, signed manifest, Authenticode chain and publisher identity verified.";
        }
        catch (Exception ex)
        {
            _stagedUpdate = null;
            InstallUpdateButton.IsEnabled = false;
            ShowError($"Update download rejected: {ex.Message}");
            UpdateStatusText.Text = "The downloaded package was rejected and will not be executed.";
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_services?.Updates is null || _stagedUpdate is null) return;
        if (_currentSession is not null)
        {
            ShowError("End the active remote session before installing an update.");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Install verified PeerOnQ update?",
            Content = "Windows Installer will open visibly. PeerOnQ will then close. You can cancel in the installer; no silent installation or startup persistence is created.",
            PrimaryButtonText = "Open installer and close PeerOnQ",
            SecondaryButtonText = "Later",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await _services.Updates.LaunchInstallerAsync(_stagedUpdate);
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex)
        {
            ShowError($"Installer launch rejected: {ex.Message}");
        }
    }

    private async void OnCrashReportingToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingPrivacyControls || _services is null) return;
        await _services.PrivacySettings.SetCrashReportingEnabledAsync(CrashReportingToggle.IsOn);
        StatusText.Text = CrashReportingToggle.IsOn
            ? "Sanitized local crash reports enabled with consent. No report is uploaded automatically."
            : "Crash reporting disabled. No new crash report will be created.";
    }

    private async void OnAuditRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPrivacyControls || _services is null
            || AuditRetentionPicker.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !int.TryParse(tag, out var days)) return;
        await _services.PrivacySettings.SetAuditRetentionDaysAsync(days);
        await _services.SecurityAudit.ApplyRetentionAsync(TimeSpan.FromDays(days));
        await RefreshPrivacyAndAuditAsync();
    }

    private async void OnExportAudit(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"peeronq-security-audit-{DateTime.UtcNow:yyyyMMdd}",
        };
        picker.FileTypeChoices.Add("Sanitized JSON Lines", new List<string> { ".jsonl" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await _services.SecurityAudit.ExportSanitizedJsonLinesAsync(file.Path);
            StatusText.Text = "Sanitized security audit exported.";
            await RefreshPrivacyAndAuditAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Audit export failed: {ex.Message}");
        }
    }

    private async void OnExportDiagnostics(object sender, RoutedEventArgs e)
    {
        if (_services is null || !await ConfirmDiagnosticsAsync("Export sanitized diagnostics?")) return;
        try
        {
            DiagnosticsUploadStatusText.Text = "Creating a bounded sanitized diagnostics archive…";
            var bundle = await _services.DiagnosticBundles.CreateAsync(CreateDiagnosticBundleRequest());
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"peeronq-diagnostics-{bundle.DiagnosticId:N}",
            };
            picker.FileTypeChoices.Add("Sanitized diagnostics ZIP", new List<string> { ".zip" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var destination = await picker.PickSaveFileAsync();
            if (destination is null)
            {
                DiagnosticsUploadStatusText.Text = "The sanitized archive remains in the local diagnostics folder.";
                return;
            }

            await Task.Run(() => File.Copy(bundle.FilePath, destination.Path, overwrite: true));
            DiagnosticsUploadStatusText.Text =
                $"Sanitized diagnostics exported. Reference {bundle.DiagnosticId:N}; expires locally after review policy cleanup.";
        }
        catch (Exception ex)
        {
            DiagnosticsUploadStatusText.Text = "Diagnostics export failed; no data was uploaded.";
            ShowError($"Diagnostics export failed: {ex.Message}");
        }
    }

    private async void OnRunNetworkDoctor(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;

        RunNetworkDoctorButton.IsEnabled = false;
        NetworkDoctorDetailsButton.IsEnabled = false;
        NetworkDoctorStatusText.Text = "Running bounded connectivity checks…";
        try
        {
            _lastNetworkDoctorReport = await _services.NetworkDoctor.RunAsync(_shutdownCancellation.Token);
            NetworkDoctorStatusText.Text = _lastNetworkDoctorReport.ToUserSummary();
            NetworkDoctorDetailsButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            NetworkDoctorStatusText.Text = "Network Doctor stopped because PeerOnQ is closing.";
        }
        catch (Exception ex)
        {
            NetworkDoctorStatusText.Text = "Network Doctor could not complete. No network addresses were displayed.";
            _services.LoggerFactory.CreateLogger<MainWindow>()
                .LogWarning(ex, "Network Doctor failed with {ErrorType}", ex.GetType().Name);
        }
        finally
        {
            RunNetworkDoctorButton.IsEnabled = _services is not null;
        }
    }

    private async void OnOpenNetworkDoctorDetails(object sender, RoutedEventArgs e)
    {
        if (_lastNetworkDoctorReport is null) return;

        var details = new TextBox
        {
            Text = _lastNetworkDoctorReport.ToSanitizedTechnicalJson(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 640,
            MaxHeight = 480,
        };
        var dialog = new ContentDialog
        {
            Title = "Network Doctor technical details",
            Content = new ScrollViewer { Content = details, MaxHeight = 500 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OnSendDiagnostics(object sender, RoutedEventArgs e)
    {
        if (_services?.CloudPlatform is null)
        {
            ShowError("This build has no fixed cloud diagnostics endpoint. Use local export instead.");
            return;
        }
        if (!await ConfirmDiagnosticsAsync("Send sanitized diagnostics to PeerOnQ support?")) return;

        SendDiagnosticsButton.IsEnabled = false;
        try
        {
            DiagnosticsUploadStatusText.Text = "Creating and sanitizing the approved diagnostics bundle…";
            var bundle = await _services.DiagnosticBundles.CreateAsync(CreateDiagnosticBundleRequest());
            DiagnosticsUploadStatusText.Text = "Uploading the approved archive with a short-lived token…";
            var status = await _services.CloudPlatform.UploadDiagnosticsAsync(
                bundle,
                new DiagnosticCreateRequestV1(
                    _services.Identity.InternalId,
                    ConsentGranted: true,
                    IncludedCategories: ["sanitized_logs", "client_environment", "webrtc_summary", "failure_codes", "update_status", "schema_version", "health_checks"],
                    AppVersion: typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown",
                    OsVersion: Environment.OSVersion.VersionString,
                    Architecture: CloudPlatformClient.CurrentArchitecture,
                    ErrorId: null,
                    IssueCategory: "user_submitted",
                    DatabaseSchemaVersion: PeerOnQDatabase.SchemaVersion));
            DiagnosticsUploadStatusText.Text =
                $"Diagnostics sent with consent. Reference {status.ReferenceCode ?? status.DiagnosticId.ToString("N")}; expires {status.ExpiresAtUtc:u}.";
        }
        catch (Exception ex)
        {
            DiagnosticsUploadStatusText.Text = "Diagnostics upload failed; the local bundle was not silently retried.";
            ShowError($"Diagnostics upload failed: {ex.Message}");
        }
        finally
        {
            SendDiagnosticsButton.IsEnabled = _services?.CloudPlatform is not null;
        }
    }

    private async Task<bool> ConfirmDiagnosticsAsync(string title)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = "Included: sanitized PeerOnQ logs, app/OS/version, address-free WebRTC statistics, failure codes, update status, database schema version, and client health checks.\n\nExcluded: screen and clipboard content, keystrokes, mouse input, file names or contents, personal documents, passwords, tokens, private keys, raw crash memory, full Device ID, and network addresses.",
            PrimaryButtonText = "I understand and approve",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private DiagnosticBundleRequest CreateDiagnosticBundleRequest()
    {
        var statistics = _services is not null && _currentSession is { } sessionId
            ? _services.Coordinator.StatisticsOf(sessionId)
            : MediaStatistics.Empty;
        var healthChecks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["local_database"] = "ready",
            ["signaling"] = _services?.Signaling.State.ToString() ?? "unknown",
            ["cloud"] = _services?.CloudPlatform?.State.ToString() ?? "not_configured",
            ["session_connection_path"] = statistics.ConnectionPath.ToString(),
            ["session_connection_health"] = statistics.ConnectionHealth.ToString(),
            ["session_reconnect_state"] = statistics.ReconnectState.ToString(),
            ["session_quality_profile"] = statistics.ActiveQualityProfile?.ToString() ?? "unknown",
            ["session_quality_reason"] = statistics.QualityChangeReason ?? "unknown",
            ["session_transfer_priority"] = statistics.TransferPriorityMode.ToString(),
        };
        if (_lastNetworkDoctorReport is { } doctor)
        {
            foreach (var probe in doctor.Probes.Take(20))
                healthChecks[$"network_{probe.Code}"] = probe.Status.ToString();
        }

        return new DiagnosticBundleRequest
        {
            UserConsented = true,
            AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown",
            UpdateStatus = _services?.Updates is null ? "not_configured" : "configured",
            DatabaseSchemaVersion = PeerOnQDatabase.SchemaVersion,
            ConnectionFailureCodes = string.IsNullOrWhiteSpace(statistics.LastInterruptionReason)
                ? []
                : [statistics.LastInterruptionReason],
            WebRtcStatistics = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["rttMs"] = statistics.RttMs,
                ["packetLossPercent"] = statistics.PacketLossPercent,
                ["jitterMs"] = statistics.JitterMs,
                ["bitrateKbps"] = statistics.CurrentBitrateKbps,
                ["availableOutgoingBitrateKbps"] = statistics.AvailableOutgoingBitrateKbps,
                ["captureFps"] = statistics.CaptureFps,
                ["encodeFps"] = statistics.EncodeFps,
                ["decodeFps"] = statistics.DecodeFps,
                ["renderFps"] = statistics.RenderFps,
                ["captureToEncodeP50Ms"] = statistics.CaptureToEncodeLatencyP50Ms,
                ["captureToEncodeP95Ms"] = statistics.CaptureToEncodeLatencyP95Ms,
                ["captureToEncodeP99Ms"] = statistics.CaptureToEncodeLatencyP99Ms,
                ["decodeToRenderP50Ms"] = statistics.DecodeToRenderLatencyP50Ms,
                ["decodeToRenderP95Ms"] = statistics.DecodeToRenderLatencyP95Ms,
                ["decodeToRenderP99Ms"] = statistics.DecodeToRenderLatencyP99Ms,
                ["captureToPresentP50Ms"] = statistics.CaptureToPresentLatencyP50Ms,
                ["captureToPresentP95Ms"] = statistics.CaptureToPresentLatencyP95Ms,
                ["captureToPresentP99Ms"] = statistics.CaptureToPresentLatencyP99Ms,
                ["frameAgeClockUncertaintyMs"] = statistics.FrameAgeClockUncertaintyMs,
                ["inputToInjectionP50Ms"] = statistics.InputToInjectionLatencyP50Ms,
                ["inputToInjectionP95Ms"] = statistics.InputToInjectionLatencyP95Ms,
                ["inputToInjectionP99Ms"] = statistics.InputToInjectionLatencyP99Ms,
                ["inputClockUncertaintyMs"] = statistics.InputClockUncertaintyMs,
                ["inputDataLaneNegotiated"] = statistics.InputDataLaneNegotiated ? 1 : 0,
                ["inputDataLaneReady"] = statistics.InputDataLaneReady ? 1 : 0,
                ["inputDataRecordsSent"] = statistics.InputDataRecordsSent,
                ["bulkDataLaneNegotiated"] = statistics.BulkDataLaneNegotiated ? 1 : 0,
                ["bulkDataLaneReady"] = statistics.BulkDataLaneReady ? 1 : 0,
                ["nativeBulkTransportNegotiated"] = statistics.NativeBulkTransportNegotiated ? 1 : 0,
                ["nativeBulkTransportReady"] = statistics.NativeBulkTransportReady ? 1 : 0,
                ["nativeBulkBudgetKbps"] = statistics.NativeBulkBudgetKbps,
                ["nativeBulkGoodputKbps"] = statistics.NativeBulkGoodputKbps,
                ["nativeBulkFeedbackSamples"] = statistics.NativeBulkFeedbackSamples,
                ["sctpAssociationBufferedBytes"] = statistics.SctpAssociationBufferedBytes,
                ["interactiveSctpBufferedBytes"] = statistics.InteractiveSctpBufferedBytes,
                ["inputSctpBufferedBytes"] = statistics.InputSctpBufferedBytes,
                ["bulkSctpBufferedBytes"] = statistics.BulkSctpBufferedBytes,
                ["bulkDataFragmentBytes"] = statistics.BulkDataFragmentBytes,
                ["bulkQueueBudgetBytes"] = statistics.BulkQueueBudgetBytes,
                ["bulkDataRecordsSent"] = statistics.BulkDataRecordsSent,
                ["bulkDataFragmentsSent"] = statistics.BulkDataFragmentsSent,
                ["targetFps"] = statistics.TargetFps,
                ["targetBitrateKbps"] = statistics.TargetBitrateKbps,
                ["encoderQueueDepth"] = statistics.EncoderQueueDepth,
                ["sourceWidth"] = statistics.SourceWidth,
                ["sourceHeight"] = statistics.SourceHeight,
                ["requestedWidth"] = statistics.RequestedWidth,
                ["requestedHeight"] = statistics.RequestedHeight,
                ["encodedWidth"] = statistics.EncodedWidth,
                ["encodedHeight"] = statistics.EncodedHeight,
                ["decodedWidth"] = statistics.DecodedWidth,
                ["decodedHeight"] = statistics.DecodedHeight,
                ["renderedWidth"] = statistics.RenderedWidth,
                ["renderedHeight"] = statistics.RenderedHeight,
                ["framesCaptured"] = statistics.FramesCaptured,
                ["framesEncoded"] = statistics.FramesEncoded,
                ["framesRendered"] = statistics.FramesRendered,
                ["droppedFrames"] = statistics.FramesDropped,
                ["bytesSent"] = statistics.BytesSent,
                ["bytesReceived"] = statistics.BytesReceived,
                ["reconnectCount"] = statistics.ReconnectAttempts,
            },
            HealthChecks = healthChecks,
            SessionTimeline = _services is not null && _lastSessionForDiagnostics is { } timelineSessionId
                ? _services.Coordinator.TimelineOf(timelineSessionId)
                : [],
        };
    }

    private async void OnClearAudit(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var dialog = new ContentDialog
        {
            Title = "Clear local security audit?",
            Content = "This permanently removes local audit history. A new chained record will document the confirmed clear operation.",
            PrimaryButtonText = "Clear audit",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Secondary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _services.SecurityAudit.ClearAsync(confirmed: true);
        await RefreshPrivacyAndAuditAsync();
        StatusText.Text = "Local security audit cleared by confirmed user action.";
    }

    private void RefreshPerformanceStatus()
    {
        if (_services is null) return;
        var sample = _services.Performance.Sample();
        PerformanceStatusText.Text =
            $"Measured startup {App.StartupElapsed.TotalMilliseconds:F0} ms | CPU {sample.CpuPercent:F1}% | working set {sample.WorkingSetBytes / 1024d / 1024:F1} MiB | private {sample.PrivateMemoryBytes / 1024d / 1024:F1} MiB | managed heap {sample.ManagedHeapBytes / 1024d / 1024:F1} MiB";
    }

    private void ShowError(string message)
    {
        DashboardErrorBar.Message = message;
        DashboardErrorBar.IsOpen = true;
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (Volatile.Read(ref _shutdownState) == 2) return;

        // WinUI does not wait for async Window.Closed handlers. Cancel the first close, finish the
        // peer notification/resource cleanup, then close once more without cancellation.
        args.Cancel = true;
        if (Interlocked.CompareExchange(ref _shutdownState, 1, 0) != 0) return;

        _performanceTimer.Stop();
        _signalingRetryTimer.Stop();
        _shutdownCancellation.Cancel();
        try
        {
            await _initializationTask;
            // Closing the window must not leave capture/input running or the peer session open.
            if (_services is not null)
            {
                await _services.DisposeAsync();
                _services = null;
                App.Services = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PeerOnQ shutdown cleanup failed: {ex.GetType().Name}");
        }
        finally
        {
            _tray?.Dispose();
            _tray = null;
            App.CleanupPortableSupportData();
            Interlocked.Exchange(ref _shutdownState, 2);
            Close();
        }
    }
}
