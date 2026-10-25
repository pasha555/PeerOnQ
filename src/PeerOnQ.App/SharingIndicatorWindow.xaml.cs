using PeerOnQ.Application.Sessions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.Graphics;

namespace PeerOnQ.App;

/// <summary>
/// The always-visible proof that a capture is running. It shows the active capability and how
/// long it has been running. It stays on top, is not closable by the peer, and keeps a small
/// direct local end control even when its detailed capability controls are collapsed.
/// </summary>
public sealed partial class SharingIndicatorWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly Func<Task> _onStop;
    private readonly Func<Task> _onRevokeControl;
    private readonly Func<bool>? _isVoiceAvailable;
    private readonly Func<bool>? _isVoiceEnabled;
    private readonly Func<bool, bool>? _onSetVoice;
    private readonly DateTimeOffset _startedAt;
    private bool _allowClose;
    private int _stopRequested;
    private int _revokeRequested;

    public SharingIndicatorWindow(
        ActiveSessionInfo info,
        Func<Task> onStop,
        Func<Task> onRevokeControl,
        Func<bool>? isVoiceAvailable = null,
        Func<bool>? isVoiceEnabled = null,
        Func<bool, bool>? onSetVoice = null)
    {
        InitializeComponent();

        _onStop = onStop;
        _onRevokeControl = onRevokeControl;
        _isVoiceAvailable = isVoiceAvailable;
        _isVoiceEnabled = isVoiceEnabled;
        _onSetVoice = onSetVoice;
        _startedAt = info.StartedAt;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        var unattended = info.AccessKind == PeerOnQ.Domain.Sessions.SessionAccessKind.Unattended;
        SetViewerText(info.Permissions.HasFlag(PeerOnQ.Domain.Sessions.SessionPermission.ControlInput)
            ? unattended ? "Unattended remote control" : "Remote control active"
            : info.Permissions.HasFlag(PeerOnQ.Domain.Sessions.SessionPermission.ViewScreen)
                ? unattended ? "Unattended screen sharing" : "Screen sharing active"
                : "PeerOnQ session active");
        var canRevokeControl = info.Permissions.HasFlag(
            PeerOnQ.Domain.Sessions.SessionPermission.ControlInput)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RevokeButton.Visibility = canRevokeControl;
        Title = info.AccessKind == PeerOnQ.Domain.Sessions.SessionAccessKind.Unattended
            ? "PeerOnQ unattended session is active"
            : "PeerOnQ session is active";

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }

        // Keep the consent signal at the top center of the physical sharer's display. It is
        // excluded from capture but stays directly actionable on the local machine.
        SetExpanded(false);
        ExcludeFromCapture();

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            UpdateDuration();
            UpdateVoice();
        };
        _timer.Start();

        UpdateDuration();
        UpdateVoice();
        Closed += (_, _) => _timer.Stop();
        AppWindow.Closing += OnAppWindowClosing;
    }

    private void ExcludeFromCapture()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        if (!SetWindowDisplayAffinity(windowHandle, WdaExcludeFromCapture))
        {
            // Never remove the owner's local safety indicator if capture exclusion is unavailable.
            // The warning records only the Win32 error code and contains no session/user data.
            Trace.TraceWarning(
                "PeerOnQ sharing indicator capture exclusion failed with Win32 error {0}.",
                Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Viewer self-test: shows the expanded panel, including the voice toggle.</summary>
    internal void ExpandForSelfTest()
    {
        SetExpanded(true);
        UpdateVoice();
    }

    private void SetExpanded(bool expanded)
    {
        CompactPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        MoveToTopCenter(expanded ? 820 : 320, expanded ? 64 : 48);
    }

    private void MoveToTopCenter(int width, int height)
    {
        AppWindow.Resize(new SizeInt32(width, height));
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        AppWindow.Move(new PointInt32(x, workArea.Y + 16));
    }

    /// <summary>The voice toggle is shown only while the viewer offers the encrypted voice lane.</summary>
    private void UpdateVoice()
    {
        var enabled = _isVoiceEnabled?.Invoke() == true;
        var available = enabled || _isVoiceAvailable?.Invoke() == true;
        VoiceToggle.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        VoiceToggle.IsChecked = enabled;
        var status = AudioStatus?.Invoke();
        AudioStatusText.Text = status ?? string.Empty;
        CompactAudioIcon.Visibility = AudioActive?.Invoke() == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>In-session indicator text: sound on, microphone on, the viewer talking, or why audio is off.</summary>
    internal Func<string?>? AudioStatus { get; init; }

    /// <summary>True while system sound or voice is active (compact indicator icon).</summary>
    internal Func<bool>? AudioActive { get; init; }

    private void OnVoiceToggled(object sender, RoutedEventArgs e)
    {
        var requested = VoiceToggle.IsChecked == true;
        _onSetVoice?.Invoke(requested);
        VoiceToggle.IsChecked = _isVoiceEnabled?.Invoke() == true;
    }

    private void SetViewerText(string value)
    {
        ViewerText.Text = value;
        DetailViewerText.Text = value;
    }

    private void UpdateDuration()
    {
        var elapsed = DateTimeOffset.UtcNow - _startedAt;
        var value = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        DurationText.Text = value;
        DetailDurationText.Text = value;
    }

    private void OnExpand(object sender, RoutedEventArgs e)
    {
        SetExpanded(true);
    }

    private void OnCollapse(object sender, RoutedEventArgs e)
    {
        SetExpanded(false);
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        await RequestStopAsync();
    }

    private async void OnRevoke(object sender, RoutedEventArgs e)
    {
        await RequestRevokeAsync();
    }

    private async Task RequestRevokeAsync()
    {
        if (Interlocked.Exchange(ref _revokeRequested, 1) != 0) return;

        RevokeButton.IsEnabled = false;
        try
        {
            await _onRevokeControl();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "PeerOnQ sharing indicator could not revoke control ({0}).",
                ex.GetType().Name);
            RevokeButton.IsEnabled = true;
            Interlocked.Exchange(ref _revokeRequested, 0);
        }
    }

    public void MarkControlRevoked()
    {
        RevokeButton.IsEnabled = false;
        RevokeButton.Visibility = Visibility.Collapsed;
        SetViewerText("Screen sharing active");
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;

        args.Cancel = true;
        _ = RequestStopAsync();
    }

    private async Task RequestStopAsync()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;

        StopButton.IsEnabled = false;
        CompactStopButton.IsEnabled = false;
        try
        {
            await _onStop();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "PeerOnQ sharing indicator could not end the session ({0}).",
                ex.GetType().Name);
            StopButton.IsEnabled = true;
            CompactStopButton.IsEnabled = true;
            Interlocked.Exchange(ref _stopRequested, 0);
        }
    }

    public void CloseAfterSessionEnded()
    {
        _allowClose = true;
        Close();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);

}
