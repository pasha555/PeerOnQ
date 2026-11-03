using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
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

    // null = automatic (the adapter-count heuristic below), true = always GPU, false = always bitmap.
    // A person can override the heuristic from the "..." menu; the choice persists per user.
    private bool? _rendererPreference;
    private bool _rendererPreferenceLoaded;
    private const string RendererPreferenceKey = @"Software\PeerOnQ\Viewer";
    private const string RendererPreferenceValue = "Renderer";

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
        // The swap chain is the default with exactly one hardware adapter: on a bitmap-only
        // i5-5300U viewer, 1080p30 rendering could not keep up and drove the sharer's quality
        // down. Hybrid/multi-GPU machines keep the bitmap until verified there (a blank viewer is
        // worse than a slow one). PEERONQ_VIEWER_RENDERER=gpu|bitmap overrides the choice.
        EnsureRendererPreferenceLoaded();
        var setting = Environment.GetEnvironmentVariable("PEERONQ_VIEWER_RENDERER");
        // A saved preference wins; otherwise the environment variable; otherwise the heuristic.
        var forceGpu = _rendererPreference == true
            || (_rendererPreference is null && string.Equals(setting, "gpu", StringComparison.OrdinalIgnoreCase));
        var forceBitmap = _rendererPreference == false
            || (_rendererPreference is null && string.Equals(setting, "bitmap", StringComparison.OrdinalIgnoreCase));
        var (hardwareAdapters, defaultAdapter, defaultIsSoftware) = D3D11CompositionVideoPresenter.DescribeAdapters();
        SoftwareRendererProbe.ReportIfLoaded(_log, "the viewer window opened");
        var reason = forceBitmap ? "the viewer renderer is set to bitmap"
            : !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? "Windows older than 10 2004"
            : forceGpu ? null
            : defaultIsSoftware ? $"no hardware graphics adapter (default adapter: {defaultAdapter})"
            : hardwareAdapters != 1 ? $"{hardwareAdapters} hardware graphics adapters (hybrid or multi-GPU)"
            : null;
        if (reason is not null)
        {
            _log.LogInformation("Viewer {SessionId} presents with a bitmap: {Reason}", _sessionId, reason);
            SyncGpuRendererButton();
            return;
        }

        var presenter = D3D11CompositionVideoPresenter.TryCreate();
        SoftwareRendererProbe.ReportIfLoaded(_log, "the viewer Direct3D 11 presenter was probed");
        if (presenter is null)
        {
            _log.LogInformation("Viewer {SessionId} presents with a bitmap: no Direct3D 11 swap chain", _sessionId);
            SyncGpuRendererButton();
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
            SyncGpuRendererButton();
            return;
        }

        Volatile.Write(ref _gpuPresenter, presenter);
        _gpuSurfaceVisible = true;
        RemoteSwapChainPanel.Visibility = Visibility.Visible;
        RemoteImage.Visibility = Visibility.Collapsed;
        SyncGpuRendererButton();
        _log.LogInformation("Viewer {SessionId} presents through a Direct3D 11 swap chain on {Adapter} ({Reason})",
            _sessionId, defaultAdapter, forceGpu ? "forced on" : "single hardware adapter");
    }

    /// <summary>
    /// Turns GPU presentation on or off at the person's request and remembers the choice. On most
    /// laptops two graphics chips make the heuristic keep the bitmap path, which the UI thread
    /// cannot drive at 60 fps and which upscales with blur; forcing the GPU swap chain fixes both,
    /// and any device or interop failure still falls back to the bitmap automatically.
    /// </summary>
    private void OnToggleGpuRenderer(object sender, RoutedEventArgs e)
    {
        var wantGpu = GpuRendererButton.IsChecked == true;
        _rendererPreference = wantGpu;
        _rendererPreferenceLoaded = true;
        SaveRendererPreference(wantGpu);

        ShutdownGpuPresentation();
        if (wantGpu)
            InitializeGpuPresentation();
        else
            SyncGpuRendererButton();

        if (wantGpu && !_gpuSurfaceVisible)
            ConnectionText.Text = "GPU rendering is unavailable on this computer; using the standard renderer.";
    }

    /// <summary>Reflects the live renderer on the toggle without re-entering its Click handler.</summary>
    private void SyncGpuRendererButton()
    {
        if (GpuRendererButton.IsChecked != _gpuSurfaceVisible)
            GpuRendererButton.IsChecked = _gpuSurfaceVisible;
    }

    private void EnsureRendererPreferenceLoaded()
    {
        if (_rendererPreferenceLoaded) return;
        _rendererPreferenceLoaded = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RendererPreferenceKey);
            _rendererPreference = (key?.GetValue(RendererPreferenceValue) as string) switch
            {
                "gpu" => true,
                "bitmap" => false,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Viewer {SessionId} could not read the renderer preference", _sessionId);
            _rendererPreference = null;
        }
    }

    private void SaveRendererPreference(bool gpu)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RendererPreferenceKey);
            key?.SetValue(RendererPreferenceValue, gpu ? "gpu" : "bitmap", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Viewer {SessionId} could not save the renderer preference", _sessionId);
        }
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
        SyncGpuRendererButton();
    }
}
