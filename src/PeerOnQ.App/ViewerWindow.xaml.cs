using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Sessions;
using PeerOnQ.Domain.Sessions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinRT;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;
using WinRT.Interop;

namespace PeerOnQ.App;

/// <summary>
/// Renders the remote screen.
///
/// Pointer and keyboard input are captured only when the accepted session includes ControlInput
/// and the remote owner explicitly accepted the full-control request. Full Control starts the
/// approved input channel automatically; recovery after a safety release remains available from
/// the secondary command menu.
/// </summary>
public sealed partial class ViewerWindow : Window
{
    private enum DisplayScaleMode
    {
        Fit,
        Fill,
        Stretch,
        ActualSize,
    }

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _statsTimer;
    private readonly DispatcherQueueTimer _fullScreenRevealTimer;
    private readonly DispatcherQueueTimer _transferDismissTimer;
    private readonly Func<MediaStatistics> _statistics;
    private readonly Func<IReadOnlyList<SessionTimelineEntry>> _timeline;
    private readonly SessionMode _mode;
    private readonly Func<Task> _onEnd;
    private RemoteInputSession? _remoteInput;
    private FileTransferService? _fileTransfers;
    private readonly Action<PresentedVideoFrame>? _onRendered;
    private readonly Func<string, Task>? _onSelectDisplay;
    private readonly ILogger _log;
    private readonly SessionId _sessionId;
    private readonly Action<SessionId>? _onActivated;
    private readonly Action<SessionId>? _onClosed;
    private readonly Lock _frameGate = new();
    private readonly HashSet<RemotePointerButton> _pressedRemoteButtons = [];
    private readonly HashSet<ushort> _pressedRemoteKeys = [];
    private bool _updatingMonitorPicker;
    private DisplayScaleMode _displayScaleMode = DisplayScaleMode.Fit;

    private WriteableBitmap? _bitmap;
    private PendingVideoFrame? _pendingFrame;
    private XamlRoot? _remoteImageRoot;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private double _lastPointerX;
    private double _lastPointerY;
    private bool _hasLastPointer;
    private bool _renderQueued;
    private bool _isFullScreen;
    private string? _renderFailure;
    private volatile bool _freezeFrames;
    private bool _sessionConnected;
    private bool _initialActivationPending = true;
    private bool _suppressFilePasteKeyUp;
    private Guid? _displayedTransferId;
    private bool _closed;
    private int _endRequested;
    private int _pointerEventLogged;
    private int _pointerNormalizedLogged;
    private int _pointerSentLogged;

    public ViewerWindow(
        ActiveSessionInfo info,
        Func<MediaStatistics> statistics,
        Func<Task> onEnd,
        RemoteInputSession? remoteInput = null,
        FileTransferService? fileTransfers = null,
        Func<string, Task>? onSelectDisplay = null,
        Action<PresentedVideoFrame>? onRendered = null,
        Func<IReadOnlyList<SessionTimelineEntry>>? timeline = null,
        ILogger<ViewerWindow>? logger = null,
        Action<SessionId>? onActivated = null,
        Action<SessionId>? onClosed = null)
    {
        InitializeComponent();
        ApplyFitLayout();

        _statistics = statistics;
        _sessionId = info.SessionId;
        _onActivated = onActivated;
        _onClosed = onClosed;
        _timeline = timeline ?? (() => []);
        _mode = info.Mode;
        _onEnd = onEnd;
        _remoteInput = remoteInput;
        _onSelectDisplay = onSelectDisplay;
        _onRendered = onRendered;
        _log = logger ?? NullLogger<ViewerWindow>.Instance;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _transferDismissTimer = _dispatcher.CreateTimer();
        _transferDismissTimer.Interval = TimeSpan.FromSeconds(2);
        _transferDismissTimer.Tick += (_, _) =>
        {
            _transferDismissTimer.Stop();
            if (_closed) return;

            TransferProgressContainer.Visibility = Visibility.Collapsed;
            _displayedTransferId = null;
        };

        Title = $"PeerOnQ viewer - {info.PeerDisplayName}";
        PeerText.Text = $"{info.PeerDisplayName} ({info.PeerMaskedId})";
        ConnectionText.Text = DisplayConnectionState(info.State);
        _sessionConnected = info.State == SessionState.ConnectedViewOnly;
        FileTransferButton.Visibility = _mode == SessionMode.FullControl
            ? Visibility.Visible
            : Visibility.Collapsed;
        AttachFileTransfers(fileTransfers);
        if (_remoteInput is not null)
            _remoteInput.ControlPermissionChanged += OnControlPermissionChanged;
        Activated += OnViewerActivated;

        _statsTimer = _dispatcher.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromSeconds(1);
        _statsTimer.Tick += (_, _) => UpdateStatistics();
        _statsTimer.Start();

        _fullScreenRevealTimer = _dispatcher.CreateTimer();
        _fullScreenRevealTimer.Interval = TimeSpan.FromSeconds(1.5);
        _fullScreenRevealTimer.Tick += (_, _) =>
        {
            _fullScreenRevealTimer.Stop();
            if (_isFullScreen) FullScreenRevealBar.Visibility = Visibility.Collapsed;
        };

        Closed += async (_, _) =>
        {
            _closed = true;
            _onClosed?.Invoke(_sessionId);
            if (_remoteInput is not null)
                _remoteInput.ControlPermissionChanged -= OnControlPermissionChanged;
            if (_fileTransfers is not null)
                _fileTransfers.TransferChanged -= OnFileTransferChanged;
            _freezeFrames = true;
            ClearPendingFrame();
            _statsTimer.Stop();
            _fullScreenRevealTimer.Stop();
            _transferDismissTimer.Stop();
            // This method clears the local forwarding flag before its first await. Keep its
            // best-effort ReleaseAll send concurrent so a wedged data channel cannot delay the
            // signaling end request or leave the UI accepting more input.
            var disableInput = DisableRemoteInputCaptureAsync();
            if (Interlocked.Exchange(ref _endRequested, 1) == 0)
            {
                await _onEnd();
            }
            await disableInput;
        };
    }

    /// <summary>
    /// Closes the viewer after the coordinator has already received the peer's session-end event.
    /// This prevents a programmatic cleanup close from sending a duplicate end request while a
    /// title-bar close still ends the live session for both devices.
    /// </summary>
    public void CloseAfterSessionEnded()
    {
        Interlocked.Exchange(ref _endRequested, 1);
        if (!_closed) Close();
    }

    /// <summary>
    /// The encrypted collaboration channel can become ready just after the connected-state UI has
    /// created this viewer. Attach its file-transfer service then, rather than leaving drag/drop
    /// and clipboard-file paste permanently unavailable for the rest of the session.
    /// </summary>
    public void AttachFileTransfers(FileTransferService? fileTransfers)
    {
        if (fileTransfers is null)
        {
            UpdateFileTransferAvailability();
            return;
        }

        if (ReferenceEquals(_fileTransfers, fileTransfers))
        {
            UpdateFileTransferAvailability();
            return;
        }

        if (_fileTransfers is not null)
        {
            _log.LogWarning("Ignoring a second file-transfer service for viewer {SessionId}", _sessionId);
            return;
        }

        _fileTransfers = fileTransfers;
        _fileTransfers.TransferChanged += OnFileTransferChanged;
        UpdateFileTransferAvailability();
    }

    /// <summary>
    /// Binds the input channel when secure collaboration becomes ready after the viewer window
    /// has already been created from the media-connected state.
    /// </summary>
    public void AttachRemoteInput(RemoteInputSession? remoteInput)
    {
        if (remoteInput is null || ReferenceEquals(_remoteInput, remoteInput)) return;

        // A viewer is session-bound. Do not replace an already attached input session with an
        // unexpected later context, because that could forward input across session boundaries.
        if (_remoteInput is not null)
        {
            _log.LogWarning("Ignoring a second remote-input session for viewer {SessionId}", _sessionId);
            return;
        }

        _remoteInput = remoteInput;
        _remoteInput.ControlPermissionChanged += OnControlPermissionChanged;
    }

    /// <summary>Called from the media thread for every decoded frame.</summary>
    public void Present(RemoteVideoFrame frame)
    {
        if (_freezeFrames || frame.Format != RemotePixelFormat.Bgr24) return;

        var pending = new PendingVideoFrame(
            frame.Width,
            frame.Height,
            TakeFramePixels(frame),
            frame.PipelineDecodedTimestamp,
            frame.CaptureTimestampUnixMicroseconds,
            frame.CaptureSequenceNumber,
            frame.PeerClockEstimate);
        var enqueue = false;
        lock (_frameGate)
        {
            if (_freezeFrames) return;
            _pendingFrame = pending;
            if (!_renderQueued)
            {
                _renderQueued = true;
                enqueue = true;
            }
        }

        if (enqueue && !_dispatcher.TryEnqueue(RenderPendingFrame))
        {
            ClearPendingFrame();
        }
    }

    private void RenderPendingFrame()
    {
        PendingVideoFrame? frame;
        lock (_frameGate)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
        }

        if (!_freezeFrames && frame is not null)
        {
            try
            {
                if (_bitmap is null || _bitmapWidth != frame.Width || _bitmapHeight != frame.Height)
                {
                    _bitmap = new WriteableBitmap(frame.Width, frame.Height);
                    _bitmapWidth = frame.Width;
                    _bitmapHeight = frame.Height;
                    RemoteImage.Source = _bitmap;
                    if (_displayScaleMode == DisplayScaleMode.ActualSize)
                        ApplyActualSizeDimensions();
                }

                WriteBgr24AsBgra32(_bitmap, frame.Pixels);
                _bitmap.Invalidate();
                _renderFailure = null;
                _onRendered?.Invoke(new PresentedVideoFrame(
                    frame.DecodedTimestamp,
                    frame.Width,
                    frame.Height,
                    frame.CaptureTimestampUnixMicroseconds,
                    frame.CaptureSequenceNumber,
                    frame.PeerClockEstimate));
            }
            catch (Exception ex)
            {
                // A dropped frame must never take the viewer window down, but a frame that
                // never draws must not look like a working session either.
                _renderFailure = ex.Message;
            }
        }

        var enqueue = false;
        lock (_frameGate)
        {
            if (_freezeFrames)
            {
                _pendingFrame = null;
                _renderQueued = false;
            }
            else if (_pendingFrame is not null)
            {
                enqueue = true;
            }
            else
            {
                _renderQueued = false;
            }
        }

        if (enqueue && !_dispatcher.TryEnqueue(RenderPendingFrame))
        {
            ClearPendingFrame();
        }
    }

    private void ClearPendingFrame()
    {
        lock (_frameGate)
        {
            _pendingFrame = null;
            _renderQueued = false;
        }
    }

    /// <summary>
    /// Expands packed BGR24 into the bitmap's BGRA32 back buffer.
    ///
    /// The buffer is reached through IBufferByteAccess. It must be obtained with the CsWinRT
    /// cast: a plain C# cast on the projected IBuffer does not query the COM interface and
    /// throws, which previously left the viewer showing a black rectangle while the
    /// statistics happily counted decoded frames.
    /// </summary>
    private static unsafe void WriteBgr24AsBgra32(WriteableBitmap bitmap, byte[] bgr24)
    {
        var access = bitmap.PixelBuffer.As<IBufferByteAccess>();
        access.Buffer(out var destination);

        var capacity = (int)bitmap.PixelBuffer.Capacity;
        var pixelCount = Math.Min(bgr24.Length / 3, capacity / 4);
        if (pixelCount == 0) return;

        // One unaligned 32-bit read plus one 32-bit write replaces four byte-at-a-time writes.
        // The final pixel stays scalar so the overlapping read can never cross the source array.
        fixed (byte* source = bgr24)
        {
            var packedPixels = pixelCount - 1;
            for (var pixel = 0; pixel < packedPixels; pixel++)
            {
                var color = Unsafe.ReadUnaligned<uint>(source + (pixel * 3));
                Unsafe.WriteUnaligned(
                    destination + (pixel * 4),
                    (color & 0x00FFFFFFu) | 0xFF000000u);
            }

            var lastSource = packedPixels * 3;
            var lastTarget = destination + (packedPixels * 4);
            lastTarget[0] = source[lastSource];
            lastTarget[1] = source[lastSource + 1];
            lastTarget[2] = source[lastSource + 2];
            lastTarget[3] = 255;
        }
    }

    /// <summary>
    /// Shows the displays the sharer offers. Selecting one only sends a request; the sharer
    /// decides whether to switch and can only pick among its own screens.
    /// </summary>
    public void SetRemoteDisplays(IReadOnlyList<CaptureTargetInfo> displays, string activeDisplayId)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _updatingMonitorPicker = true;

            try
            {
                MonitorPicker.Items.Clear();

                foreach (var display in displays)
                {
                    MonitorPicker.Items.Add(new ComboBoxItem
                    {
                        Content = $"{display.DisplayName} ({display.Width}x{display.Height})",
                        Tag = display.Id,
                    });
                }

                var activeIndex = displays
                    .Select((display, index) => (display, index))
                    .FirstOrDefault(entry => entry.display.Id == activeDisplayId).index;

                if (MonitorPicker.Items.Count > 0)
                {
                    MonitorPicker.SelectedIndex = activeIndex;
                }

                MonitorPicker.IsEnabled = _onSelectDisplay is not null && displays.Count > 1;
                MonitorContainer.Visibility = displays.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _updatingMonitorPicker = false;
            }
        });
    }

    private async void OnMonitorSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingMonitorPicker || _onSelectDisplay is null) return;

        if (MonitorPicker.SelectedItem is ComboBoxItem { Tag: string displayId })
        {
            var inputWasEnabled = _remoteInput?.IsLocallyEnabled == true;
            if (inputWasEnabled) await DisableRemoteInputCaptureAsync();
            await _onSelectDisplay(displayId);
            if (inputWasEnabled)
            {
                try
                {
                    await EnableRemoteInputCaptureAsync();
                }
                catch (Exception ex)
                {
                    await HandleInputFailureAsync(ex);
                }
            }
        }
    }

    private void UpdateStatistics()
    {
        var stats = _statistics();

        var dimensions = stats.RenderedWidth > 0
            ? $"{stats.RenderedWidth}×{stats.RenderedHeight}"
            : stats.Width > 0 ? $"{stats.Width}×{stats.Height}" : "Connecting…";
        var measuredFps = stats.RenderFps > 0 ? stats.RenderFps : stats.CurrentFps;
        var measuredBitrate = stats.CurrentBitrateKbps > 0
            ? $"{stats.CurrentBitrateKbps / 1000:0.0} Mbps"
            : "measuring bitrate";
        QualityText.Text = _renderFailure is null
            ? $"{dimensions} · {measuredFps:0} FPS · {measuredBitrate}"
            : $"render error: {_renderFailure}";
        AutomationProperties.SetItemStatus(RemoteImage, QualityText.Text);
        AutomationProperties.SetItemStatus(RemoteInputSurface, QualityText.Text);
        var path = stats.ConnectionPath switch
        {
            ConnectionPath.DirectLan => "Direct LAN",
            ConnectionPath.DirectInternet => "Direct internet",
            ConnectionPath.Relayed => "Relayed",
            _ => "Unknown / negotiating",
        };

        var mode = _mode == SessionMode.FullControl && _remoteInput?.IsLocallyEnabled == true
            ? "Full Control active"
            : _mode == SessionMode.FullControl ? "Full Control paused" : "View Only";
        var profile = stats.ActiveQualityProfile switch
        {
            QualityProfile.LowBandwidth => "Low Bandwidth",
            QualityProfile.Quality => "High Quality",
            { } value => value.ToString(),
            null => "Profile pending",
        };
        ConnectionText.Text = $"{mode} · {stats.ConnectionHealth} · {path} · {profile}";

        var codec = stats.EncoderName ?? stats.DecoderName ?? "codec pending";
        var qualityReason = stats.QualityChangeReason ?? "awaiting_measurements";
        var technicalStatus = $"{ConnectionText.Text}. {QualityText.Text}. Codec {codec}. Quality reason {qualityReason}.";
        AutomationProperties.SetHelpText(RemoteImage, technicalStatus);
        AutomationProperties.SetHelpText(RemoteInputSurface, technicalStatus);
        var reconnecting = stats.ReconnectState is not (ReconnectState.Connected or ReconnectState.Ended);
        ReconnectOverlay.Visibility = reconnecting ? Visibility.Visible : Visibility.Collapsed;
        ReconnectText.Text = $"{stats.ReconnectState} · attempt {stats.ReconnectAttempts}";
    }

    public void UpdateConnectionState(SessionState state)
    {
        // Preserve the last successfully painted frame while transport/authentication is uncertain.
        _freezeFrames = state == SessionState.Reconnecting;
        if (_freezeFrames) ClearPendingFrame();
        _sessionConnected = state == SessionState.ConnectedViewOnly;

        _dispatcher.TryEnqueue(async () =>
        {
            if (!_sessionConnected)
            {
                await DisableRemoteInputCaptureAsync();
            }
            ReconnectOverlay.Visibility = state == SessionState.Reconnecting
                ? Visibility.Visible
                : Visibility.Collapsed;
            ConnectionText.Text = DisplayConnectionState(state);
            UpdateFileTransferAvailability();
        });
    }

    private async void OnChooseFiles(object sender, RoutedEventArgs e)
    {
        if (!CanOfferFiles) return;

        FileTransferButton.IsEnabled = false;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) await OfferStorageItemsAsync(files);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not choose files for session {SessionId}", _sessionId);
            ShowTransferFailure("File transfer could not start");
        }
        finally
        {
            UpdateFileTransferAvailability();
        }
    }

    private void OnFileTransferChanged(object? sender, TransferSnapshot snapshot)
    {
        if (snapshot.Incoming) return;

        _dispatcher.TryEnqueue(() =>
        {
            if (!_closed && _displayedTransferId == snapshot.TransferId)
                ShowTransferProgress(snapshot);
        });
    }

    private void ShowTransferProgress(TransferSnapshot snapshot)
    {
        var percent = snapshot.Status == TransferStatus.Completed
            ? 100d
            : snapshot.TotalBytes <= 0
                ? 0d
                : Math.Clamp(snapshot.TransferredBytes * 100d / snapshot.TotalBytes, 0d, 100d);
        var status = snapshot.Status switch
        {
            TransferStatus.Offered or TransferStatus.AwaitingDecision => "Waiting for remote approval",
            TransferStatus.Queued => "Queued",
            TransferStatus.Transferring => "Sending",
            TransferStatus.Paused => "Paused",
            TransferStatus.Completed => "Transfer completed",
            TransferStatus.Rejected => "Transfer declined",
            TransferStatus.Canceled => "Transfer canceled",
            TransferStatus.Failed => "Transfer failed",
            _ => snapshot.Status.ToString(),
        };

        SetTransferProgress($"{status}: {snapshot.DisplayName}", percent);
        if (snapshot.Status == TransferStatus.Completed)
            _transferDismissTimer.Start();
    }

    private void ShowTransferFailure(string message) => SetTransferProgress(message, 0);

    private void ShowTransferPreparing(IReadOnlyList<string> paths)
    {
        _displayedTransferId = null;
        var status = paths.Count == 1
            ? $"Preparing: {Path.GetFileName(paths[0])}"
            : $"Preparing {paths.Count} items";
        SetTransferProgress(status, 0);
    }

    private void SetTransferProgress(string status, double percent)
    {
        _transferDismissTimer.Stop();
        TransferProgressContainer.Visibility = Visibility.Visible;
        TransferStatusText.Text = status;
        TransferPercentText.Text = $"{percent:0}%";
        TransferProgressBar.Value = percent;
        AutomationProperties.SetItemStatus(
            TransferProgressBar,
            $"{TransferStatusText.Text}, {TransferPercentText.Text}");
    }

    private void UpdateFileTransferAvailability()
    {
        FileTransferButton.IsEnabled = CanOfferFiles;
    }

    private string DisplayConnectionState(SessionState state) =>
        state == SessionState.ConnectedViewOnly
            ? _mode == SessionMode.FullControl ? "Connected - Full Control" : "Connected - View Only"
            : state.ToString();

    private void OnFit(object sender, RoutedEventArgs e)
        => ApplyFitLayout();

    private void ApplyFitLayout()
        => ApplyWindowScaleLayout(
            DisplayScaleMode.Fit,
            Microsoft.UI.Xaml.Media.Stretch.Uniform,
            "Fit to window");

    private void OnFillWindow(object sender, RoutedEventArgs e)
        => ApplyWindowScaleLayout(
            DisplayScaleMode.Fill,
            Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            "Fill window");

    private void OnStretchWindow(object sender, RoutedEventArgs e)
        => ApplyWindowScaleLayout(
            DisplayScaleMode.Stretch,
            Microsoft.UI.Xaml.Media.Stretch.Fill,
            "Stretch");

    private void ApplyWindowScaleLayout(
        DisplayScaleMode mode,
        Microsoft.UI.Xaml.Media.Stretch stretch,
        string label)
    {
        _displayScaleMode = mode;
        Scroller.HorizontalScrollMode = ScrollMode.Disabled;
        Scroller.VerticalScrollMode = ScrollMode.Disabled;
        Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        Scroller.HorizontalContentAlignment = HorizontalAlignment.Left;
        Scroller.VerticalContentAlignment = VerticalAlignment.Top;
        RemoteImage.Stretch = stretch;
        RemoteImage.HorizontalAlignment = HorizontalAlignment.Stretch;
        RemoteImage.VerticalAlignment = VerticalAlignment.Stretch;
        RemoteImage.Width = double.NaN;
        RemoteImage.Height = double.NaN;
        ApplyWindowScaleDimensions(Scroller.ActualWidth, Scroller.ActualHeight);
        Scroller.ChangeView(0, 0, null, disableAnimation: true);
        UpdateScaleSelection(label);
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_displayScaleMode == DisplayScaleMode.ActualSize) return;
        ApplyWindowScaleDimensions(e.NewSize.Width, e.NewSize.Height);
    }

    private void ApplyWindowScaleDimensions(double width, double height)
    {
        // ScrollViewer measures its child with an unconstrained extent even when scrolling is
        // disabled. Pin the Image element to the visible viewport so its Stretch mode actually
        // controls whether the frame is fitted, cropped, or distorted.
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            RemoteImageViewport.Width = double.NaN;
            RemoteImageViewport.Height = double.NaN;
            return;
        }

        RemoteImageViewport.Width = width;
        RemoteImageViewport.Height = height;
    }

    private void OnActualSize(object sender, RoutedEventArgs e)
    {
        _displayScaleMode = DisplayScaleMode.ActualSize;
        Scroller.HorizontalScrollMode = ScrollMode.Enabled;
        Scroller.VerticalScrollMode = ScrollMode.Enabled;
        Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Scroller.HorizontalContentAlignment = HorizontalAlignment.Left;
        Scroller.VerticalContentAlignment = VerticalAlignment.Top;
        // Fill is intentional: the element is sized to one physical pixel per source pixel
        // below, so unlike Stretch=None the bitmap cannot retain a larger natural DIP size
        // and become clipped at 125%/150%/200% display scaling.
        RemoteImage.Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill;
        RemoteImage.HorizontalAlignment = HorizontalAlignment.Stretch;
        RemoteImage.VerticalAlignment = VerticalAlignment.Stretch;
        RemoteImage.Width = double.NaN;
        RemoteImage.Height = double.NaN;
        ApplyActualSizeDimensions();
        UpdateScaleSelection("Actual size");
    }

    private void ApplyActualSizeDimensions()
    {
        if (_bitmapWidth <= 0 || _bitmapHeight <= 0)
        {
            RemoteImageViewport.Width = double.NaN;
            RemoteImageViewport.Height = double.NaN;
            return;
        }

        var rasterizationScale = Math.Max(0.01, RemoteImage.XamlRoot?.RasterizationScale ?? 1);
        var size = RemotePointerMapper.ActualSizeInDips(_bitmapWidth, _bitmapHeight, rasterizationScale);
        RemoteImageViewport.Width = size.Width;
        RemoteImageViewport.Height = size.Height;
    }

    private void OnRemoteImageLoaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_remoteImageRoot, RemoteImage.XamlRoot)) return;
        if (_remoteImageRoot is not null) _remoteImageRoot.Changed -= OnRemoteImageRootChanged;
        _remoteImageRoot = RemoteImage.XamlRoot;
        if (_remoteImageRoot is not null) _remoteImageRoot.Changed += OnRemoteImageRootChanged;
    }

    private void OnRemoteImageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_remoteImageRoot is not null) _remoteImageRoot.Changed -= OnRemoteImageRootChanged;
        _remoteImageRoot = null;
    }

    private void OnRemoteImageRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_displayScaleMode == DisplayScaleMode.ActualSize)
        {
            ApplyActualSizeDimensions();
        }
    }

    private void UpdateScaleSelection(string label)
    {
        FitScaleItem.IsChecked = _displayScaleMode == DisplayScaleMode.Fit;
        FillScaleItem.IsChecked = _displayScaleMode == DisplayScaleMode.Fill;
        StretchScaleItem.IsChecked = _displayScaleMode == DisplayScaleMode.Stretch;
        ActualScaleItem.IsChecked = _displayScaleMode == DisplayScaleMode.ActualSize;
        ScaleButton.Label = label;
        AutomationProperties.SetName(ScaleButton, $"Display scaling: {label}");
    }

    private void OnFullScreen(object sender, RoutedEventArgs e)
        => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;

        AppWindow.SetPresenter(_isFullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Overlapped);
        // Immersive fullscreen restores the complete source aspect without permanently
        // consuming vertical pixels for chrome. F11 remains local and the top edge reveals exit.
        ViewerCommandBar.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        FullScreenRevealTarget.Visibility = _isFullScreen ? Visibility.Visible : Visibility.Collapsed;
        FullScreenRevealBar.Visibility = Visibility.Collapsed;
        _fullScreenRevealTimer.Stop();
    }

    private void OnFullScreenRevealTargetEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullScreen) return;

        _fullScreenRevealTimer.Stop();
        FullScreenRevealBar.Visibility = Visibility.Visible;
    }

    private void OnFullScreenRevealBarEntered(object sender, PointerRoutedEventArgs e) =>
        _fullScreenRevealTimer.Stop();

    private void OnFullScreenRevealBarExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullScreen) return;

        _fullScreenRevealTimer.Stop();
        _fullScreenRevealTimer.Start();
    }

    private async void OnViewerActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_initialActivationPending)
        {
            _initialActivationPending = false;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }

        if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            _onActivated?.Invoke(_sessionId);
            EnableAuthorizedInput();
        }

        if (e.WindowActivationState == WindowActivationState.Deactivated
            && _remoteInput?.IsLocallyEnabled == true)
        {
            await PauseInputAfterPointerCaptureLossAsync(restoreWhenActive: false);
        }
    }

    public void EnableAuthorizedInput()
    {
        _dispatcher.TryEnqueue(async () =>
        {
            if (_remoteInput is null || !_sessionConnected || _remoteInput.IsLocallyEnabled) return;

            try
            {
                await EnableRemoteInputCaptureAsync();
            }
            catch (Exception ex)
            {
                await HandleInputFailureAsync(ex);
            }
        });
    }

    private async void OnEnd(object sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _endRequested, 1) == 1) return;

        EndButton.IsEnabled = false;
        var disableInput = DisableRemoteInputCaptureAsync();
        await _onEnd();
        await disableInput;
        if (!_closed) Close();
    }

    private async Task EnableRemoteInputCaptureAsync()
    {
        if (_remoteInput is null || !_sessionConnected)
        {
            return;
        }

        await _remoteInput.SetLocalCaptureEnabledAsync(true);
        ApplyFitLayout();
        ActualScaleItem.IsEnabled = false;
        RemoteInputSurface.Visibility = Visibility.Visible;
        RemoteInputSurface.Focus(FocusState.Programmatic);
        ConnectionText.Text = "Full Control active";
    }

    private void OnRemoteImagePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // A focus/pointer-capture safety release may temporarily hide the input surface. Once
        // the approved viewer returns to the remote image, request a new host-acknowledged focus
        // generation rather than exposing a manual resume action.
        if (_remoteInput is null
            || _remoteInput.IsLocallyEnabled
            || !_remoteInput.IsControlPermissionAvailable
            || !_sessionConnected)
        {
            return;
        }

        EnableAuthorizedInput();
    }

    private async void OnRemotePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _pointerEventLogged, 1) == 0)
        {
            _log.LogInformation("Remote input surface received its first pointer event");
        }
        if (!TryNormalizePointer(e, out var x, out var y) || _remoteInput is null) return;
        if (Interlocked.Exchange(ref _pointerNormalizedLogged, 1) == 0)
        {
            _log.LogInformation("Remote input surface normalized its first pointer event");
        }
        RememberPointer(x, y);
        e.Handled = true;
        try
        {
            await _remoteInput.SendPointerMoveAsync(x, y);
            if (Interlocked.Exchange(ref _pointerSentLogged, 1) == 0)
            {
                _log.LogInformation("Remote input channel accepted its first pointer event");
            }
        }
        catch (Exception ex)
        {
            await HandleInputFailureAsync(ex);
        }
    }

    private async void OnRemotePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Toolbar commands take keyboard focus. Restore it on the first content click so
        // subsequent key down/up events continue to reach the approved remote-input surface.
        RemoteInputSurface.Focus(FocusState.Pointer);
        if (!TryNormalizePointer(e, out var x, out var y) || _remoteInput is null) return;
        var button = PointerButtonFor(e.GetCurrentPoint(RemotePointerSurface).Properties.PointerUpdateKind);
        if (button == RemotePointerButton.None) return;
        if (!RemotePointerSurface.CapturePointer(e.Pointer)) return;
        RememberPointer(x, y);
        _pressedRemoteButtons.Add(button);
        e.Handled = true;

        try
        {
            await _remoteInput.SendPointerButtonAsync(x, y, button, isPressed: true);
        }
        catch (Exception ex)
        {
            _pressedRemoteButtons.Remove(button);
            RemotePointerSurface.ReleasePointerCapture(e.Pointer);
            await HandleInputFailureAsync(ex);
        }
    }

    private async void OnRemotePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var button = PointerButtonFor(e.GetCurrentPoint(RemotePointerSurface).Properties.PointerUpdateKind);
        if (button == RemotePointerButton.None || _remoteInput is null) return;

        var hasPosition = TryNormalizePointer(e, out var x, out var y);
        if (hasPosition) RememberPointer(x, y);
        else if (_hasLastPointer)
        {
            x = _lastPointerX;
            y = _lastPointerY;
            hasPosition = true;
        }

        var wasPressed = _pressedRemoteButtons.Remove(button);
        var shouldSend = hasPosition && wasPressed;
        if (_pressedRemoteButtons.Count == 0)
        {
            RemotePointerSurface.ReleasePointerCapture(e.Pointer);
        }
        e.Handled = true;

        try
        {
            if (shouldSend)
            {
                await _remoteInput.SendPointerButtonAsync(x, y, button, isPressed: false);
            }
        }
        catch (Exception ex)
        {
            await HandleInputFailureAsync(ex);
        }
    }

    private async void OnRemotePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!TryNormalizePointer(e, out var x, out var y) || _remoteInput is null) return;
        RememberPointer(x, y);
        var properties = e.GetCurrentPoint(RemotePointerSurface).Properties;
        var delta = properties.MouseWheelDelta;
        var isHorizontal = properties.IsHorizontalMouseWheel;
        if (delta == 0) return;
        e.Handled = true;

        try
        {
            await _remoteInput.SendPointerWheelAsync(
                x,
                y,
                Math.Clamp(delta, -1200, 1200),
                isHorizontal);
        }
        catch (Exception ex)
        {
            await HandleInputFailureAsync(ex);
        }
    }

    private async void OnRemoteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var virtualKey = (ushort)e.Key;
        if (virtualKey is > 0 and <= 254)
            _pressedRemoteKeys.Add(virtualKey);

        if (IsClipboardFilePaste(e))
        {
            _suppressFilePasteKeyUp = true;
            e.Handled = true;
            await OfferClipboardFilesAsync();
            return;
        }

        await SendRemoteKeyAsync(e, isPressed: true);
    }

    private async void OnRemoteKeyUp(object sender, KeyRoutedEventArgs e)
    {
        var virtualKey = (ushort)e.Key;
        if (virtualKey is > 0 and <= 254)
            _pressedRemoteKeys.Remove(virtualKey);

        if (e.Key == VirtualKey.V && _suppressFilePasteKeyUp)
        {
            _suppressFilePasteKeyUp = false;
            e.Handled = true;
            return;
        }

        await SendRemoteKeyAsync(e, isPressed: false);
    }

    private bool IsClipboardFilePaste(KeyRoutedEventArgs e) =>
        e.Key == VirtualKey.V
        && IsControlKeyPressed()
        && CanOfferFiles
        && Clipboard.GetContent().Contains(StandardDataFormats.StorageItems);

    private bool IsControlKeyPressed() =>
        _pressedRemoteKeys.Contains((ushort)VirtualKey.Control)
        || _pressedRemoteKeys.Contains((ushort)VirtualKey.LeftControl)
        || _pressedRemoteKeys.Contains((ushort)VirtualKey.RightControl);

    private async Task OfferClipboardFilesAsync()
    {
        var data = Clipboard.GetContent();
        try
        {
            await OfferStorageItemsAsync(await data.GetStorageItemsAsync());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not offer clipboard files for session {SessionId}", _sessionId);
            ConnectionText.Text = "File transfer could not start";
        }
    }

    private bool CanOfferFiles =>
        _mode == SessionMode.FullControl
        && _sessionConnected
        && _fileTransfers is not null;

    private void OnRemoteFilesDragOver(object sender, DragEventArgs e)
    {
        if (!CanOfferFiles || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Send files to remote device";
        e.DragUIOverride.IsCaptionVisible = true;
        e.Handled = true;
    }

    private async void OnRemoteFilesDropped(object sender, DragEventArgs e)
    {
        if (!CanOfferFiles || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        e.Handled = true;
        try
        {
            await OfferStorageItemsAsync(await e.DataView.GetStorageItemsAsync());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not offer dropped files for session {SessionId}", _sessionId);
            ConnectionText.Text = "File transfer could not start";
        }
    }

    private async Task OfferStorageItemsAsync(IReadOnlyList<IStorageItem> storageItems)
    {
        if (!CanOfferFiles || _fileTransfers is null)
            throw new UnauthorizedAccessException("File transfer is not available for this session.");

        var paths = storageItems
            .Where(item => item is StorageFile or StorageFolder)
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
            throw new InvalidOperationException("The selected clipboard or dropped content does not contain transferable files or folders.");

        ShowTransferPreparing(paths);
        var transferId = await _fileTransfers.OfferAsync(paths);
        _displayedTransferId = transferId;
        var snapshot = _fileTransfers.History.FirstOrDefault(item => item.TransferId == transferId);
        if (snapshot is not null) ShowTransferProgress(snapshot);
        ConnectionText.Text = "File transfer offered; awaiting remote acceptance";
    }

    private async Task SendRemoteKeyAsync(KeyRoutedEventArgs e, bool isPressed)
    {
        var virtualKey = (ushort)e.Key;
        if (virtualKey is 0 or > 254) return;

        // Full Control fullscreen intentionally hides the toolbar. Keep F11 local so the viewer
        // always has an immediate way back to the windowed controls.
        if (virtualKey == 0x7A)
        {
            e.Handled = true;
            if (isPressed) ToggleFullScreen();
            return;
        }

        if (_remoteInput is null) return;
        e.Handled = true;

        try
        {
            await _remoteInput.SendKeyAsync(virtualKey, isPressed, IsExtendedKey(virtualKey));
        }
        catch (Exception ex)
        {
            await HandleInputFailureAsync(ex);
        }
    }

    private bool TryNormalizePointer(PointerRoutedEventArgs e, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (RemoteImage.ActualWidth <= 0 || RemoteImage.ActualHeight <= 0
            || _bitmapWidth <= 0 || _bitmapHeight <= 0)
            return false;

        var position = e.GetCurrentPoint(RemotePointerSurface).Position;
        var transformed = RemoteImage.TransformToVisual(RemotePointerSurface).TransformBounds(
            new Windows.Foundation.Rect(0, 0, RemoteImage.ActualWidth, RemoteImage.ActualHeight));
        var content = new RemoteContentBounds(
            transformed.X,
            transformed.Y,
            transformed.Width,
            transformed.Height);
        if (_displayScaleMode == DisplayScaleMode.Fit)
        {
            content = RemotePointerMapper.UniformFit(content, _bitmapWidth, _bitmapHeight);
        }
        else if (_displayScaleMode == DisplayScaleMode.Fill)
        {
            content = RemotePointerMapper.UniformFill(content, _bitmapWidth, _bitmapHeight);
        }

        return RemotePointerMapper.TryNormalize(position.X, position.Y, content, out x, out y);
    }

    private void RememberPointer(double x, double y)
    {
        _lastPointerX = x;
        _lastPointerY = y;
        _hasLastPointer = true;
    }

    private async void OnRemotePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedRemoteButtons.Count > 0)
        {
            await PauseInputAfterPointerCaptureLossAsync();
        }
    }

    private async Task PauseInputAfterPointerCaptureLossAsync(bool restoreWhenActive = true)
    {
        await DisableRemoteInputCaptureAsync();
        if (!restoreWhenActive
            || !_sessionConnected
            || _remoteInput?.IsControlPermissionAvailable != true) return;

        try
        {
            await EnableRemoteInputCaptureAsync();
        }
        catch (Exception ex)
        {
            await HandleInputFailureAsync(ex);
        }
    }

    private static RemotePointerButton PointerButtonFor(Microsoft.UI.Input.PointerUpdateKind kind) => kind switch
    {
        Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed or
            Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased => RemotePointerButton.Left,
        Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed or
            Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased => RemotePointerButton.Right,
        Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed or
            Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased => RemotePointerButton.Middle,
        Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed or
            Microsoft.UI.Input.PointerUpdateKind.XButton1Released => RemotePointerButton.X1,
        Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed or
            Microsoft.UI.Input.PointerUpdateKind.XButton2Released => RemotePointerButton.X2,
        _ => RemotePointerButton.None,
    };

    private static bool IsExtendedKey(ushort virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0x90 or 0x91 or 0xA3 or 0xA5;

    private async Task DisableRemoteInputCaptureAsync()
    {
        _pressedRemoteButtons.Clear();
        _pressedRemoteKeys.Clear();
        _suppressFilePasteKeyUp = false;
        _hasLastPointer = false;
        RemotePointerSurface.ReleasePointerCaptures();
        RemoteInputSurface.Visibility = Visibility.Collapsed;
        ActualScaleItem.IsEnabled = true;
        if (_remoteInput is null)
        {
            return;
        }

        if (!_remoteInput.IsLocallyEnabled)
        {
            return;
        }

        try
        {
            await _remoteInput.SetLocalCaptureEnabledAsync(false);
        }
        catch (Exception)
        {
            // The coordinator independently releases sharer input on interruption/session end.
        }
    }

    private async Task HandleInputFailureAsync(Exception exception)
    {
        await DisableRemoteInputCaptureAsync();
        ConnectionText.Text = $"Remote input stopped: {exception.Message}";
    }

    private void OnControlPermissionChanged(object? sender, RemoteInputPermissionStatus status)
    {
        _dispatcher.TryEnqueue(async () =>
        {
            await DisableRemoteInputCaptureAsync();
            ConnectionText.Text = status.IsAvailable
                ? "Remote input is available"
                : "Control permission revoked by the remote owner";
        });
    }

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ConnectionDiagnosticsExporter.ExportSanitized(_statistics(), _timeline()));
        Clipboard.SetContent(package);
    }

    private sealed record PendingVideoFrame(
        int Width,
        int Height,
        byte[] Pixels,
        long DecodedTimestamp,
        long CaptureTimestampUnixMicroseconds,
        long CaptureSequenceNumber,
        PeerClockEstimate? PeerClockEstimate);

    private static byte[] TakeFramePixels(RemoteVideoFrame frame)
    {
        if (frame.BufferOwnershipCanTransfer
            && MemoryMarshal.TryGetArray(frame.Pixels, out var segment)
            && segment.Array is { } array
            && segment.Offset == 0
            && segment.Count == array.Length)
        {
            return array;
        }

        return frame.Pixels.ToArray();
    }
}

/// <summary>Native access to a WinRT IBuffer's backing memory.</summary>
[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IBufferByteAccess
{
    void Buffer(out byte* value);
}
