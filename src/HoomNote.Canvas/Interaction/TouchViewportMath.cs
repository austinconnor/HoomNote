using System.Numerics;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Interaction;

public readonly record struct TouchViewportState(double Zoom, Vector2 Pan);

/// <summary>
/// Pure viewport math shared by the WinUI touch controller and unit tests. The page-space
/// anchor captured beneath the initial finger centroid remains beneath the moving centroid
/// throughout a pinch, so simultaneous scale and translation never produce a visual jump.
/// </summary>
public static class TouchViewportMath
{
    public static TouchViewportState Pan(
        double zoom,
        Vector2 startPan,
        PointD startScreenPoint,
        PointD currentScreenPoint)
    {
        var pan = startPan + new Vector2(
            (float)(currentScreenPoint.X - startScreenPoint.X),
            (float)(currentScreenPoint.Y - startScreenPoint.Y));
        return new TouchViewportState(zoom, pan);
    }

    public static TouchViewportState Pinch(
        double startZoom,
        double scale,
        PointD pageAnchor,
        PointD screenAnchor,
        SizeD pageSize,
        SizeD viewportSize,
        double minimumZoom = 0.08,
        double maximumZoom = 8)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var zoom = Math.Clamp(startZoom * scale, minimumZoom, maximumZoom);
        var centeredX = (viewportSize.Width - pageSize.Width * zoom) / 2d;
        var centeredY = (viewportSize.Height - pageSize.Height * zoom) / 2d;
        var pan = new Vector2(
            (float)(screenAnchor.X - pageAnchor.X * zoom - centeredX),
            (float)(screenAnchor.Y - pageAnchor.Y * zoom - centeredY));
        return new TouchViewportState(zoom, pan);
    }

    /// <summary>
    /// Scales around the centroid where the gesture began. Translation of two fingers with an
    /// unchanged spread is intentionally ignored; one finger owns panning.
    /// </summary>
    public static TouchViewportState PinchOnly(
        double startZoom,
        double scale,
        PointD pageAnchor,
        PointD gestureOrigin,
        PointD currentCentroid,
        SizeD pageSize,
        SizeD viewportSize,
        double minimumZoom = 0.08,
        double maximumZoom = 8)
    {
        _ = currentCentroid;
        return Pinch(startZoom, scale, pageAnchor, gestureOrigin, pageSize, viewportSize,
            minimumZoom, maximumZoom);
    }
}
