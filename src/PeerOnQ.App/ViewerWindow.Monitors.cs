using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Sessions;
using WinRT;

namespace PeerOnQ.App;

/// <summary>
/// Separate windows: while the sharer streams its combined "All displays" view, this window
/// shows the first remote display and one <see cref="RemoteMonitorWindow"/> per further display,
/// all cut from the same decoded frame. Input from any of them is mapped to the combined view and
/// sent through this window's approved channel; it pauses only when no window of the group is
/// active. Crops are drawn by the bitmap presenter; turning separate windows on leaves the GPU swap chain.
/// </summary>
public sealed partial class ViewerWindow
{
    private readonly List<RemoteMonitorWindow> _monitorWindows = [];
    private IReadOnlyList<CaptureTargetInfo> _remoteDisplays = [];
    private string _activeRemoteDisplayId = string.Empty;
    private bool _separateWindowsRequested;
    private bool _viewerWindowActive;
    private RemoteMonitorLayout? _splitLayout;

    internal bool IsRemoteInputActive => _remoteInput?.IsLocallyEnabled == true;

    internal int MonitorWindowCountForSelfTest => _monitorWindows.Count;

    internal int MonitorWindowFramesForSelfTest => _monitorWindows.Sum(window => window.RenderedFrames);

    internal void RequestSeparateWindowsForSelfTest()
    {
        SeparateWindowsButton.IsChecked = true;
        ToggleSeparateWindows();
    }

    private void ToggleSeparateWindows()
    {
        _separateWindowsRequested = SeparateWindowsButton.IsChecked == true;
        if (_separateWindowsRequested && _gpuSurfaceVisible)
        {
            // The crops are drawn by the bitmap presenter; the swap chain shows one full frame.
            _log.LogInformation("Viewer {SessionId} switches to the bitmap presenter for separate windows", _sessionId);
            ShutdownGpuPresentation();
        }
        if (_separateWindowsRequested
            && RemoteMonitorLayout.TryCreate(_remoteDisplays) is { } layout
            && layout.Combined.Id != _activeRemoteDisplayId)
        {
            // Selecting the combined entry sends the normal display request; the windows open
            // when the sharer confirms it is streaming the combined view.
            foreach (var item in MonitorPicker.Items)
            {
                if (item is ComboBoxItem { Tag: string id } && id == layout.Combined.Id)
                {
                    MonitorPicker.SelectedItem = item;
                    break;
                }
            }
        }

        UpdateSeparateWindows();
    }

    /// <summary>Applies the latest display list and toggle state (UI thread).</summary>
    private void UpdateSeparateWindows()
    {
        var layout = _onSelectDisplay is null ? null : RemoteMonitorLayout.TryCreate(_remoteDisplays);
        SeparateWindowsButton.Visibility = layout is null ? Visibility.Collapsed : Visibility.Visible;
        var streamingCombined = layout is not null && layout.Combined.Id == _activeRemoteDisplayId;
        // Choosing a single display while split ends separate windows instead of leaving the
        // toggle on with nothing to show.
        if (_splitLayout is not null && !streamingCombined) _separateWindowsRequested = false;
        SeparateWindowsButton.IsChecked = _separateWindowsRequested && layout is not null;

        var split = _separateWindowsRequested && streamingCombined ? layout : null;
        if (split is not null && _splitLayout is not null && SameMonitors(split, _splitLayout))
        {
            _splitLayout = split;
            return;
        }

        CloseMonitorWindows();
        _splitLayout = split;
        // The next frame reallocates this window's bitmap for its crop (or the full view).
        _bitmapWidth = 0;
        if (split is null) return;

        for (var monitor = 1; monitor < split.Monitors.Count; monitor++)
        {
            var display = split.Monitors[monitor];
            var window = new RemoteMonitorWindow(this, monitor, $"PeerOnQ - {display.DisplayName} ({display.Width}x{display.Height})");
            _monitorWindows.Add(window);
            window.Activate();
        }

        _log.LogInformation("Viewer {SessionId} shows {Count} remote monitors in separate windows", _sessionId, split.Monitors.Count);
    }

    private static bool SameMonitors(RemoteMonitorLayout left, RemoteMonitorLayout right) =>
        left.Combined == right.Combined && left.Monitors.SequenceEqual(right.Monitors);

    private void CloseMonitorWindows()
    {
        var windows = _monitorWindows.ToArray();
        _monitorWindows.Clear();
        foreach (var window in windows) window.Close();
    }

    internal void OnMonitorWindowClosed(RemoteMonitorWindow window)
    {
        if (!_monitorWindows.Remove(window) || _monitorWindows.Count > 0 || _closed) return;
        // The user closed the last extra window: this window shows the whole combined view again.
        _separateWindowsRequested = false;
        UpdateSeparateWindows();
        QueueGroupDeactivationCheck();
    }

    internal void OnMonitorWindowActivation(bool active)
    {
        if (active)
        {
            _onActivated?.Invoke(_sessionId);
            EnableAuthorizedInput();
        }
        else
        {
            QueueGroupDeactivationCheck();
        }
    }

    /// <summary>
    /// Moving activation between windows of the group arrives as a deactivate/activate pair, so
    /// the safety release is decided after both: input pauses only when no group window is active.
    /// </summary>
    private void QueueGroupDeactivationCheck() =>
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            if (_viewerWindowActive || _monitorWindows.Any(window => window.IsActive) || !IsRemoteInputActive) return;
            await PauseInputAfterPointerCaptureLossAsync(restoreWhenActive: false);
        });

    /// <summary>Draws this window's crop and every extra window's crop of one frame (UI thread).</summary>
    private void RenderMonitorWindows(PendingVideoFrame frame)
    {
        if (_splitLayout is not { } layout) return;
        foreach (var window in _monitorWindows)
        {
            if (window.Monitor < layout.Monitors.Count)
                window.Render(frame.Pixels, frame.Width, frame.Format, layout.CropFor(window.Monitor, frame.Width, frame.Height));
        }
    }

    internal async Task SendMonitorPointerMoveAsync(int monitor, double x, double y)
    {
        if (!TryMapMonitorPoint(monitor, x, y, out var combinedX, out var combinedY)) return;
        RememberPointer(combinedX, combinedY);
        try
        {
            await _remoteInput!.SendPointerMoveAsync(combinedX, combinedY);
        }
        catch (Exception ex)
        {
            await HandleInputFailureAsync(ex);
        }
    }

    internal async Task<bool> SendMonitorPointerButtonAsync(
        int monitor, double x, double y, RemotePointerButton button, bool isPressed, bool hasPosition = true)
    {
        double combinedX, combinedY;
        if (hasPosition && TryMapMonitorPoint(monitor, x, y, out combinedX, out combinedY))
        {
            RememberPointer(combinedX, combinedY);
        }
        else if (!isPressed && _hasLastPointer && IsRemoteInputActive)
        {
            combinedX = _lastPointerX;
            combinedY = _lastPointerY;
        }
        else
        {
            return false;
        }

        try
        {
            await _remoteInput!.SendPointerButtonAsync(combinedX, combinedY, button, isPressed);
            return true;
        }
        catch (Exception ex)
        {
            await HandleInputFailureAsync(ex);
            return false;
        }
    }

    internal async Task SendMonitorPointerWheelAsync(int monitor, double x, double y, int delta, bool isHorizontal)
    {
        if (!TryMapMonitorPoint(monitor, x, y, out var combinedX, out var combinedY)) return;
        RememberPointer(combinedX, combinedY);
        try
        {
            await _remoteInput!.SendPointerWheelAsync(combinedX, combinedY, Math.Clamp(delta, -1200, 1200), isHorizontal);
        }
        catch (Exception ex)
        {
            await HandleInputFailureAsync(ex);
        }
    }

    internal Task PauseInputAfterMonitorPointerCaptureLossAsync() => PauseInputAfterPointerCaptureLossAsync();

    internal void ForwardMonitorKey(object sender, KeyRoutedEventArgs e, bool isPressed)
    {
        if (isPressed) OnRemoteKeyDown(sender, e);
        else OnRemoteKeyUp(sender, e);
    }

    internal static RemotePointerButton PointerButtonOf(Microsoft.UI.Input.PointerUpdateKind kind) => PointerButtonFor(kind);

    private bool TryMapMonitorPoint(int monitor, double x, double y, out double combinedX, out double combinedY)
    {
        combinedX = 0;
        combinedY = 0;
        if (_splitLayout is not { } layout || monitor >= layout.Monitors.Count || !IsRemoteInputActive) return false;
        (combinedX, combinedY) = layout.ToCombined(monitor, x, y);
        return true;
    }

    /// <summary>Copies one crop of a BGRA32 or BGR24 frame into a BGRA32 bitmap of the crop size.</summary>
    internal static unsafe void CopyCrop(WriteableBitmap bitmap, byte[] pixels, int frameWidth, RemotePixelFormat format, RemoteFrameCrop crop)
    {
        var access = bitmap.PixelBuffer.As<IBufferByteAccess>();
        access.Buffer(out var destination);
        var capacity = (int)bitmap.PixelBuffer.Capacity;
        var sourceBytesPerPixel = format == RemotePixelFormat.Bgra32 ? 4 : 3;
        var rowBytes = crop.Width * 4;
        var target = new Span<byte>(destination, capacity);
        for (var row = 0; row < crop.Height; row++)
        {
            var sourceOffset = ((crop.Y + row) * frameWidth + crop.X) * sourceBytesPerPixel;
            var targetOffset = row * rowBytes;
            if (targetOffset + rowBytes > capacity || sourceOffset + crop.Width * sourceBytesPerPixel > pixels.Length) return;
            if (format == RemotePixelFormat.Bgra32)
            {
                pixels.AsSpan(sourceOffset, rowBytes).CopyTo(target.Slice(targetOffset, rowBytes));
                continue;
            }

            for (var column = 0; column < crop.Width; column++)
            {
                var source = sourceOffset + column * 3;
                var pixel = targetOffset + column * 4;
                target[pixel] = pixels[source];
                target[pixel + 1] = pixels[source + 1];
                target[pixel + 2] = pixels[source + 2];
                target[pixel + 3] = 255;
            }
        }
    }
}
