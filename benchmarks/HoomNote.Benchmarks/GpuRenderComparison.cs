using System.Diagnostics;
using System.Numerics;
using HoomNote.Canvas.Geometry;
using HoomNote.Canvas.Rendering;
using HoomNote.Core.Documents;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

public static class GpuRenderComparison
{
    private const int BatchStrokeLimit = 128;
    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);

    public static void Run(NotePage page, double scale)
    {
        try
        {
            using var device = new CanvasDevice();
            using var strokeStyle = new CanvasStrokeStyle
            {
                StartCap = CanvasCapStyle.Round,
                EndCap = CanvasCapStyle.Round,
                LineJoin = CanvasLineJoin.Round
            };
            var strokes = page.Objects.OfType<InkStrokeObject>()
                .Where(stroke => !stroke.IsHidden && stroke.Points.Count > 0)
                .ToArray();

            Print("gpu_per_stroke_current", () => RenderPerStroke(
                device, strokeStyle, page, strokes, scale, Math.Max(scale, 2)));
            Print("gpu_per_stroke_target_lod", () => RenderPerStroke(
                device, strokeStyle, page, strokes, scale, scale));

            var prepared = Prepare(strokes, scale);
            Print("gpu_prepared_per_stroke", () => RenderPreparedPerStroke(
                device, strokeStyle, page, prepared, scale));
            Print("gpu_prepared_batches", () => RenderPreparedBatches(
                device, strokeStyle, page, prepared, scale));

            var cacheStopwatch = Stopwatch.StartNew();
            var cachedBatches = BuildCachedBatches(device, prepared, strokeStyle);
            cacheStopwatch.Stop();
            try
            {
                Console.WriteLine(
                    $"gpu_cached_batch_build elapsed_ms={cacheStopwatch.Elapsed.TotalMilliseconds:F2} " +
                    $"batches={cachedBatches.Count}");
                Print("gpu_cached_batches_warm", () => RenderCachedBatches(
                    device, page, cachedBatches, scale));
            }
            finally
            {
                foreach (var batch in cachedBatches) batch.Geometry.Dispose();
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"gpu_test_unavailable type={exception.GetType().Name} message=\"{Sanitize(exception.Message)}\"");
        }
    }

    private static RenderStats RenderPerStroke(
        CanvasDevice device,
        CanvasStrokeStyle strokeStyle,
        NotePage page,
        IReadOnlyList<InkStrokeObject> strokes,
        double outputScale,
        double samplingScale)
    {
        using var target = CreateTarget(device, page, outputScale);
        using var session = target.CreateDrawingSession();
        session.Clear(Transparent);
        session.Transform = Matrix3x2.CreateScale((float)outputScale);
        long points = 0;
        foreach (var stroke in strokes)
            points += DrawStroke(session, strokeStyle, stroke, samplingScale);
        return new RenderStats(strokes.Count, strokes.Count, points);
    }

    private static RenderStats RenderPreparedPerStroke(
        CanvasDevice device,
        CanvasStrokeStyle strokeStyle,
        NotePage page,
        IReadOnlyList<PreparedStroke> strokes,
        double outputScale)
    {
        using var target = CreateTarget(device, page, outputScale);
        using var session = target.CreateDrawingSession();
        session.Clear(Transparent);
        session.Transform = Matrix3x2.CreateScale((float)outputScale);
        long points = 0;
        foreach (var stroke in strokes)
        {
            using var geometry = CreateGeometry(session, [stroke]);
            DrawGeometry(session, strokeStyle, geometry, stroke);
            points += stroke.PointCount;
        }
        return new RenderStats(strokes.Count, strokes.Count, points);
    }

    private static RenderStats RenderPreparedBatches(
        CanvasDevice device,
        CanvasStrokeStyle strokeStyle,
        NotePage page,
        IReadOnlyList<PreparedStroke> strokes,
        double outputScale)
    {
        using var target = CreateTarget(device, page, outputScale);
        using var session = target.CreateDrawingSession();
        session.Clear(Transparent);
        session.Transform = Matrix3x2.CreateScale((float)outputScale);
        var batchCount = 0;
        long points = 0;
        foreach (var batch in CompatibleBatches(strokes))
        {
            using var geometry = CreateGeometry(session, batch);
            DrawGeometry(session, strokeStyle, geometry, batch[0]);
            batchCount++;
            points += batch.Sum(stroke => (long)stroke.PointCount);
        }
        return new RenderStats(strokes.Count, batchCount, points);
    }

    private static IReadOnlyList<CachedBatch> BuildCachedBatches(
        CanvasDevice device,
        IReadOnlyList<PreparedStroke> strokes,
        CanvasStrokeStyle strokeStyle)
    {
        var result = new List<CachedBatch>();
        try
        {
            foreach (var batch in CompatibleBatches(strokes))
            {
                using var geometry = CreateGeometry(device, batch);
                var first = batch[0];
                var cached = first.IsCenterline
                    ? CanvasCachedGeometry.CreateStroke(geometry, first.Width, strokeStyle, 0.3f)
                    : CanvasCachedGeometry.CreateFill(geometry, 0.3f);
                result.Add(new CachedBatch(cached, first.Color));
            }
            return result;
        }
        catch
        {
            foreach (var item in result) item.Geometry.Dispose();
            throw;
        }
    }

    private static RenderStats RenderCachedBatches(
        CanvasDevice device,
        NotePage page,
        IReadOnlyList<CachedBatch> batches,
        double outputScale)
    {
        using var target = CreateTarget(device, page, outputScale);
        using var session = target.CreateDrawingSession();
        session.Clear(Transparent);
        session.Transform = Matrix3x2.CreateScale((float)outputScale);
        foreach (var batch in batches) session.DrawCachedGeometry(batch.Geometry, batch.Color);
        return new RenderStats(0, batches.Count, 0);
    }

    private static List<PreparedStroke> Prepare(IReadOnlyList<InkStrokeObject> strokes, double scale)
    {
        var result = new List<PreparedStroke>(strokes.Count);
        foreach (var stroke in strokes)
        {
            var color = ParseColor(stroke.Style.Color);
            if (StrokeOutlineBuilder.UsesCenterlineStroke(stroke))
            {
                var fitted = stroke.Style.PreserveSourceGeometry || stroke.Style.Smoothing <= 0
                    ? stroke.Points
                    : StrokeOutlineBuilder.FitCenterline(stroke);
                var sampled = StrokeRenderSampler.ForRaster(fitted, scale).ToArray();
                if (sampled.Length >= 2)
                    result.Add(new PreparedStroke(true,
                        StrokeOutlineBuilder.VectorCenterlineWidth(stroke.Style), color,
                        sampled, null));
            }
            else
            {
                var sampled = StrokeRenderSampler.ForRaster(stroke.Points, scale);
                var contour = StrokeOutlineBuilder.Build(sampled, stroke.Style).Contour.ToArray();
                if (contour.Length >= 3)
                    result.Add(new PreparedStroke(false, 0, color, null, contour));
            }
        }
        return result;
    }

    private static int DrawStroke(
        CanvasDrawingSession session,
        CanvasStrokeStyle strokeStyle,
        InkStrokeObject stroke,
        double samplingScale)
    {
        var color = ParseColor(stroke.Style.Color);
        if (StrokeOutlineBuilder.UsesCenterlineStroke(stroke))
        {
            var fitted = stroke.Style.PreserveSourceGeometry || stroke.Style.Smoothing <= 0
                ? stroke.Points
                : StrokeOutlineBuilder.FitCenterline(stroke);
            var sampled = StrokeRenderSampler.ForRaster(fitted, samplingScale);
            if (sampled.Count < 2) return sampled.Count;
            using var geometry = CreateCenterlineGeometry(session, sampled);
            session.DrawGeometry(geometry, color,
                StrokeOutlineBuilder.VectorCenterlineWidth(stroke.Style), strokeStyle);
            return sampled.Count;
        }

        var renderPoints = StrokeRenderSampler.ForRaster(stroke.Points, samplingScale);
        var outline = StrokeOutlineBuilder.Build(renderPoints, stroke.Style);
        if (outline.Contour.Count < 3) return outline.Contour.Count;
        using var outlineGeometry = CreateOutlineGeometry(session, outline.Contour);
        session.FillGeometry(outlineGeometry, color);
        return outline.Contour.Count;
    }

    private static IEnumerable<IReadOnlyList<PreparedStroke>> CompatibleBatches(
        IReadOnlyList<PreparedStroke> strokes)
    {
        var start = 0;
        while (start < strokes.Count)
        {
            var first = strokes[start];
            var count = 1;
            while (start + count < strokes.Count && count < BatchStrokeLimit &&
                   Compatible(first, strokes[start + count]))
                count++;
            var batch = new PreparedStroke[count];
            for (var index = 0; index < count; index++) batch[index] = strokes[start + index];
            yield return batch;
            start += count;
        }
    }

    private static bool Compatible(PreparedStroke left, PreparedStroke right) =>
        left.IsCenterline == right.IsCenterline &&
        Math.Abs(left.Width - right.Width) < 0.0001f &&
        left.Color.Equals(right.Color);

    private static CanvasGeometry CreateGeometry(
        ICanvasResourceCreator creator,
        IReadOnlyList<PreparedStroke> strokes)
    {
        using var path = new CanvasPathBuilder(creator);
        if (!strokes[0].IsCenterline)
            path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        foreach (var stroke in strokes)
        {
            if (stroke.Centerline is { Length: >= 2 } centerline)
            {
                path.BeginFigure(centerline[0].Position.ToVector2());
                for (var index = 1; index < centerline.Length; index++)
                    path.AddLine(centerline[index].Position.ToVector2());
                path.EndFigure(CanvasFigureLoop.Open);
            }
            else if (stroke.Contour is { Length: >= 3 } contour)
            {
                path.BeginFigure(contour[0].ToVector2());
                for (var index = 1; index < contour.Length; index++)
                    path.AddLine(contour[index].ToVector2());
                path.EndFigure(CanvasFigureLoop.Closed);
            }
        }
        return CanvasGeometry.CreatePath(path);
    }

    private static CanvasGeometry CreateCenterlineGeometry(
        ICanvasResourceCreator creator,
        IReadOnlyList<InkPoint> points)
    {
        using var path = new CanvasPathBuilder(creator);
        path.BeginFigure(points[0].Position.ToVector2());
        for (var index = 1; index < points.Count; index++)
            path.AddLine(points[index].Position.ToVector2());
        path.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(path);
    }

    private static CanvasGeometry CreateOutlineGeometry(
        ICanvasResourceCreator creator,
        IReadOnlyList<PointD> points)
    {
        using var path = new CanvasPathBuilder(creator);
        path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        path.BeginFigure(points[0].ToVector2());
        for (var index = 1; index < points.Count; index++)
            path.AddLine(points[index].ToVector2());
        path.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(path);
    }

    private static void DrawGeometry(
        CanvasDrawingSession session,
        CanvasStrokeStyle strokeStyle,
        CanvasGeometry geometry,
        PreparedStroke stroke)
    {
        if (stroke.IsCenterline)
            session.DrawGeometry(geometry, stroke.Color, stroke.Width, strokeStyle);
        else
            session.FillGeometry(geometry, stroke.Color);
    }

    private static CanvasRenderTarget CreateTarget(CanvasDevice device, NotePage page, double scale) =>
        new(device,
            Math.Max(1, (float)(page.Size.Width * scale)),
            Math.Max(1, (float)(page.Size.Height * scale)),
            96);

    private static Color ParseColor(string color)
    {
        var value = color.TrimStart('#');
        if (value.Length == 6 && uint.TryParse(value,
                System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return Black;
    }

    private static void Print(string name, Func<RenderStats> action)
    {
        action();
        var times = new double[5];
        RenderStats stats = default;
        for (var iteration = 0; iteration < times.Length; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            stats = action();
            stopwatch.Stop();
            times[iteration] = stopwatch.Elapsed.TotalMilliseconds;
        }
        Array.Sort(times);
        Console.WriteLine(
            $"{name} median_ms={times[2]:F2} p95_ms={times[^1]:F2} " +
            $"strokes={stats.Strokes} draw_calls={stats.DrawCalls} render_points={stats.RenderPoints}");
    }

    private static string Sanitize(string value) =>
        value.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

    private sealed record PreparedStroke(
        bool IsCenterline,
        float Width,
        Color Color,
        InkPoint[]? Centerline,
        PointD[]? Contour)
    {
        public int PointCount => Centerline?.Length ?? Contour?.Length ?? 0;
    }

    private sealed record CachedBatch(CanvasCachedGeometry Geometry, Color Color);
    private readonly record struct RenderStats(int Strokes, int DrawCalls, long RenderPoints);
}
