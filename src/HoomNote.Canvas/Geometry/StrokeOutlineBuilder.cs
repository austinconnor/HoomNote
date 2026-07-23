using System.Numerics;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Geometry;

public sealed record StrokeOutline(
    IReadOnlyList<InkPoint> Centerline,
    IReadOnlyList<PointD> Contour,
    IReadOnlyList<float> Widths);

public static class StrokeOutlineBuilder
{
    public const float CenterlineStrokeThreshold = 1.35f;
    private const double MinimumSampleDistance = 0.35;
    private const double TargetSplineStep = 1.35;
    private const int CapSegments = 10;

    /// <summary>
    /// Source-fitted and subpixel ink are more robust as stroked vector centerlines. Expanding
    /// dense imported paths into a single ribbon polygon can self-intersect at caps and tight
    /// turns, producing visible holes even with winding fill.
    /// </summary>
    public static bool UsesCenterlineStroke(InkStyle requestedStyle)
    {
        var style = requestedStyle.Normalize();
        // Smoothing == 0 was used by Samsung imports before PreserveSourceGeometry was added
        // to the schema. Keep that legacy detection so already-saved notebooks do not fall back
        // to the self-intersecting pressure ribbon renderer.
        var sourceFidelityMode = style.PreserveSourceGeometry || style.Smoothing <= 0;
        return style.Tool != InkToolKind.Highlighter &&
               (!style.PressureEnabled || sourceFidelityMode || style.Width <= CenterlineStrokeThreshold);
    }

    public static bool UsesCenterlineStroke(InkStrokeObject stroke)
    {
        if (UsesCenterlineStroke(stroke.Style)) return true;
        return IsLegacySamsungStroke(stroke);
    }

    public static IReadOnlyList<InkPoint> FitCenterline(InkStrokeObject stroke)
    {
        var preserveSourceSamples = stroke.Style.PreserveSourceGeometry ||
                                    stroke.Style.Smoothing <= 0 || IsLegacySamsungStroke(stroke);
        return FitCenterline(stroke.Points, preserveSourceSamples ? 0 : stroke.Style.Smoothing);
    }

    private static bool IsLegacySamsungStroke(InkStrokeObject stroke)
    {
        if (stroke.Style.Tool == InkToolKind.Highlighter || stroke.Points.Count < 2 ||
            stroke.Points[0].TimestampMicroseconds != 0) return false;

        // Legacy Samsung imports predate both source-fidelity style fields. Their decoder has
        // always emitted a deterministic 1 ms timeline beginning at zero for each stroke, which
        // lets existing persisted notebooks opt into the stable centerline renderer without a
        // destructive re-import or an ambiguous width heuristic.
        var inspected = Math.Min(stroke.Points.Count, 16);
        for (var index = 1; index < inspected; index++)
        {
            if (stroke.Points[index].TimestampMicroseconds != index * 1_000L) return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the stored document-space width. The canvas transform scales this width with the
    /// page, preserving the relative weight and spacing of fine handwriting at every zoom level.
    /// </summary>
    public static float VisibleCenterlineWidth(InkStyle requestedStyle, double viewportZoom)
    {
        _ = viewportZoom;
        return requestedStyle.Normalize().Width;
    }

    public static float VectorCenterlineWidth(InkStyle requestedStyle)
    {
        return requestedStyle.Normalize().Width;
    }

    public static StrokeOutline Build(IReadOnlyList<InkPoint> input, InkStyle requestedStyle)
    {
        var style = requestedStyle.Normalize();
        var centerline = FitCenterline(input, style.Smoothing);
        if (centerline.Count == 0) return new StrokeOutline([], [], []);

        var widths = SmoothWidths(centerline, style);
        if (centerline.Count == 1)
        {
            var radius = widths[0] / 2d;
            var circle = Enumerable.Range(0, CapSegments * 2)
                .Select(index =>
                {
                    var angle = Math.Tau * index / (CapSegments * 2d);
                    return new PointD(centerline[0].X + Math.Cos(angle) * radius,
                        centerline[0].Y + Math.Sin(angle) * radius);
                }).ToArray();
            return new StrokeOutline(centerline, circle, widths);
        }

        var tangents = BuildTangents(centerline);
        var left = new PointD[centerline.Count];
        var right = new PointD[centerline.Count];
        for (var index = 0; index < centerline.Count; index++)
        {
            var tangent = tangents[index];
            var normal = new Vector2(-tangent.Y, tangent.X);
            var radius = widths[index] / 2f;
            left[index] = new PointD(centerline[index].X + normal.X * radius,
                centerline[index].Y + normal.Y * radius);
            right[index] = new PointD(centerline[index].X - normal.X * radius,
                centerline[index].Y - normal.Y * radius);
        }

        var contour = new List<PointD>(centerline.Count * 2 + CapSegments * 2);
        contour.AddRange(left);
        AppendCap(contour, centerline[^1].Position, tangents[^1], widths[^1] / 2f, atEnd: true);
        for (var index = right.Length - 1; index >= 0; index--) contour.Add(right[index]);
        AppendCap(contour, centerline[0].Position, tangents[0], widths[0] / 2f, atEnd: false);
        return new StrokeOutline(centerline, contour, widths);
    }

    public static IReadOnlyList<InkPoint> FitCenterline(IReadOnlyList<InkPoint> input, float smoothing)
    {
        if (input.Count == 0) return [];
        // A zero smoothing value is an explicit fidelity mode for imported vector ink. Samsung
        // samples are intentionally dense; distance-filtering them before this check used to
        // collapse a whole pen-down path into a single point.
        // Source-fidelity imports are normalized at their ingestion boundary. Returning the
        // original immutable point references avoids duplicating hundreds of thousands of
        // InkPoint records whenever a Samsung page cache or thumbnail is rendered.
        if (smoothing <= 0) return input;

        var points = Deduplicate(input);
        if (points.Count < 3) return points;

        var filtered = SymmetricFilter(points, smoothing);
        var result = new List<InkPoint>(filtered.Count * 3) { filtered[0] };
        for (var index = 0; index < filtered.Count - 1; index++)
        {
            var p0 = filtered[Math.Max(0, index - 1)];
            var p1 = filtered[index];
            var p2 = filtered[index + 1];
            var p3 = filtered[Math.Min(filtered.Count - 1, index + 2)];
            var distance = Vector2.Distance(p1.Position.ToVector2(), p2.Position.ToVector2());
            var steps = Math.Clamp((int)Math.Ceiling(distance / TargetSplineStep), 1, 64);
            for (var step = 1; step <= steps; step++)
            {
                var t = step / (double)steps;
                var position = CatmullRom(p0.Position.ToVector2(), p1.Position.ToVector2(),
                    p2.Position.ToVector2(), p3.Position.ToVector2(), (float)t);
                result.Add(new InkPoint(position.X, position.Y,
                    Lerp(p1.Pressure, p2.Pressure, t),
                    Lerp(p1.TiltX, p2.TiltX, t),
                    Lerp(p1.TiltY, p2.TiltY, t),
                    (long)(p1.TimestampMicroseconds + (p2.TimestampMicroseconds - p1.TimestampMicroseconds) * t)));
            }
        }
        return result;
    }

    public static float EffectiveWidth(InkPoint point, InkStyle requestedStyle)
    {
        var style = requestedStyle.Normalize();
        if (!style.PressureEnabled) return style.Width;
        var pressure = Math.Clamp(point.Pressure, 0.01f, 1f);
        var curved = 0.12f + 0.88f * MathF.Pow(pressure, 0.72f);
        var multiplier = 1f + style.PressureSensitivity * (curved - 1f);
        return Math.Max(0.15f, style.Width * multiplier);
    }

    private static List<InkPoint> Deduplicate(IReadOnlyList<InkPoint> input)
    {
        var result = new List<InkPoint>(input.Count);
        foreach (var raw in input)
        {
            var point = raw.Normalize();
            if (result.Count == 0)
            {
                result.Add(point);
                continue;
            }
            if (Vector2.Distance(result[^1].Position.ToVector2(), point.Position.ToVector2()) < MinimumSampleDistance)
            {
                continue;
            }
            result.Add(point);
        }
        // Always preserve the physical end of the stroke even when its final movement is below
        // the sampling threshold.
        var finalPoint = input[^1].Normalize();
        if (result.Count > 0 && result[^1].Position != finalPoint.Position) result.Add(finalPoint);
        return result;
    }

    private static List<InkPoint> SymmetricFilter(IReadOnlyList<InkPoint> points, float smoothing)
    {
        var amount = 0.08 + Math.Clamp(smoothing, 0, 1) * 0.24;
        var result = new List<InkPoint>(points.Count) { points[0] };
        for (var index = 1; index < points.Count - 1; index++)
        {
            var previous = points[index - 1];
            var current = points[index];
            var next = points[index + 1];
            var neighborWeight = amount / 2d;
            result.Add(current with
            {
                X = current.X * (1 - amount) + (previous.X + next.X) * neighborWeight,
                Y = current.Y * (1 - amount) + (previous.Y + next.Y) * neighborWeight,
                Pressure = (float)(current.Pressure * (1 - amount) + (previous.Pressure + next.Pressure) * neighborWeight)
            });
        }
        result.Add(points[^1]);
        return result;
    }

    private static float[] SmoothWidths(IReadOnlyList<InkPoint> centerline, InkStyle style)
    {
        var widths = centerline.Select(point => EffectiveWidth(point, style)).ToArray();
        if (widths.Length < 3) return widths;
        var forward = widths[0];
        for (var index = 1; index < widths.Length; index++)
        {
            forward += (widths[index] - forward) * 0.24f;
            widths[index] = forward;
        }
        var backward = widths[^1];
        for (var index = widths.Length - 2; index >= 0; index--)
        {
            backward += (widths[index] - backward) * 0.24f;
            widths[index] = (widths[index] + backward) / 2f;
        }
        return widths;
    }

    private static Vector2[] BuildTangents(IReadOnlyList<InkPoint> centerline)
    {
        var tangents = new Vector2[centerline.Count];
        var fallback = Vector2.UnitX;
        for (var index = 0; index < centerline.Count; index++)
        {
            var previous = centerline[Math.Max(0, index - 1)].Position.ToVector2();
            var next = centerline[Math.Min(centerline.Count - 1, index + 1)].Position.ToVector2();
            var tangent = next - previous;
            if (tangent.LengthSquared() > 0.000001f) fallback = Vector2.Normalize(tangent);
            tangents[index] = fallback;
        }
        return tangents;
    }

    private static void AppendCap(List<PointD> contour, PointD center, Vector2 tangent, float radius, bool atEnd)
    {
        var normalAngle = Math.Atan2(tangent.X, -tangent.Y);
        for (var step = 1; step < CapSegments; step++)
        {
            var amount = step / (double)CapSegments;
            var angle = atEnd ? normalAngle - Math.PI * amount : normalAngle - Math.PI - Math.PI * amount;
            contour.Add(new PointD(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius));
        }
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t +
                       (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static float Lerp(float left, float right, double amount) =>
        (float)(left + (right - left) * amount);
}
