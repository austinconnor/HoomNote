using System.Diagnostics;
using System.Text.Json;
using HoomNote.Canvas.Geometry;
using HoomNote.Canvas.Rendering;
using HoomNote.Canvas.Spatial;
using HoomNote.Core.Documents;
using HoomNote.Infrastructure.Serialization;
using Microsoft.Data.Sqlite;

public static class RealDataRenderComparison
{
    private const int CandidatePageCount = 8;
    private const int SelectedPageCount = 3;
    private const double NavigationScale = 0.5;
    private const double CurrentMinimumRenderScale = 2;
    private const double DetailZoom = 4;
    private const double ViewportPixelWidth = 1_920;
    private const double ViewportPixelHeight = 1_080;
    private const int TilePixels = 320;

    public static async Task RunAsync(string databasePath)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("The benchmark database snapshot was not found.", databasePath);

        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();

        Console.WriteLine($"database={Path.GetFullPath(databasePath)}");
        Console.WriteLine($"database_bytes={new FileInfo(databasePath).Length}");
        var candidates = await LoadLargestSerializedPagesAsync(connection, CandidatePageCount);
        var loaded = new List<RealPage>();
        foreach (var candidate in candidates)
        {
            var page = await LoadPageAsync(connection, candidate);
            loaded.Add(page);
            Console.WriteLine(
                $"candidate document=\"{Sanitize(page.DocumentTitle)}\" page=\"{Sanitize(page.Page.Title)}\" " +
                $"payload_mb={page.PayloadBytes / 1024d / 1024d:F1} objects={page.Page.Objects.Count} " +
                $"strokes={page.StrokeCount} points={page.PointCount}");
        }

        var selected = loaded
            .OrderByDescending(page => page.PointCount)
            .ThenByDescending(page => page.PayloadBytes)
            .Take(SelectedPageCount)
            .ToArray();

        Console.WriteLine();
        Console.WriteLine("method columns: median_ms p95_ms allocated_mb source_points render_points objects");
        foreach (var page in selected)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"page document=\"{Sanitize(page.DocumentTitle)}\" page=\"{Sanitize(page.Page.Title)}\" " +
                $"size={page.Page.Size.Width:F0}x{page.Page.Size.Height:F0} points={page.PointCount}");
            RunPageComparisons(page.Page);
        }

        Console.WriteLine();
        Console.WriteLine("Win2D submission test on the largest real page");
        GpuRenderComparison.Run(selected[0].Page, NavigationScale);
    }

    private static void RunPageComparisons(NotePage page)
    {
        var allVisible = page.Objects.Where(item => !item.IsHidden).ToArray();
        var index = new SpatialIndex();
        index.Rebuild(page.Objects);
        var viewport = CenteredViewport(page, DetailZoom);
        var viewportObjects = index.Query(viewport).Where(item => !item.IsHidden).ToArray();

        PrintMeasurement("current_low_zoom", () => PrepareGeometry(allVisible, CurrentMinimumRenderScale));
        PrintMeasurement("target_scale_lod", () => PrepareGeometry(allVisible, NavigationScale));
        PrintMeasurement("full_detail_scene", () => PrepareGeometry(allVisible, DetailZoom));
        PrintMeasurement("culled_detail_view", () => PrepareGeometry(viewportObjects, DetailZoom));

        var batchPlan = ProductionBatchPlan(allVisible);
        Console.WriteLine(
            $"production_batch_plan draw_calls={batchPlan.DrawCalls} " +
            $"batched_strokes={batchPlan.BatchedStrokes} batches={batchPlan.Batches}");

        var lodCache = BuildLodCache(allVisible, NavigationScale);
        PrintMeasurement("warm_lod_cache", () => ReadLodCache(allVisible, lodCache));

        var visibleTiles = VisibleTiles(page, viewport, DetailZoom).ToArray();
        var dirtyObject = SelectDirtyObject(viewportObjects, viewport);
        var dirtyBounds = dirtyObject is null
            ? viewport
            : StrokeGeometry.GetWorldBounds(dirtyObject).Inflate(2);
        var dirtyTiles = visibleTiles.Where(tile => tile.Intersects(dirtyBounds)).ToArray();
        PrintMeasurement("rebuild_visible_tiles", () => PrepareTiles(index, visibleTiles, DetailZoom));
        PrintMeasurement("rebuild_dirty_tiles", () => PrepareTiles(index, dirtyTiles, DetailZoom));

        var sliced = MeasureTimeSliced(allVisible, NavigationScale, 2);
        Console.WriteLine(
            $"time_sliced_low_zoom total_ms={sliced.TotalMilliseconds:F2} max_slice_ms={sliced.MaximumSliceMilliseconds:F2} " +
            $"slices={sliced.Slices} render_points={sliced.RenderPoints}");
    }

    private static GeometryStats PrepareGeometry(IEnumerable<CanvasObject> objects, double scale)
    {
        long sourcePoints = 0;
        long renderPoints = 0;
        var renderedObjects = 0;
        foreach (var canvasObject in objects)
        {
            if (canvasObject is not InkStrokeObject stroke || stroke.Points.Count == 0) continue;
            sourcePoints += stroke.Points.Count;
            renderedObjects++;
            renderPoints += PrepareStroke(stroke, scale);
        }
        return new GeometryStats(sourcePoints, renderPoints, renderedObjects);
    }

    private static BatchPlanStats ProductionBatchPlan(IReadOnlyList<CanvasObject> objects)
    {
        var drawCalls = 0;
        var batchedStrokes = 0;
        var batches = 0;
        for (var index = 0; index < objects.Count;)
        {
            var count = InkBatchPolicy.CompatiblePrefixLength(
                objects, index, maximumStrokes: 128, maximumSourcePoints: 16_384);
            if (count >= 2)
            {
                drawCalls++;
                batches++;
                batchedStrokes += count;
                index += count;
            }
            else
            {
                drawCalls++;
                index++;
            }
        }
        return new BatchPlanStats(drawCalls, batchedStrokes, batches);
    }

    private static int PrepareStroke(InkStrokeObject stroke, double scale)
    {
        if (StrokeOutlineBuilder.UsesCenterlineStroke(stroke))
        {
            var fitted = stroke.Style.PreserveSourceGeometry || stroke.Style.Smoothing <= 0
                ? stroke.Points
                : StrokeOutlineBuilder.FitCenterline(stroke);
            return StrokeRenderSampler.ForRaster(fitted, scale).Count;
        }

        var sampled = StrokeRenderSampler.ForRaster(stroke.Points, scale);
        return StrokeOutlineBuilder.Build(sampled, stroke.Style).Contour.Count;
    }

    private static Dictionary<Guid, int> BuildLodCache(IEnumerable<CanvasObject> objects, double scale)
    {
        var cache = new Dictionary<Guid, int>();
        foreach (var canvasObject in objects)
            if (canvasObject is InkStrokeObject stroke && stroke.Points.Count > 0)
                cache[stroke.Id] = PrepareStroke(stroke, scale);
        return cache;
    }

    private static GeometryStats ReadLodCache(IEnumerable<CanvasObject> objects, IReadOnlyDictionary<Guid, int> cache)
    {
        long sourcePoints = 0;
        long renderPoints = 0;
        var renderedObjects = 0;
        foreach (var canvasObject in objects)
        {
            if (canvasObject is not InkStrokeObject stroke || !cache.TryGetValue(stroke.Id, out var count)) continue;
            sourcePoints += stroke.Points.Count;
            renderPoints += count;
            renderedObjects++;
        }
        return new GeometryStats(sourcePoints, renderPoints, renderedObjects);
    }

    private static GeometryStats PrepareTiles(SpatialIndex index, IReadOnlyList<RectD> tiles, double scale)
    {
        long sourcePoints = 0;
        long renderPoints = 0;
        var renderedObjects = 0;
        var ids = new HashSet<Guid>();
        var objects = new List<CanvasObject>();
        foreach (var tile in tiles)
        {
            index.Query(tile.Inflate(32), ids, objects);
            var stats = PrepareGeometry(objects.Where(item => !item.IsHidden), scale);
            sourcePoints += stats.SourcePoints;
            renderPoints += stats.RenderPoints;
            renderedObjects += stats.Objects;
        }
        return new GeometryStats(sourcePoints, renderPoints, renderedObjects);
    }

    private static TimeSlicedStats MeasureTimeSliced(
        IReadOnlyList<CanvasObject> objects,
        double scale,
        double budgetMilliseconds)
    {
        var total = Stopwatch.StartNew();
        var slice = Stopwatch.StartNew();
        var slices = 1;
        double maximumSlice = 0;
        long renderPoints = 0;
        foreach (var canvasObject in objects)
        {
            if (canvasObject is InkStrokeObject stroke && stroke.Points.Count > 0)
                renderPoints += PrepareStroke(stroke, scale);
            if (slice.Elapsed.TotalMilliseconds < budgetMilliseconds) continue;
            maximumSlice = Math.Max(maximumSlice, slice.Elapsed.TotalMilliseconds);
            slices++;
            slice.Restart();
        }
        maximumSlice = Math.Max(maximumSlice, slice.Elapsed.TotalMilliseconds);
        total.Stop();
        return new TimeSlicedStats(total.Elapsed.TotalMilliseconds, maximumSlice, slices, renderPoints);
    }

    private static void PrintMeasurement(string name, Func<GeometryStats> action)
    {
        action();
        var elapsed = new double[5];
        var allocated = new long[5];
        GeometryStats result = default;
        for (var iteration = 0; iteration < elapsed.Length; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            result = action();
            stopwatch.Stop();
            elapsed[iteration] = stopwatch.Elapsed.TotalMilliseconds;
            allocated[iteration] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(elapsed);
        Array.Sort(allocated);
        Console.WriteLine(
            $"{name} median_ms={elapsed[2]:F2} p95_ms={elapsed[^1]:F2} " +
            $"allocated_mb={allocated[2] / 1024d / 1024d:F2} source_points={result.SourcePoints} " +
            $"render_points={result.RenderPoints} objects={result.Objects}");
    }

    private static RectD CenteredViewport(NotePage page, double zoom)
    {
        var width = Math.Min(page.Size.Width, ViewportPixelWidth / zoom);
        var height = Math.Min(page.Size.Height, ViewportPixelHeight / zoom);
        return new RectD(
            Math.Max(0, (page.Size.Width - width) / 2),
            Math.Max(0, (page.Size.Height - height) / 2),
            width,
            height);
    }

    private static IEnumerable<RectD> VisibleTiles(NotePage page, RectD viewport, double scale)
    {
        var minimumX = (int)Math.Floor(viewport.Left * scale / TilePixels);
        var maximumX = (int)Math.Floor(Math.Max(viewport.Left, viewport.Right - 0.0001) * scale / TilePixels);
        var minimumY = (int)Math.Floor(viewport.Top * scale / TilePixels);
        var maximumY = (int)Math.Floor(Math.Max(viewport.Top, viewport.Bottom - 0.0001) * scale / TilePixels);
        for (var y = minimumY; y <= maximumY; y++)
        for (var x = minimumX; x <= maximumX; x++)
        {
            var left = x * TilePixels / scale;
            var top = y * TilePixels / scale;
            yield return new RectD(
                left,
                top,
                Math.Min(TilePixels / scale, page.Size.Width - left),
                Math.Min(TilePixels / scale, page.Size.Height - top));
        }
    }

    private static CanvasObject? SelectDirtyObject(IReadOnlyList<CanvasObject> objects, RectD viewport)
    {
        var center = new PointD(viewport.X + viewport.Width / 2, viewport.Y + viewport.Height / 2);
        return objects
            .Where(item => StrokeGeometry.GetWorldBounds(item).Intersects(viewport))
            .OrderBy(item => DistanceSquared(StrokeGeometry.GetWorldBounds(item), center))
            .FirstOrDefault();
    }

    private static double DistanceSquared(RectD bounds, PointD point)
    {
        var deltaX = bounds.X + bounds.Width / 2 - point.X;
        var deltaY = bounds.Y + bounds.Height / 2 - point.Y;
        return deltaX * deltaX + deltaY * deltaY;
    }

    private static async Task<IReadOnlyList<PageCandidate>> LoadLargestSerializedPagesAsync(
        SqliteConnection connection,
        int count)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, d.title, LENGTH(CAST(p.page_json AS BLOB)) +
                   COALESCE((SELECT SUM(LENGTH(CAST(j.object_json AS BLOB)))
                             FROM ink_append_journal j WHERE j.page_id = p.id), 0) AS payload_bytes
            FROM pages p
            INNER JOIN documents d ON d.id = p.document_id
            ORDER BY payload_bytes DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$count", count);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<PageCandidate>();
        while (await reader.ReadAsync())
            result.Add(new PageCandidate(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2)));
        return result;
    }

    private static async Task<RealPage> LoadPageAsync(SqliteConnection connection, PageCandidate candidate)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(page_json AS BLOB) FROM pages WHERE id = $id;";
        command.Parameters.AddWithValue("$id", candidate.PageId.ToString("D"));
        var payload = (byte[])(await command.ExecuteScalarAsync()
            ?? throw new InvalidDataException($"Page {candidate.PageId} has no payload."));
        var page = JsonSerializer.Deserialize<NotePage>(payload, HoomNoteJson.Options)
            ?? throw new InvalidDataException($"Page {candidate.PageId} could not be deserialized.");

        var ids = page.Objects.Select(item => item.Id).ToHashSet();
        await using var appendCommand = connection.CreateCommand();
        appendCommand.CommandText =
            "SELECT CAST(object_json AS BLOB) FROM ink_append_journal WHERE page_id = $id ORDER BY created_utc;";
        appendCommand.Parameters.AddWithValue("$id", candidate.PageId.ToString("D"));
        await using var reader = await appendCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var canvasObject = JsonSerializer.Deserialize<CanvasObject>(
                reader.GetFieldValue<byte[]>(0), HoomNoteJson.Options);
            if (canvasObject is not null && ids.Add(canvasObject.Id)) page.Objects.Add(canvasObject);
        }
        CanvasObjectOrdering.SortStable(page.Objects);
        var strokes = page.Objects.OfType<InkStrokeObject>().ToArray();
        return new RealPage(candidate.DocumentTitle, page, candidate.PayloadBytes,
            strokes.Length, strokes.Sum(stroke => (long)stroke.Points.Count));
    }

    private static string Sanitize(string value) =>
        value.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

    private readonly record struct PageCandidate(Guid PageId, string DocumentTitle, long PayloadBytes);
    private sealed record RealPage(
        string DocumentTitle,
        NotePage Page,
        long PayloadBytes,
        int StrokeCount,
        long PointCount);
    private readonly record struct GeometryStats(long SourcePoints, long RenderPoints, int Objects);
    private readonly record struct BatchPlanStats(int DrawCalls, int BatchedStrokes, int Batches);
    private readonly record struct TimeSlicedStats(
        double TotalMilliseconds,
        double MaximumSliceMilliseconds,
        int Slices,
        long RenderPoints);
}
