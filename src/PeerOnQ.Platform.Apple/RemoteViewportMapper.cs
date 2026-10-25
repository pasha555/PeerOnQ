namespace PeerOnQ.Platform.Apple;

/// <summary>Maps a point in an aspect-fit Apple viewport to normalized remote-screen coordinates.</summary>
public static class RemoteViewportMapper
{
    public static bool TryNormalize(
        double pointerX,
        double pointerY,
        double viewportWidth,
        double viewportHeight,
        int videoWidth,
        int videoHeight,
        bool allowOutside,
        out double normalizedX,
        out double normalizedY)
    {
        normalizedX = normalizedY = 0;
        if (!double.IsFinite(pointerX) || !double.IsFinite(pointerY)
            || !double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0 || viewportHeight <= 0
            || videoWidth <= 0 || videoHeight <= 0)
        {
            return false;
        }

        var scale = Math.Min(viewportWidth / videoWidth, viewportHeight / videoHeight);
        var renderedWidth = videoWidth * scale;
        var renderedHeight = videoHeight * scale;
        var offsetX = (viewportWidth - renderedWidth) / 2;
        var offsetY = (viewportHeight - renderedHeight) / 2;
        if (!allowOutside && (pointerX < offsetX || pointerX > offsetX + renderedWidth
            || pointerY < offsetY || pointerY > offsetY + renderedHeight))
        {
            return false;
        }

        normalizedX = Math.Clamp((pointerX - offsetX) / renderedWidth, 0, 1);
        normalizedY = Math.Clamp((pointerY - offsetY) / renderedHeight, 0, 1);
        return true;
    }
}
