using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Interaction;

public enum ContinuousPageSlot
{
    Previous = -1,
    Current = 0,
    Next = 1
}

public static class ContinuousPageLayout
{
    public static RectD CurrentBounds(
        SizeD pageSize,
        double zoom,
        double panX,
        double panY,
        SizeD viewport)
    {
        var width = pageSize.Width * zoom;
        var height = pageSize.Height * zoom;
        return new RectD(
            (viewport.Width - width) / 2d + panX,
            (viewport.Height - height) / 2d + panY,
            width,
            height);
    }

    public static RectD AdjacentBounds(
        RectD currentBounds,
        SizeD adjacentPageSize,
        double zoom,
        double panX,
        SizeD viewport,
        bool aboveCurrentPage,
        double gap)
    {
        var width = adjacentPageSize.Width * zoom;
        var height = adjacentPageSize.Height * zoom;
        var top = aboveCurrentPage
            ? currentBounds.Y - gap - height
            : currentBounds.Bottom + gap;
        return new RectD(
            (viewport.Width - width) / 2d + panX,
            top,
            width,
            height);
    }

    public static ContinuousPageSlot? HitTest(
        PointD point,
        RectD currentBounds,
        RectD? previousBounds,
        RectD? nextBounds)
    {
        if (currentBounds.Contains(point)) return ContinuousPageSlot.Current;
        if (previousBounds?.Contains(point) == true) return ContinuousPageSlot.Previous;
        if (nextBounds?.Contains(point) == true) return ContinuousPageSlot.Next;
        return null;
    }

    public static PointD PageTranslationForSameViewportPosition(
        RectD sourceBounds,
        RectD destinationBounds,
        double zoom)
    {
        var safeZoom = double.IsFinite(zoom) && zoom > 0.0001 ? zoom : 1;
        return new PointD(
            (sourceBounds.X - destinationBounds.X) / safeZoom,
            (sourceBounds.Y - destinationBounds.Y) / safeZoom);
    }

    public static double PanYForPageTop(
        double pageTop,
        SizeD pageSize,
        double zoom,
        double viewportHeight) =>
        pageTop - (viewportHeight - pageSize.Height * zoom) / 2d;
}
