using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Platform.Linux;

/// <summary>
/// Fail-closed capture source for the viewer-only Linux composition. The Linux client does not
/// advertise host capabilities, and any accidental attempt to capture remains explicit.
/// </summary>
public sealed class UnsupportedLinuxCaptureSource : IScreenCaptureSource
{
    public bool IsCapturing => false;

    public CaptureTargetInfo? Target => null;

    public event EventHandler<CapturedFrame>? FrameArrived
    {
        add { }
        remove { }
    }

    public event EventHandler<CaptureStoppedReason>? CaptureStopped
    {
        add { }
        remove { }
    }

    public Task StartAsync(CaptureRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException(new PlatformNotSupportedException(
            "This PeerOnQ Linux build is viewer-only and cannot share the local screen."));

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
