namespace HoomNote.Canvas.Interaction;

public static class ViewportPanBounds
{
    public static float ClampHorizontal(
        double pageWidth,
        double zoom,
        double viewportWidth,
        float requestedPan)
    {
        if (!double.IsFinite(pageWidth) || pageWidth <= 0 ||
            !double.IsFinite(zoom) || zoom <= 0 ||
            !double.IsFinite(viewportWidth) || viewportWidth <= 0 ||
            !float.IsFinite(requestedPan))
            return 0;

        var overflow = Math.Max(0, pageWidth * zoom - viewportWidth);
        if (overflow <= 0) return 0;
        var maximumPan = (float)Math.Min(float.MaxValue, overflow / 2d);
        return Math.Clamp(requestedPan, -maximumPan, maximumPan);
    }
}
