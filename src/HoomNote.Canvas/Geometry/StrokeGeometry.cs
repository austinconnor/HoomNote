using System.Numerics;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Geometry;

public static class StrokeGeometry
{
    /// <summary>
    /// Removes pointer wobble below the detail visible when the stroke was captured. The
    /// tolerance is expressed in screen pixels and converted to document space, so writing at
    /// page-fit zoom does not reveal magnified sampling noise when inspected later.
    /// </summary>
    public static IReadOnlyList<InkPoint> StabilizeForViewport(
        IReadOnlyList<InkPoint> points,
        double viewportZoom,
        float smoothing)
    {
        if (points.Count < 3 || smoothing <= 0) return points.ToArray();
        var normalizedZoom = Math.Clamp(
            double.IsFinite(viewportZoom) ? viewportZoom : 1,
            0.08,
            8);
        var normalizedSmoothing = Math.Clamp(
            float.IsFinite(smoothing) ? smoothing : 0.72f,
            0,
            1);
        // Keep correction below half a screen pixel even at maximum smoothing. This is enough
        // to remove magnified sampling chatter without a perceptible pen-up shift.
        var screenTolerance = 0.12 + normalizedSmoothing * 0.32;
        var documentTolerance = Math.Clamp(screenTolerance / normalizedZoom, 0.05, 12);

        var retained = new bool[points.Count];
        retained[0] = true;
        retained[^1] = true;
        var ranges = new Stack<(int Start, int End)>();
        ranges.Push((0, points.Count - 1));
        while (ranges.TryPop(out var range))
        {
            if (range.End - range.Start < 2) continue;
            var start = points[range.Start].Position.ToVector2();
            var end = points[range.End].Position.ToVector2();
            var furthestDistance = 0d;
            var furthestIndex = -1;
            for (var index = range.Start + 1; index < range.End; index++)
            {
                var distance = DistanceToSegment(points[index].Position.ToVector2(), start, end);
                if (distance <= furthestDistance) continue;
                furthestDistance = distance;
                furthestIndex = index;
            }
            if (furthestIndex < 0 || furthestDistance <= documentTolerance) continue;
            retained[furthestIndex] = true;
            ranges.Push((range.Start, furthestIndex));
            ranges.Push((furthestIndex, range.End));
        }

        var result = new List<InkPoint>(points.Count);
        for (var index = 0; index < points.Count; index++)
            if (retained[index]) result.Add(points[index]);
        return result;
    }

    public static IReadOnlyList<InkPoint> Smooth(IReadOnlyList<InkPoint> points, int passes = 2)
    {
        if (points.Count < 3 || passes <= 0) return points.ToArray();

        IReadOnlyList<InkPoint> current = points;
        for (var pass = 0; pass < passes; pass++)
        {
            var result = new List<InkPoint>(current.Count * 2) { current[0] };
            for (var index = 0; index < current.Count - 1; index++)
            {
                var left = current[index];
                var right = current[index + 1];
                result.Add(Interpolate(left, right, 0.25));
                result.Add(Interpolate(left, right, 0.75));
            }

            result.Add(current[^1]);
            current = result;
        }

        return current;
    }

    public static RectD GetWorldBounds(CanvasObject canvasObject)
    {
        var local = canvasObject.LocalBounds;
        var corners = new[]
        {
            new PointD(local.Left, local.Top),
            new PointD(local.Right, local.Top),
            new PointD(local.Right, local.Bottom),
            new PointD(local.Left, local.Bottom)
        }.Select(canvasObject.Transform.Apply);
        return RectD.FromPoints(corners);
    }

    public static bool HitTest(CanvasObject canvasObject, PointD worldPoint, double tolerance = 8)
    {
        if (!Matrix3x2.Invert(canvasObject.Transform.ToMatrix(), out var inverse)) return false;
        var localVector = Vector2.Transform(worldPoint.ToVector2(), inverse);
        var localPoint = new PointD(localVector.X, localVector.Y);

        if (canvasObject is InkStrokeObject stroke)
        {
            var radius = tolerance + stroke.Style.Width / 2d;
            for (var index = 1; index < stroke.Points.Count; index++)
            {
                if (DistanceToSegment(localPoint.ToVector2(), stroke.Points[index - 1].Position.ToVector2(),
                        stroke.Points[index].Position.ToVector2()) <= radius)
                {
                    return true;
                }
            }

            return stroke.Points.Count == 1 &&
                   Vector2.Distance(localPoint.ToVector2(), stroke.Points[0].Position.ToVector2()) <= radius;
        }

        return canvasObject.LocalBounds.Inflate(tolerance).Contains(localPoint);
    }

    public static double DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= float.Epsilon) return Vector2.Distance(point, start);
        var t = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0, 1);
        return Vector2.Distance(point, start + segment * t);
    }

    public static double SegmentDistance(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
    {
        if (SegmentsIntersect(a0, a1, b0, b1)) return 0;
        return Math.Min(
            Math.Min(DistanceToSegment(a0, b0, b1), DistanceToSegment(a1, b0, b1)),
            Math.Min(DistanceToSegment(b0, a0, a1), DistanceToSegment(b1, a0, a1)));
    }

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Cross(Vector2 left, Vector2 right) => left.X * right.Y - left.Y * right.X;
        var r = b - a;
        var s = d - c;
        var denominator = Cross(r, s);
        if (Math.Abs(denominator) < 0.000001f) return false;
        var u = Cross(c - a, r) / denominator;
        var t = Cross(c - a, s) / denominator;
        return t is >= 0 and <= 1 && u is >= 0 and <= 1;
    }

    private static InkPoint Interpolate(InkPoint left, InkPoint right, double amount) => new(
        left.X + (right.X - left.X) * amount,
        left.Y + (right.Y - left.Y) * amount,
        (float)(left.Pressure + (right.Pressure - left.Pressure) * amount),
        (float)(left.TiltX + (right.TiltX - left.TiltX) * amount),
        (float)(left.TiltY + (right.TiltY - left.TiltY) * amount),
        (long)(left.TimestampMicroseconds + (right.TimestampMicroseconds - left.TimestampMicroseconds) * amount));
}
