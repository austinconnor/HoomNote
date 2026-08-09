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
            if (stroke.Points.Count == 0) return false;
            var previous = stroke.Transform.Apply(stroke.Points[0].Position);
            if (Contains(previous, polygon)) return true;
            for (var index = 1; index < stroke.Points.Count; index++)
            {
                var current = stroke.Transform.Apply(stroke.Points[index].Position);
                if (Contains(current, polygon) ||
                    RectD.FromPoints([previous, current]).Intersects(polygonBounds) &&
                    IntersectsPolygon(previous, current, polygon)) return true;
                previous = current;
            }
            return false;
        }

        var corners = objectBounds.Corners();
        return corners.Any(point => Contains(point, polygon)) ||
               Contains(objectBounds.Center, polygon) ||
               polygon.Any(objectBounds.Contains) ||
               corners.Select((point, index) => (Start: point, End: corners[(index + 1) % corners.Length]))
                   .Any(edge => IntersectsPolygon(edge.Start, edge.End, polygon));
    }

    public static IReadOnlyList<PointD> Simplify(IReadOnlyList<PointD> polygon, double tolerance)
    {
        if (polygon.Count < 4 || tolerance <= 0) return polygon;
        var retained = new bool[polygon.Count];
        retained[0] = retained[^1] = true;
        var ranges = new Stack<(int Start, int End)>();
        ranges.Push((0, polygon.Count - 1));
        while (ranges.TryPop(out var range))
        {
            var furthestDistance = 0d;
            var furthestIndex = -1;
            for (var index = range.Start + 1; index < range.End; index++)
            {
                var distance = StrokeGeometry.DistanceToSegment(
                    polygon[index].ToVector2(), polygon[range.Start].ToVector2(), polygon[range.End].ToVector2());
                if (distance <= furthestDistance) continue;
                furthestDistance = distance;
                furthestIndex = index;
            }
            if (furthestIndex < 0 || furthestDistance <= tolerance) continue;
            retained[furthestIndex] = true;
            ranges.Push((range.Start, furthestIndex));
            ranges.Push((furthestIndex, range.End));
        }
        var result = new List<PointD>();
        for (var index = 0; index < polygon.Count; index++)
            if (retained[index]) result.Add(polygon[index]);
        return result;
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
