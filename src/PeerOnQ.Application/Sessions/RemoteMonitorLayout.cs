using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Application.Sessions;

/// <summary>A rectangle of a decoded frame, in frame pixels.</summary>
public readonly record struct RemoteFrameCrop(int X, int Y, int Width, int Height);

/// <summary>
/// Places every display of a sharer's combined "All displays" view, so a viewer can show each
/// remote display in its own window from the one combined stream. It needs the virtual-desktop
/// positions that 0.9.83+ sharers publish; without them there is no layout and the viewer keeps
/// its single window.
/// </summary>
public sealed class RemoteMonitorLayout
{
    // Generous bounds for real desktops; anything outside is treated as a malformed list.
    private const int MaximumCoordinate = 1_000_000;
    private const int MaximumExtent = 65_536;

    private RemoteMonitorLayout(CaptureTargetInfo combined, IReadOnlyList<CaptureTargetInfo> monitors)
    {
        Combined = combined;
        Monitors = monitors;
    }

    /// <summary>The combined entry the sharer must stream for separate windows.</summary>
    public CaptureTargetInfo Combined { get; }

    /// <summary>The individual displays, in the sharer's order (primary first).</summary>
    public IReadOnlyList<CaptureTargetInfo> Monitors { get; }

    /// <summary>
    /// Finds the combined entry: the placed display whose rectangle is exactly the union of at
    /// least two other placed displays.
    /// </summary>
    public static RemoteMonitorLayout? TryCreate(IReadOnlyList<CaptureTargetInfo> displays)
    {
        var placed = displays.Where(IsPlaced).ToArray();
        if (placed.Length < 3) return null;

        foreach (var candidate in placed)
        {
            var others = placed.Where(display => !ReferenceEquals(display, candidate)).ToArray();
            var left = others.Min(display => (long)display.X!.Value);
            var top = others.Min(display => (long)display.Y!.Value);
            var right = others.Max(display => (long)display.X!.Value + display.Width);
            var bottom = others.Max(display => (long)display.Y!.Value + display.Height);
            if (candidate.X == left && candidate.Y == top
                && candidate.Width == right - left && candidate.Height == bottom - top)
                return new RemoteMonitorLayout(candidate, others);
        }

        return null;
    }

    /// <summary>The part of a combined frame that shows <paramref name="monitor"/>.</summary>
    public RemoteFrameCrop CropFor(int monitor, int frameWidth, int frameHeight)
    {
        var display = Monitors[monitor];
        var left = Scale(display.X!.Value - Combined.X!.Value, frameWidth, Combined.Width);
        var top = Scale(display.Y!.Value - Combined.Y!.Value, frameHeight, Combined.Height);
        var right = Scale(display.X.Value - Combined.X.Value + display.Width, frameWidth, Combined.Width);
        var bottom = Scale(display.Y.Value - Combined.Y.Value + display.Height, frameHeight, Combined.Height);
        return new RemoteFrameCrop(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>
    /// Maps a normalized point inside <paramref name="monitor"/> to the normalized point of the
    /// combined view, which is what the sharer injects into.
    /// </summary>
    public (double X, double Y) ToCombined(int monitor, double x, double y)
    {
        var display = Monitors[monitor];
        var combinedX = (display.X!.Value - Combined.X!.Value + Math.Clamp(x, 0, 1) * display.Width) / Combined.Width;
        var combinedY = (display.Y!.Value - Combined.Y!.Value + Math.Clamp(y, 0, 1) * display.Height) / Combined.Height;
        return (Math.Clamp(combinedX, 0, 1), Math.Clamp(combinedY, 0, 1));
    }

    private static int Scale(long offset, int frameExtent, int combinedExtent) =>
        (int)Math.Clamp(offset * frameExtent / combinedExtent, 0, frameExtent);

    // Range patterns, not Math.Abs: a peer-supplied int.MinValue must not throw.
    private static bool IsPlaced(CaptureTargetInfo display) =>
        display is
        {
            X: >= -MaximumCoordinate and <= MaximumCoordinate,
            Y: >= -MaximumCoordinate and <= MaximumCoordinate,
            Width: > 0 and <= MaximumExtent,
            Height: > 0 and <= MaximumExtent,
        };
}
