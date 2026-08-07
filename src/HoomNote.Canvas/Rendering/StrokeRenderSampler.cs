using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Rendering;

/// <summary>
/// Removes source samples that cannot affect a raster at the requested scale. Imported pen
/// formats can contain thousands of nearly coincident points per stroke; replaying all of them
/// makes page snapshots and thumbnails input-blocking without adding visible detail.
/// </summary>
public static class StrokeRenderSampler
{
    public const double DefaultMinimumPixelDistance = 0.55;

    public static IReadOnlyList<InkPoint> ForRaster(
        IReadOnlyList<InkPoint> points,
        double pixelsPerDocumentUnit,
        CancellationToken cancellationToken = default,
        double minimumPixelDistance = DefaultMinimumPixelDistance)
    {
        if (points.Count <= 2) return points;
        var scale = Math.Max(0.01, pixelsPerDocumentUnit);
        var tolerance = Math.Max(0.01, minimumPixelDistance / scale);
        var toleranceSquared = tolerance * tolerance;
        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        var segments = new Stack<(int Start, int End)>();
        segments.Push((0, points.Count - 1));
        var inspected = 0;
        while (segments.TryPop(out var segment))
        {
            if (segment.End - segment.Start <= 1) continue;
            var start = points[segment.Start];
            var end = points[segment.End];
            var largestScore = 0d;
            var splitIndex = -1;
            for (var index = segment.Start + 1; index < segment.End; index++)
            {
                if ((++inspected & 511) == 0) cancellationToken.ThrowIfCancellationRequested();
                var (distanceSquared, amount) = DistanceToSegment(points[index], start, end);
                var geometryScore = distanceSquared / toleranceSquared;
                var expectedPressure = start.Pressure + (end.Pressure - start.Pressure) * amount;
                var pressureScore = Math.Pow((points[index].Pressure - expectedPressure) / 0.04, 2);
                var score = Math.Max(geometryScore, pressureScore);
                if (score <= largestScore) continue;
                largestScore = score;
                splitIndex = index;
            }
            if (largestScore <= 1 || splitIndex < 0) continue;
            keep[splitIndex] = true;
            segments.Push((segment.Start, splitIndex));
            segments.Push((splitIndex, segment.End));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sampled = new List<InkPoint>(Math.Min(points.Count, 4_096));
        for (var index = 0; index < points.Count; index++)
            if (keep[index]) sampled.Add(points[index]);
        return sampled.Count == points.Count ? points : sampled;
    }

    private static (double DistanceSquared, double Amount) DistanceToSegment(
        InkPoint point,
        InkPoint start,
        InkPoint end)
    {
        var segmentX = end.X - start.X;
        var segmentY = end.Y - start.Y;
        var lengthSquared = segmentX * segmentX + segmentY * segmentY;
        if (lengthSquared <= double.Epsilon)
        {
            var deltaX = point.X - start.X;
            var deltaY = point.Y - start.Y;
            return (deltaX * deltaX + deltaY * deltaY, 0);
        }

        var amount = Math.Clamp(
            ((point.X - start.X) * segmentX + (point.Y - start.Y) * segmentY) / lengthSquared,
            0,
            1);
        var projectionX = start.X + segmentX * amount;
        var projectionY = start.Y + segmentY * amount;
        var distanceX = point.X - projectionX;
        var distanceY = point.Y - projectionY;
        return (distanceX * distanceX + distanceY * distanceY, amount);
    }
}
