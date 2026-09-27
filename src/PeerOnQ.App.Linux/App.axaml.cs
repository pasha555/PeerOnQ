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
            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try
                {
                    _services = await LinuxAppServices.CreateAsync();
                    await window.AttachAsync(_services);
                }
                catch (Exception exception)
                {
                    window.ShowStartupError(exception);
                }
            };
            desktop.ShutdownRequested += async (_, _) =>
            {
                if (_services is not null)
                    await _services.DisposeAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
