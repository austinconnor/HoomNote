using System.Numerics;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Geometry;

public static class SegmentEraser
{
    public static IReadOnlyList<InkStrokeObject> Erase(
        InkStrokeObject stroke,
        IReadOnlyList<PointD> eraserPath,
        double eraserRadius)
    {
        if (stroke.Points.Count == 0 || eraserPath.Count == 0 || eraserRadius <= 0)
        {
            return [stroke];
        }

        if (!Matrix3x2.Invert(stroke.Transform.ToMatrix(), out var inverse))
        {
            return [stroke];
        }

        var localEraser = eraserPath
            .Select(point => Vector2.Transform(point.ToVector2(), inverse))
            .ToArray();
        var removed = new bool[stroke.Points.Count];

        if (stroke.Points.Count == 1)
        {
            removed[0] = localEraser.Any(point =>
                Vector2.Distance(point, stroke.Points[0].Position.ToVector2()) <=
                eraserRadius + stroke.Style.Width / 2d);
        }
        else
        {
            for (var strokeIndex = 1; strokeIndex < stroke.Points.Count; strokeIndex++)
            {
                var start = stroke.Points[strokeIndex - 1].Position.ToVector2();
                var end = stroke.Points[strokeIndex].Position.ToVector2();
                var pressure = Math.Max(stroke.Points[strokeIndex - 1].Pressure, stroke.Points[strokeIndex].Pressure);
                var threshold = eraserRadius + stroke.Style.Width * pressure / 2d;
                var hit = localEraser.Length == 1 &&
                          StrokeGeometry.DistanceToSegment(localEraser[0], start, end) <= threshold;
                for (var eraserIndex = 1; !hit && eraserIndex < localEraser.Length; eraserIndex++)
                    hit = StrokeGeometry.SegmentDistance(start, end,
                        localEraser[eraserIndex - 1], localEraser[eraserIndex]) <= threshold;

                if (hit)
                {
                    removed[strokeIndex - 1] = true;
                    removed[strokeIndex] = true;
                }
            }
        }

        var removedCount = 0;
        foreach (var value in removed)
            if (value) removedCount++;
        if (removedCount == 0) return [stroke];
        if (removedCount == removed.Length) return [];

        var fragments = new List<InkStrokeObject>();
        var index = 0;
        while (index < stroke.Points.Count)
        {
            while (index < stroke.Points.Count && removed[index]) index++;
            var start = index;
            while (index < stroke.Points.Count && !removed[index]) index++;
            var length = index - start;
            if (length == 0) continue;
            var points = new List<InkPoint>(length);
            for (var pointIndex = start; pointIndex < index; pointIndex++)
                points.Add(stroke.Points[pointIndex]);
            AddFragment(points);
        }
        return fragments;

        void AddFragment(List<InkPoint> points)
        {
            if (points.Count == 0) return;
            fragments.Add(stroke with
            {
                Id = Guid.NewGuid(),
                ParentStrokeId = stroke.ParentStrokeId ?? stroke.Id,
                // The run list is never mutated after this call, so transfer it directly rather
                // than copying every surviving value into a second large allocation.
                Points = points
            });
        }
    }
}
