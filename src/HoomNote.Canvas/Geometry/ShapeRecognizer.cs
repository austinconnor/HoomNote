using System.Numerics;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Geometry;

public static class ShapeRecognizer
{
    public sealed record Recognition(ShapeKind Kind, PointD Start, PointD End);

    public static ShapeKind? Recognize(IReadOnlyList<InkPoint> samples) =>
        RecognizeDetailed(samples)?.Kind;

    public static Recognition? RecognizeDetailed(
        IReadOnlyList<InkPoint> samples,
        bool deliberateGesture = true)
    {
        if (samples.Count < 2) return null;
        var source = RemoveAdjacentDuplicates(samples.Select(sample => sample.Position));
        if (source.Count < 4) return null;
        var bounds = RectD.FromPoints(source);
        var minimumDimension = Math.Min(bounds.Width, bounds.Height);
        var diagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
        if (Math.Max(bounds.Width, bounds.Height) < 18 || minimumDimension < 14) return null;

        var pathLength = PathLength(source);
        if (pathLength < 18) return null;
        var first = source[0];
        var end = source[^1];
        var endDistance = Vector2.Distance(first.ToVector2(), end.ToVector2());
        if (endDistance > Math.Max(18, diagonal * 0.24)) return null;
        if (!deliberateGesture && minimumDimension < 60) return null;

        var points = ResampleClosed(source, 96);
        var rectangleError = RectangleError(points, bounds) / minimumDimension;
        var ellipseError = points.Average(point => EllipseError(point, bounds));
        var starError = StarError(points, bounds) / minimumDimension;

        // Stars must be materially more star-like than the smooth/box candidates. This stops
        // arbitrary closed scribbles from falling through to the old broad oval fallback.
        if (starError <= 0.105 && starError < rectangleError * 0.82 &&
            starError < ellipseError * 0.82)
            return new Recognition(ShapeKind.Star, first, end);

        // Corners place rectangle samples directly on an edge, while ellipse samples stay
        // radially consistent. Compare the fits before applying strict absolute thresholds.
        if (rectangleError <= 0.095 && rectangleError < ellipseError * 0.92)
            return new Recognition(ShapeKind.Rectangle, first, end);
        if (ellipseError <= 0.135 && ellipseError < rectangleError * 1.15)
            return new Recognition(ShapeKind.Ellipse, first, end);
        return null;
    }

    private static List<PointD> RemoveAdjacentDuplicates(IEnumerable<PointD> points)
    {
        var result = new List<PointD>();
        foreach (var point in points)
        {
            if (result.Count == 0 ||
                Vector2.DistanceSquared(result[^1].ToVector2(), point.ToVector2()) > 0.01f)
                result.Add(point);
        }
        return result;
    }

    private static double PathLength(IReadOnlyList<PointD> points)
    {
        var length = 0d;
        for (var index = 1; index < points.Count; index++)
            length += Vector2.Distance(points[index - 1].ToVector2(), points[index].ToVector2());
        return length;
    }

    private static IReadOnlyList<PointD> ResampleClosed(IReadOnlyList<PointD> source, int count)
    {
        var closed = source.ToList();
        if (Vector2.DistanceSquared(closed[0].ToVector2(), closed[^1].ToVector2()) > 0.01f)
            closed.Add(closed[0]);
        var cumulative = new double[closed.Count];
        for (var index = 1; index < closed.Count; index++)
            cumulative[index] = cumulative[index - 1] +
                Vector2.Distance(closed[index - 1].ToVector2(), closed[index].ToVector2());
        var total = cumulative[^1];
        if (total <= 0.001) return closed;

        var result = new PointD[count];
        var segment = 1;
        for (var sample = 0; sample < count; sample++)
        {
            var distance = total * sample / count;
            while (segment + 1 < cumulative.Length && cumulative[segment] < distance) segment++;
            var startDistance = cumulative[segment - 1];
            var segmentLength = Math.Max(0.0001, cumulative[segment] - startDistance);
            var amount = (distance - startDistance) / segmentLength;
            var start = closed[segment - 1];
            var end = closed[segment];
            result[sample] = new PointD(
                start.X + (end.X - start.X) * amount,
                start.Y + (end.Y - start.Y) * amount);
        }
        return result;
    }

    private static double RectangleError(IReadOnlyList<PointD> points, RectD bounds)
    {
        var corners = new[]
        {
            new PointD(bounds.Left, bounds.Top),
            new PointD(bounds.Right, bounds.Top),
            new PointD(bounds.Right, bounds.Bottom),
            new PointD(bounds.Left, bounds.Bottom)
        };
        return AverageDistanceToSegments(points, corners);
    }

    private static double StarError(IReadOnlyList<PointD> points, RectD bounds)
    {
        var best = double.PositiveInfinity;
        foreach (var innerRatio in new[] { 0.35, 0.45, 0.55 })
        for (var rotationStep = 0; rotationStep < 10; rotationStep++)
        {
            var rotation = -Math.PI / 2d + rotationStep * Math.PI / 25d;
            var vertices = new PointD[10];
            for (var index = 0; index < vertices.Length; index++)
            {
                var inner = index % 2 == 1;
                var angle = rotation + index * Math.PI / 5d;
                var radius = inner ? innerRatio : 1d;
                vertices[index] = new PointD(
                    bounds.Center.X + Math.Cos(angle) * bounds.Width / 2d * radius,
                    bounds.Center.Y + Math.Sin(angle) * bounds.Height / 2d * radius);
            }
            best = Math.Min(best, AverageDistanceToSegments(points, vertices));
        }
        return best;
    }

    private static double AverageDistanceToSegments(
        IReadOnlyList<PointD> points,
        IReadOnlyList<PointD> vertices)
    {
        var total = 0d;
        foreach (var point in points)
        {
            var distance = double.PositiveInfinity;
            for (var index = 0; index < vertices.Count; index++)
                distance = Math.Min(distance, StrokeGeometry.DistanceToSegment(
                    point.ToVector2(), vertices[index].ToVector2(),
                    vertices[(index + 1) % vertices.Count].ToVector2()));
            total += distance;
        }
        return total / Math.Max(1, points.Count);
    }

    private static double EllipseError(PointD point, RectD bounds)
    {
        var radiusX = Math.Max(0.001, bounds.Width / 2d);
        var radiusY = Math.Max(0.001, bounds.Height / 2d);
        var x = (point.X - bounds.Center.X) / radiusX;
        var y = (point.Y - bounds.Center.Y) / radiusY;
        return Math.Abs(Math.Sqrt(x * x + y * y) - 1);
    }
}
