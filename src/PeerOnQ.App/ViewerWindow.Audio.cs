using Microsoft.UI.Xaml;
using PeerOnQ.Application.Collaboration;

namespace PeerOnQ.App;

public sealed partial class ViewerWindow
{
    private SystemAudioSession? _systemAudio;

    public void AttachSystemAudio(SystemAudioSession? audio)
    {
        if (audio is null || audio.SessionId != _sessionId || ReferenceEquals(_systemAudio, audio)) return;
        if (_systemAudio is not null) return;
        _systemAudio = audio;
        audio.StateChanged += OnSystemAudioStateChanged;
        UpdateSystemSoundButton();
    }

    private void OnSystemAudioStateChanged(object? sender, EventArgs args) =>
        _dispatcher.TryEnqueue(UpdateSystemSoundButton);

    private void UpdateSystemSoundButton()
    {
        if (_closed || _systemAudio is not { } audio) return;
        SystemSoundButton.Visibility = Visibility.Visible;
        SystemSoundButton.IsEnabled = audio.IsAvailable;
        SystemSoundButton.Label = !audio.IsAvailable ? "System sound unavailable"
            : audio.IsMuted ? "Play system sound" : "Mute system sound";
    }

    private void OnSystemSound(object sender, RoutedEventArgs args)
    {
        if (_systemAudio is not { } audio) return;
        try { audio.SetMuted(!audio.IsMuted); }
        catch (Exception)
        {
            audio.RevokeLocalPermission();
        }
        UpdateSystemSoundButton();
    }

    private void DetachSystemAudio()
    {
        if (_systemAudio is not { } audio) return;
        audio.StateChanged -= OnSystemAudioStateChanged;
        if (!audio.IsMuted) audio.SetMuted(true);
        _systemAudio = null;
    }
}
