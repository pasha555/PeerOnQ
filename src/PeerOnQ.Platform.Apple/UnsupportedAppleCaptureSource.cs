using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Platform.Apple;

/// <summary>Fail-closed capture boundary for the viewer-only Apple applications.</summary>
public sealed class UnsupportedAppleCaptureSource(AppleClientPlatform platform) : IScreenCaptureSource
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
            $"This PeerOnQ {AppleClientCapabilityProfile.ToWirePlatform(platform)} build is viewer-only and cannot share the local screen."));

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
