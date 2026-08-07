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
        var sampled = new List<InkPoint>(Math.Min(points.Count, 4_096));
        sampled.Add(points[0]);
        var lastKept = points[0];
        for (var index = 1; index < points.Count - 1; index++)
        {
            if ((index & 511) == 0) cancellationToken.ThrowIfCancellationRequested();
            var point = points[index];
            var deltaX = point.X - lastKept.X;
            var deltaY = point.Y - lastKept.Y;
            var pressureChanged = Math.Abs(point.Pressure - lastKept.Pressure) >= 0.04f;
            var next = points[index + 1];
            var firstX = point.X - lastKept.X;
            var firstY = point.Y - lastKept.Y;
            var secondX = next.X - point.X;
            var secondY = next.Y - point.Y;
            var cross = Math.Abs(firstX * secondY - firstY * secondX);
            var firstLength = Math.Sqrt(firstX * firstX + firstY * firstY);
            var secondLength = Math.Sqrt(secondX * secondX + secondY * secondY);
            var sharpTurn = firstLength > double.Epsilon && secondLength > double.Epsilon &&
                            cross / (firstLength * secondLength) > 0.35;
            var cornerChanged = sharpTurn || cross > tolerance * Math.Max(tolerance, secondLength);
            if (deltaX * deltaX + deltaY * deltaY < toleranceSquared &&
                !pressureChanged && !cornerChanged) continue;

            sampled.Add(point);
            lastKept = point;
        }
        sampled.Add(points[^1]);
        cancellationToken.ThrowIfCancellationRequested();
        return sampled.Count == points.Count ? points : sampled;
    }
}
