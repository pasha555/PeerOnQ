using Foundation;
using UIKit;

namespace PeerOnQ.App.Apple;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    private MainViewController? _controller;

    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        AppleNativeLibraryBootstrap.Initialize();
        _controller = new MainViewController();
        Window = new UIWindow(UIScreen.MainScreen.Bounds)
        {
            RootViewController = _controller,
        };
        Window.MakeKeyAndVisible();
        return true;
    }

    public override void OnResignActivation(UIApplication application) =>
        _ = _controller?.SuspendAsync();

    public override void DidEnterBackground(UIApplication application) =>
        _ = _controller?.SuspendAsync();

    public override void WillTerminate(UIApplication application) =>
        _ = _controller?.ShutdownAsync();
}
