using PeerOnQ.Application.Abstractions;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Application.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PeerOnQ.App;

/// <summary>
/// Shows one display of the sharer's combined "All displays" view in its own window. It holds no
/// session state: frames come from the owning <see cref="ViewerWindow"/>, and pointer and key
/// events go back through it, so the approved input channel, permission checks and the
/// focus-loss safety release stay in one place.
/// </summary>
public sealed partial class RemoteMonitorWindow : Window
{
    private readonly ViewerWindow _owner;
    private readonly HashSet<RemotePointerButton> _pressedButtons = [];
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private bool _closed;

    public RemoteMonitorWindow(ViewerWindow owner, int monitor, string title)
    {
        InitializeComponent();
        _owner = owner;
        Monitor = monitor;
        Title = title;
        Activated += (_, e) =>
        {
            IsActive = e.WindowActivationState != WindowActivationState.Deactivated;
            if (IsActive) InputSurface.Focus(FocusState.Programmatic);
            _owner.OnMonitorWindowActivation(IsActive);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            IsActive = false;
            _owner.OnMonitorWindowClosed(this);
        };
    }

    /// <summary>Index of the shown display in the owner's monitor layout.</summary>
    public int Monitor { get; }

    public bool IsActive { get; private set; }

    public int RenderedFrames { get; private set; }

    /// <summary>Copies this window's part of a combined frame (UI thread).</summary>
    internal void Render(byte[] pixels, int frameWidth, RemotePixelFormat format, RemoteFrameCrop crop)
    {
        if (_closed) return;
        if (_bitmap is null || _bitmapWidth != crop.Width || _bitmapHeight != crop.Height)
        {
            _bitmap = new WriteableBitmap(crop.Width, crop.Height);
            _bitmapWidth = crop.Width;
            _bitmapHeight = crop.Height;
            MonitorImage.Source = _bitmap;
        }

        ViewerWindow.CopyCrop(_bitmap, pixels, frameWidth, format, crop);
        _bitmap.Invalidate();
        RenderedFrames++;
    }

    private bool TryNormalizePointer(PointerRoutedEventArgs e, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (MonitorImage.ActualWidth <= 0 || MonitorImage.ActualHeight <= 0
            || _bitmapWidth <= 0 || _bitmapHeight <= 0)
            return false;

        var position = e.GetCurrentPoint(PointerSurface).Position;
        var transformed = MonitorImage.TransformToVisual(PointerSurface).TransformBounds(
            new Windows.Foundation.Rect(0, 0, MonitorImage.ActualWidth, MonitorImage.ActualHeight));
        var content = RemotePointerMapper.UniformFit(
            new RemoteContentBounds(transformed.X, transformed.Y, transformed.Width, transformed.Height),
            _bitmapWidth,
            _bitmapHeight);
        return RemotePointerMapper.TryNormalize(position.X, position.Y, content, out x, out y);
    }

    private async void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_owner.IsRemoteInputActive || !TryNormalizePointer(e, out var x, out var y)) return;
        e.Handled = true;
        await _owner.SendMonitorPointerMoveAsync(Monitor, x, y);
    }

    private async void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        InputSurface.Focus(FocusState.Pointer);
        if (!_owner.IsRemoteInputActive || !TryNormalizePointer(e, out var x, out var y)) return;
        var button = ViewerWindow.PointerButtonOf(e.GetCurrentPoint(PointerSurface).Properties.PointerUpdateKind);
        if (button == RemotePointerButton.None || !PointerSurface.CapturePointer(e.Pointer)) return;
        _pressedButtons.Add(button);
        e.Handled = true;
        if (!await _owner.SendMonitorPointerButtonAsync(Monitor, x, y, button, isPressed: true))
        {
            _pressedButtons.Remove(button);
            PointerSurface.ReleasePointerCapture(e.Pointer);
        }
    }

    private async void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var button = ViewerWindow.PointerButtonOf(e.GetCurrentPoint(PointerSurface).Properties.PointerUpdateKind);
        if (button == RemotePointerButton.None) return;
        var wasPressed = _pressedButtons.Remove(button);
        if (_pressedButtons.Count == 0) PointerSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        // A release outside the image still ends the remote press at the last known position.
        var hasPosition = TryNormalizePointer(e, out var x, out var y);
        if (wasPressed) await _owner.SendMonitorPointerButtonAsync(Monitor, x, y, button, isPressed: false, hasPosition);
    }

    private async void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_owner.IsRemoteInputActive || !TryNormalizePointer(e, out var x, out var y)) return;
        var properties = e.GetCurrentPoint(PointerSurface).Properties;
        if (properties.MouseWheelDelta == 0) return;
        e.Handled = true;
        await _owner.SendMonitorPointerWheelAsync(Monitor, x, y, properties.MouseWheelDelta, properties.IsHorizontalMouseWheel);
    }

    private async void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedButtons.Count == 0) return;
        _pressedButtons.Clear();
        await _owner.PauseInputAfterMonitorPointerCaptureLossAsync();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_owner.IsRemoteInputActive) _owner.ForwardMonitorKey(sender, e, isPressed: true);
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_owner.IsRemoteInputActive) _owner.ForwardMonitorKey(sender, e, isPressed: false);
    }
}
