using Microsoft.UI.Xaml;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.App;

public sealed partial class MainWindow
{
    private bool _systemSoundChangeInProgress;

    /// <summary>
    /// The in-session sound indicator on both sides: system sound, this side's microphone and
    /// the peer talking, or why sound and voice are off for the session.
    /// </summary>
    private string? DescribeSessionAudio(SessionId sessionId, string peerName)
    {
        if (_services?.Coordinator is not { } coordinator) return null;
        if (coordinator.AudioUnavailableReason(sessionId) is not null) return "Sound off (policy or device)";
        var parts = new List<string>(3);
        if (coordinator.CollaborationFor(sessionId)?.SystemAudio is { IsAvailable: true } audio
            && (audio.Role == SessionRole.Sharer ? audio.IsCaptureEnabled : !audio.IsMuted))
            parts.Add("Sound on");
        if (coordinator.IsVoiceEnabled(sessionId)) parts.Add("Mic on");
        if (coordinator.IsPeerVoiceActive(sessionId)) parts.Add($"{peerName} talking");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private bool IsSessionAudioActive(SessionId sessionId) => DescribeSessionAudio(sessionId, "Peer") is { } status
                                                              && !status.StartsWith("Sound off", StringComparison.Ordinal);

    private void AttachSystemAudio(SessionCollaborationContext context)
    {
        _viewers.GetValueOrDefault(context.SessionId)?.AttachSystemAudio(context.SystemAudio);
        if (context.SystemAudio is { } audio)
            audio.StateChanged += (_, _) => _dispatcher.TryEnqueue(() => RefreshHostSystemAudio());
    }

    private void RefreshHostSystemAudio()
    {
        if (_collaboration?.SystemAudio is not { Role: SessionRole.Sharer } audio)
        {
            SystemSoundHostButton.Visibility = Visibility.Collapsed;
            return;
        }
        SystemSoundHostButton.Visibility = Visibility.Visible;
        SystemSoundHostButton.IsEnabled = audio.IsAvailable && !_systemSoundChangeInProgress;
        SystemSoundHostButton.Content = !audio.IsAvailable ? "System sound stopped; start a new session"
            : audio.IsCaptureEnabled ? "Stop system sound for this session" : "Resume system sound";
    }

    private async void OnSystemSoundHost(object sender, RoutedEventArgs args)
    {
        if (_collaboration?.SystemAudio is not { Role: SessionRole.Sharer } audio) return;
        if (_systemSoundChangeInProgress) return;
        _systemSoundChangeInProgress = true;
        RefreshHostSystemAudio();
        try
        {
            if (audio.IsCaptureEnabled) audio.RevokeLocalPermission();
            else await audio.SetCaptureEnabledAsync(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            audio.RevokeLocalPermission();
            ShowError($"System sound remains off: {ex.Message}");
        }
        finally
        {
            _systemSoundChangeInProgress = false;
            RefreshHostSystemAudio();
        }
    }
}
