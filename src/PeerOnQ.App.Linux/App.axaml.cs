using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PeerOnQ.App.Linux;

public sealed partial class App : Avalonia.Application
{
    private LinuxAppServices? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startupCancellation = new CancellationTokenSource();
            var startupToken = startupCancellation.Token;
            var window = new MainWindow();
            desktop.MainWindow = window;
            var startupTask = Task.CompletedTask;
            window.CancelStartupAsync = () =>
            {
                startupCancellation.Cancel();
                return startupTask;
            };
            window.Opened += (_, _) => startupTask = InitializeWindowAsync(window, startupToken);
            desktop.Exit += (_, _) =>
            {
                startupCancellation.Cancel();
                startupCancellation.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeWindowAsync(MainWindow window, CancellationToken startupToken)
    {
        try
        {
            _services = await LinuxAppServices.CreateAsync(startupToken);
            startupToken.ThrowIfCancellationRequested();
            await window.AttachAsync(_services, startupToken);
        }
        catch (OperationCanceledException) when (startupToken.IsCancellationRequested)
        {
            // The closing window awaits this task before the desktop lifetime exits.
        }
        catch (Exception exception)
        {
            if (!startupToken.IsCancellationRequested)
                window.ShowStartupError(exception);
        }
        finally
        {
            if (startupToken.IsCancellationRequested && _services is not null)
                await _services.DisposeAsync();
        }
    }
}
