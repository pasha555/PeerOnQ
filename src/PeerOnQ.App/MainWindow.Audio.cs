using Microsoft.UI.Xaml;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Sessions;

namespace PeerOnQ.App;

public sealed partial class MainWindow
{
    private bool _systemSoundChangeInProgress;
    private void RefreshSystemSoundRequestAvailability()
    {
        if (RequestSystemSoundCheckBox is null) return;
        RequestSystemSoundCheckBox.IsEnabled = !UnattendedConnectionToggle.IsOn && !SupportInvitationToggle.IsOn;
        if (!RequestSystemSoundCheckBox.IsEnabled) RequestSystemSoundCheckBox.IsChecked = false;
    }

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
