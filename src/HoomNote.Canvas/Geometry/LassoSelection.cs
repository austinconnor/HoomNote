using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Geometry;

public static class LassoSelection
{
    public static bool Intersects(CanvasObject canvasObject, IReadOnlyList<PointD> polygon)
    {
        if (polygon.Count < 3) return false;
        var polygonBounds = RectD.FromPoints(polygon);
        var objectBounds = StrokeGeometry.GetWorldBounds(canvasObject);
        if (!objectBounds.Intersects(polygonBounds)) return false;

        if (canvasObject is InkStrokeObject stroke)
        {
            var points = stroke.Points
                .Select(point => stroke.Transform.Apply(point.Position))
                .ToArray();
            if (points.Any(point => Contains(point, polygon))) return true;
            for (var index = 1; index < points.Length; index++)
                if (IntersectsPolygon(points[index - 1], points[index], polygon)) return true;
            return false;
        }

        var corners = new[]
        {
            new PointD(objectBounds.Left, objectBounds.Top),
            new PointD(objectBounds.Right, objectBounds.Top),
            new PointD(objectBounds.Right, objectBounds.Bottom),
            new PointD(objectBounds.Left, objectBounds.Bottom)
        };
        return corners.Any(point => Contains(point, polygon)) ||
               Contains(objectBounds.Center, polygon) ||
               polygon.Any(objectBounds.Contains) ||
               corners.Select((point, index) => (Start: point, End: corners[(index + 1) % corners.Length]))
                   .Any(edge => IntersectsPolygon(edge.Start, edge.End, polygon));
    }

    public static bool Contains(PointD point, IReadOnlyList<PointD> polygon)
    {
        var inside = false;
        for (int left = 0, right = polygon.Count - 1; left < polygon.Count; right = left++)
        {
            var a = polygon[left];
            var b = polygon[right];
            if ((a.Y > point.Y) == (b.Y > point.Y)) continue;
            var crossing = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (point.X < crossing) inside = !inside;
        }
        return inside;
    }

    private static bool IntersectsPolygon(PointD start, PointD end, IReadOnlyList<PointD> polygon)
    {
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
            if (StrokeGeometry.SegmentDistance(
                    start.ToVector2(), end.ToVector2(),
                    polygon[previous].ToVector2(), polygon[index].ToVector2()) < 0.001)
                return true;
        return false;
    }
}
