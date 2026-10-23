using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Platform.Windows.Rendering;

namespace PeerOnQ.App;

/// <summary>
/// GPU presentation for the viewer. Frames are uploaded to a Direct3D 11 composition swap chain
/// from the media thread and scaled by the compositor, so the UI thread does no per-frame work.
/// The WriteableBitmap path stays as the fallback: no hardware device, an interop failure, a
/// device loss, or PEERONQ_VIEWER_RENDERER=bitmap all keep or return the viewer to it.
/// </summary>
public sealed partial class ViewerWindow
{
    private D3D11CompositionVideoPresenter? _gpuPresenter;
    private bool _gpuSurfaceVisible;

    /// <summary>The element whose bounds hold the remote frame (UI thread only).</summary>
    private FrameworkElement ActiveRemoteSurface =>
        _gpuSurfaceVisible ? RemoteSwapChainPanel : RemoteImage;

    private string RendererName => _gpuSurfaceVisible ? "GPU swap chain" : "bitmap";

    internal string RendererNameForSelfTest => RendererName;

    internal string? RenderFailureForSelfTest => _renderFailure;

    /// <summary>Self-test: 0 Fit, 1 Fill, 2 Stretch, 3 Actual size.</summary>
    internal void ApplyScaleModeForSelfTest(int mode)
    {
        switch (mode)
        {
            case 1: OnFillWindow(this, new RoutedEventArgs()); break;
            case 2: OnStretchWindow(this, new RoutedEventArgs()); break;
            case 3: OnActualSize(this, new RoutedEventArgs()); break;
            default: OnFit(this, new RoutedEventArgs()); break;
        }
    }

    internal void StepMonitorForSelfTest(int step) => SwitchRemoteMonitor(step);

    private void InitializeGpuPresentation()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("PEERONQ_VIEWER_RENDERER"), "bitmap",
                StringComparison.OrdinalIgnoreCase)
            || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return;

        var presenter = D3D11CompositionVideoPresenter.TryCreate();
        if (presenter is null)
        {
            _log.LogInformation("Viewer {SessionId} presents with a bitmap: no Direct3D 11 swap chain", _sessionId);
            return;
        }

        try
        {
            SwapChainPanelInterop.SetSwapChain(RemoteSwapChainPanel, presenter.SwapChainPointer);
        }
        catch (Exception ex)
        {
            presenter.Dispose();
            _log.LogWarning(ex, "Viewer {SessionId} could not attach its swap chain; using a bitmap", _sessionId);
            return;
        }

        Volatile.Write(ref _gpuPresenter, presenter);
        _gpuSurfaceVisible = true;
        RemoteSwapChainPanel.Visibility = Visibility.Visible;
        RemoteImage.Visibility = Visibility.Collapsed;
        _log.LogInformation("Viewer {SessionId} presents through a Direct3D 11 swap chain", _sessionId);
    }

    /// <summary>Called on the media thread. False means the bitmap path must draw this frame.</summary>
    private bool TryPresentOnGpu(RemoteVideoFrame frame)
    {
        if (Volatile.Read(ref _gpuPresenter) is not { } presenter) return false;

        var pixels = TakeFramePixels(frame, out var transferred);
        var presented = false;
        try
        {
            var sizeChanged = presenter.Width != frame.Width || presenter.Height != frame.Height;
            presenter.Present(frame.Width, frame.Height,
                frame.Format == RemotePixelFormat.Bgra32 ? pixels : ExpandBgr24(pixels, frame.Width, frame.Height));
            presented = true;
            if (sizeChanged) _dispatcher.TryEnqueue(() => OnGpuFrameSizeChanged(frame.Width, frame.Height));
            _renderFailure = null;
            _onRendered?.Invoke(new PresentedVideoFrame(
                frame.PipelineDecodedTimestamp,
                frame.Width,
                frame.Height,
                frame.CaptureTimestampUnixMicroseconds,
                frame.CaptureSequenceNumber,
                frame.PeerClockEstimate));
            return true;
        }
        catch (Exception ex)
        {
            // The pixels are still intact: the bitmap path draws this same frame.
            DisableGpuPresentation(ex);
            return presented;
        }
        finally
        {
            if (presented && transferred) frame.ReturnBuffer?.Invoke(pixels);
        }
    }

    private static byte[] ExpandBgr24(byte[] bgr24, int width, int height)
    {
        var bgra = GC.AllocateUninitializedArray<byte>(width * height * 4);
        for (int source = 0, target = 0; target < bgra.Length; source += 3, target += 4)
        {
            bgra[target] = bgr24[source];
            bgra[target + 1] = bgr24[source + 1];
            bgra[target + 2] = bgr24[source + 2];
            bgra[target + 3] = 255;
        }
        return bgra;
    }

    private void OnGpuFrameSizeChanged(int width, int height)
    {
        if (_closed || !_gpuSurfaceVisible) return;
        _bitmapWidth = width;
        _bitmapHeight = height;
        if (_displayScaleMode == DisplayScaleMode.ActualSize) ApplyActualSizeDimensions();
        UpdateGpuLayout();
    }

    private void OnRemoteSwapChainPanelSizeChanged(object sender, SizeChangedEventArgs e) => UpdateGpuLayout();

    private void OnRemoteSwapChainPanelCompositionScaleChanged(SwapChainPanel sender, object args) => UpdateGpuLayout();

    /// <summary>Gives the presenter the viewport and scale mode the bitmap would have used.</summary>
    private void UpdateGpuLayout()
    {
        if (Volatile.Read(ref _gpuPresenter) is not { } presenter) return;
        var panel = RemoteSwapChainPanel;
        var mode = _displayScaleMode switch
        {
            DisplayScaleMode.Fill => VideoPresentationScaleMode.Fill,
            DisplayScaleMode.Stretch => VideoPresentationScaleMode.Stretch,
            DisplayScaleMode.ActualSize => VideoPresentationScaleMode.ActualSize,
            _ => VideoPresentationScaleMode.Fit,
        };
        try
        {
            presenter.UpdateLayout(new VideoPresentationLayout(
                panel.ActualWidth, panel.ActualHeight, panel.CompositionScaleX, panel.CompositionScaleY, mode));
        }
        catch (Exception ex)
        {
            DisableGpuPresentation(ex);
        }
    }

    /// <summary>Returns the viewer to the bitmap path after a device or interop failure.</summary>
    private void DisableGpuPresentation(Exception error)
    {
        if (Interlocked.Exchange(ref _gpuPresenter, null) is not { } presenter) return;
        _log.LogWarning(error, "Viewer {SessionId} GPU presentation failed; switching to a bitmap", _sessionId);
        if (!_dispatcher.TryEnqueue(() => ReleaseGpuSurface(presenter))) presenter.Dispose();
    }

    private void ShutdownGpuPresentation()
    {
        if (Interlocked.Exchange(ref _gpuPresenter, null) is { } presenter) ReleaseGpuSurface(presenter);
    }

    private void ReleaseGpuSurface(D3D11CompositionVideoPresenter presenter)
    {
        try
        {
            SwapChainPanelInterop.SetSwapChain(RemoteSwapChainPanel, 0);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Viewer {SessionId} could not detach its swap chain", _sessionId);
        }
        presenter.Dispose();
        _gpuSurfaceVisible = false;
        if (_closed) return;
        RemoteSwapChainPanel.Visibility = Visibility.Collapsed;
        RemoteImage.Visibility = Visibility.Visible;
    }
}
