using PeerOnQ.Application.Collaboration;

namespace PeerOnQ.Media.Tests;

/// <summary>Optional external diagnostic observer for the real normal-transfer fixture.</summary>
public static class LoopbackFileTransferDiagnostics
{
    // Test execution contexts remain independent; ordinary tests install no observer.
    public static AsyncLocal<Func<WebRtcMediaSession, WebRtcMediaSession,
        FileTransferService, FileTransferService, IDisposable>?> ObserverFactory { get; } = new();
}
