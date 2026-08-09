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

        var localEraser = new Vector2[eraserPath.Count];
        var minEraserX = float.PositiveInfinity;
        var minEraserY = float.PositiveInfinity;
        var maxEraserX = float.NegativeInfinity;
        var maxEraserY = float.NegativeInfinity;
        for (var eraserPointIndex = 0; eraserPointIndex < eraserPath.Count; eraserPointIndex++)
        {
            var transformed = Vector2.Transform(eraserPath[eraserPointIndex].ToVector2(), inverse);
            localEraser[eraserPointIndex] = transformed;
            minEraserX = MathF.Min(minEraserX, transformed.X);
            minEraserY = MathF.Min(minEraserY, transformed.Y);
            maxEraserX = MathF.Max(maxEraserX, transformed.X);
            maxEraserY = MathF.Max(maxEraserY, transformed.Y);
        }
        var localEraserRadius = eraserRadius / StrokeGeometry.LinearScale(stroke.Transform);
        var eraserInflation = localEraserRadius + stroke.Style.Width / 2d;
        var eraserLeft = minEraserX - eraserInflation;
        var eraserTop = minEraserY - eraserInflation;
        var eraserRight = maxEraserX + eraserInflation;
        var eraserBottom = maxEraserY + eraserInflation;
        var eraserSegments = new EraserSegmentBounds[Math.Max(0, localEraser.Length - 1)];
        for (var eraserSegmentIndex = 1; eraserSegmentIndex < localEraser.Length; eraserSegmentIndex++)
            eraserSegments[eraserSegmentIndex - 1] = new EraserSegmentBounds(
                localEraser[eraserSegmentIndex - 1], localEraser[eraserSegmentIndex]);
        var removed = new bool[stroke.Points.Count];

        if (stroke.Points.Count == 1)
        {
            var point = stroke.Points[0].Position.ToVector2();
            var threshold = localEraserRadius + stroke.Style.Width / 2d;
            for (var eraserPointIndex = 0; eraserPointIndex < localEraser.Length && !removed[0]; eraserPointIndex++)
                removed[0] = Vector2.Distance(localEraser[eraserPointIndex], point) <= threshold;
        }
        else
        {
            for (var strokeIndex = 1; strokeIndex < stroke.Points.Count; strokeIndex++)
            {
                var start = stroke.Points[strokeIndex - 1].Position.ToVector2();
                var end = stroke.Points[strokeIndex].Position.ToVector2();
                var pressure = Math.Max(stroke.Points[strokeIndex - 1].Pressure, stroke.Points[strokeIndex].Pressure);
                var threshold = localEraserRadius + stroke.Style.Width * pressure / 2d;
                var strokeLeft = Math.Min(start.X, end.X) - threshold;
                var strokeTop = Math.Min(start.Y, end.Y) - threshold;
                var strokeRight = Math.Max(start.X, end.X) + threshold;
                var strokeBottom = Math.Max(start.Y, end.Y) + threshold;
                if (strokeRight < eraserLeft || strokeLeft > eraserRight ||
                    strokeBottom < eraserTop || strokeTop > eraserBottom) continue;
                var hit = localEraser.Length == 1 &&
                          StrokeGeometry.DistanceToSegment(localEraser[0], start, end) <= threshold;
                for (var eraserIndex = 0; !hit && eraserIndex < eraserSegments.Length; eraserIndex++)
                {
                    var eraserSegment = eraserSegments[eraserIndex];
                    if (eraserSegment.MaxX + threshold < strokeLeft ||
                        eraserSegment.MinX - threshold > strokeRight ||
                        eraserSegment.MaxY + threshold < strokeTop ||
                        eraserSegment.MinY - threshold > strokeBottom) continue;
                    hit = StrokeGeometry.SegmentDistance(
                        start, end, eraserSegment.Start, eraserSegment.End) <= threshold;
                }

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

    private readonly record struct EraserSegmentBounds(
        Vector2 Start,
        Vector2 End,
        float MinX,
        float MinY,
        float MaxX,
        float MaxY)
    {
        public EraserSegmentBounds(Vector2 start, Vector2 end) : this(
            start,
            end,
            MathF.Min(start.X, end.X),
            MathF.Min(start.Y, end.Y),
            MathF.Max(start.X, end.X),
            MathF.Max(start.Y, end.Y))
        {
        }
    }
}
