using UIKit;

namespace PeerOnQ.App.Apple;

/// <summary>Native semantic mirror of the PeerOnQ design tokens.</summary>
internal static class AppleTheme
{
    public static UIColor Background { get; } = Dynamic(0xf9, 0xf9, 0xfa, 0x15, 0x17, 0x1b);
    public static UIColor Surface { get; } = Dynamic(0xff, 0xff, 0xff, 0x1d, 0x20, 0x26);
    public static UIColor SurfaceMuted { get; } = Dynamic(0xf1, 0xf3, 0xf5, 0x25, 0x29, 0x30);
    public static UIColor Primary { get; } = Dynamic(0x30, 0x91, 0x69, 0x38, 0xb7, 0x7b);
    public static UIColor OnPrimary { get; } = UIColor.White;
    public static UIColor Text { get; } = Dynamic(0x20, 0x22, 0x27, 0xe2, 0xe5, 0xe9);
    public static UIColor MutedText { get; } = Dynamic(0x69, 0x6e, 0x78, 0x9a, 0xa0, 0xaa);
    public static UIColor Border { get; } = Dynamic(0xe8, 0xea, 0xed, 0x33, 0x37, 0x40);
    public static UIColor Destructive { get; } = Dynamic(0xd9, 0x3d, 0x42, 0xf0, 0x61, 0x66);
    public static UIColor Viewport { get; } = UIColor.FromRGB(0x0c, 0x0f, 0x13);

    private static UIColor Dynamic(byte lr, byte lg, byte lb, byte dr, byte dg, byte db) =>
        UIColor.FromDynamicProvider(traits => traits.UserInterfaceStyle == UIUserInterfaceStyle.Dark
            ? UIColor.FromRGB(dr, dg, db)
            : UIColor.FromRGB(lr, lg, lb));
}
