using System.Numerics;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Geometry;

public static class ShapeRecognizer
{
    public sealed record Recognition(ShapeKind Kind, PointD Start, PointD End);

    public static ShapeKind? Recognize(IReadOnlyList<InkPoint> samples) => RecognizeDetailed(samples)?.Kind;

    public static Recognition? RecognizeDetailed(
        IReadOnlyList<InkPoint> samples,
        bool deliberateGesture = true)
    {
        // Geometry alone cannot distinguish a handwritten "o" from a deliberately drawn
        // circle. The editor supplies intent (a short terminal hold) so ordinary writing is
        // never replaced merely because one letter happened to be geometrically regular.
        if (!deliberateGesture) return null;
        if (samples.Count < 2) return null;
        var first = samples[0].Position;
        var previous = first;
        var minX = first.X;
        var minY = first.Y;
        var maxX = first.X;
        var maxY = first.Y;
        var pathLength = 0d;
        for (var index = 1; index < samples.Count; index++)
        {
            var point = samples[index].Position;
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
            pathLength += Vector2.Distance(previous.ToVector2(), point.ToVector2());
            previous = point;
        }
        var bounds = new RectD(minX, minY, maxX - minX, maxY - minY);
        if (Math.Max(bounds.Width, bounds.Height) < 18) return null;
        if (pathLength < 18) return null;

        var end = samples[^1].Position;
        var endDistance = Vector2.Distance(first.ToVector2(), end.ToVector2());
        var diagonal = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
        var closed = endDistance <= Math.Max(18, diagonal * 0.24);
        // Draw-to-shape deliberately recognizes only closed boxes and circles. Lines, arrows,
        // and the other supported shapes remain available through the explicit shape tool so
        // ordinary handwriting is never unexpectedly replaced.
        if (!closed) return null;

        if (bounds.Width < 14 || bounds.Height < 14) return null;
        var rectangleError = 0d;
        var ellipseError = 0d;
        foreach (var sample in samples)
        {
            rectangleError += DistanceToRectangleEdge(sample.Position, bounds);
            ellipseError += EllipseError(sample.Position, bounds);
        }
        rectangleError = rectangleError / samples.Count / Math.Max(1, Math.Min(bounds.Width, bounds.Height));
        ellipseError /= samples.Count;

        if (ellipseError <= 0.12) return new Recognition(ShapeKind.Ellipse, first, end);
        if (rectangleError <= 0.125) return new Recognition(ShapeKind.Rectangle, first, end);
        if (ellipseError <= 0.23) return new Recognition(ShapeKind.Ellipse, first, end);
        return null;
    }

    private static double DistanceToRectangleEdge(PointD point, RectD bounds) => Math.Min(
        Math.Min(Math.Abs(point.X - bounds.Left), Math.Abs(point.X - bounds.Right)),
        Math.Min(Math.Abs(point.Y - bounds.Top), Math.Abs(point.Y - bounds.Bottom)));

    private static double EllipseError(PointD point, RectD bounds)
    {
        var radiusX = Math.Max(0.001, bounds.Width / 2d);
        var radiusY = Math.Max(0.001, bounds.Height / 2d);
        var x = (point.X - bounds.Center.X) / radiusX;
        var y = (point.Y - bounds.Center.Y) / radiusY;
        return Math.Abs(Math.Sqrt(x * x + y * y) - 1);
    }
}
