using UIKit;

namespace PeerOnQ.App.Apple;

public static class Application
{
    public static void Main(string[] args) =>
        UIApplication.Main(args, null, typeof(AppDelegate));
}
