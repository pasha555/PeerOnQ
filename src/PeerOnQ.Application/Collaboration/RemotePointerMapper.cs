namespace PeerOnQ.Application.Collaboration;

/// <summary>The rendered remote-frame rectangle in viewer-local device-independent pixels.</summary>
public readonly record struct RemoteContentBounds(double X, double Y, double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y)
        && double.IsFinite(Width) && double.IsFinite(Height)
        && Width > 0 && Height > 0;
}

/// <summary>
/// Maps viewer pointer positions into the normalized coordinates accepted by the remote-input
/// protocol. The caller supplies the real transformed image bounds so scrolling and DPI scaling
/// remain part of the visual tree instead of being reconstructed from the window size.
/// </summary>
public static class RemotePointerMapper
{
    public static (double Width, double Height) ActualSizeInDips(
        int frameWidth,
        int frameHeight,
        double rasterizationScale)
    {
        if (frameWidth <= 0 || frameHeight <= 0
            || !double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            return default;

        return (frameWidth / rasterizationScale, frameHeight / rasterizationScale);
    }

    public static RemoteContentBounds UniformFit(
        RemoteContentBounds container,
        int frameWidth,
        int frameHeight)
    {
        if (!container.IsValid || frameWidth <= 0 || frameHeight <= 0) return default;

        var scale = Math.Min(container.Width / frameWidth, container.Height / frameHeight);
        var width = frameWidth * scale;
        var height = frameHeight * scale;
        return new RemoteContentBounds(
            container.X + ((container.Width - width) / 2),
            container.Y + ((container.Height - height) / 2),
            width,
            height);
    }

    public static RemoteContentBounds UniformFill(
        RemoteContentBounds container,
        int frameWidth,
        int frameHeight)
    {
        if (!container.IsValid || frameWidth <= 0 || frameHeight <= 0) return default;

        var scale = Math.Max(container.Width / frameWidth, container.Height / frameHeight);
        var width = frameWidth * scale;
        var height = frameHeight * scale;
        return new RemoteContentBounds(
            container.X + ((container.Width - width) / 2),
            container.Y + ((container.Height - height) / 2),
            width,
            height);
    }

    public static bool TryNormalize(
        double pointerX,
        double pointerY,
        RemoteContentBounds content,
        out double normalizedX,
        out double normalizedY)
    {
        normalizedX = 0;
        normalizedY = 0;
        if (!content.IsValid || !double.IsFinite(pointerX) || !double.IsFinite(pointerY)) return false;

        if (pointerX < content.X || pointerX > content.X + content.Width
            || pointerY < content.Y || pointerY > content.Y + content.Height)
            return false;

        normalizedX = Math.Clamp((pointerX - content.X) / content.Width, 0, 1);
        normalizedY = Math.Clamp((pointerY - content.Y) / content.Height, 0, 1);
        return true;
    }
}
