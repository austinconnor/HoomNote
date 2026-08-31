using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using HoomNote.Canvas.Geometry;
using HoomNote.Canvas.Spatial;
using HoomNote.Core.Documents;

if (args.Length >= 2 && string.Equals(args[0], "--real-data", StringComparison.OrdinalIgnoreCase))
{
    await RealDataRenderComparison.RunAsync(args[1]);
    return;
}

BenchmarkRunner.Run<CanvasBenchmarks>();

[MemoryDiagnoser]
[ShortRunJob]
public class CanvasBenchmarks
{
    private InkStrokeObject _largeStroke = null!;
    private PointD[] _eraser = null!;
    private SpatialIndex _index = null!;
    private HashSet<Guid> _queryIds = null!;
    private List<CanvasObject> _queryResults = null!;
    private CanvasObject[] _denseInkPage = null!;
    private SpatialIndex _denseIndex = null!;
    private PointD[] _longEraser = null!;
    private PointD[] _lasso = null!;
    private InkStrokeObject _outlineStroke = null!;

    [GlobalSetup]
    public void Setup()
    {
        _largeStroke = new InkStrokeObject
        {
            Points = Enumerable.Range(0, 250_000)
                .Select(index => new InkPoint(index * 0.02, 500 + Math.Sin(index * 0.01) * 100, 0.6f))
                .ToList()
        };
        _eraser = [new PointD(2_500, 300), new PointD(2_500, 700)];
        _index = new SpatialIndex();
        _index.Rebuild(Enumerable.Range(0, 10_000).Select(index => new ShapeObject
        {
            Bounds = new RectD(index % 100 * 80, index / 100 * 80, 40, 40)
        }));
        _queryIds = [];
        _queryResults = [];
        _denseInkPage = Enumerable.Range(0, 200).Select(strokeIndex => (CanvasObject)new InkStrokeObject
        {
            Points = Enumerable.Range(0, 2_500).Select(pointIndex => new InkPoint(
                pointIndex * 0.2, strokeIndex * 6 + Math.Sin(pointIndex * 0.02) * 3, 0.6f)).ToList(),
            ZIndex = strokeIndex
        }).ToArray();
        _denseIndex = new SpatialIndex();
        _longEraser = Enumerable.Range(0, 200)
            .Select(index => new PointD(index * 12, 500 + Math.Sin(index * 0.2) * 80)).ToArray();
        _lasso = Enumerable.Range(0, 300)
            .Select(index => new PointD(2_500 + Math.Cos(index * Math.PI * 2 / 300) * 2_000,
                600 + Math.Sin(index * Math.PI * 2 / 300) * 550)).ToArray();
        _outlineStroke = (InkStrokeObject)_denseInkPage[0];
    }

    [Benchmark]
    public IReadOnlyList<InkStrokeObject> EraseAcross250KPoints() =>
        SegmentEraser.Erase(_largeStroke, _eraser, 8);

    [Benchmark]
    public IReadOnlyList<CanvasObject> QueryVisibleViewport() =>
        _index.Query(new RectD(1_000, 1_000, 1_920, 1_080));

    [Benchmark]
    public int QueryVisibleViewportBuffered()
    {
        _index.Query(new RectD(1_000, 1_000, 1_920, 1_080), _queryIds, _queryResults);
        return _queryResults.Count;
    }

    [Benchmark]
    public int RebuildSpatialIndexFor500KInkPoints()
    {
        _denseIndex.Rebuild(_denseInkPage);
        return _denseIndex.Count;
    }

    [Benchmark]
    public IReadOnlyList<InkStrokeObject> EraseLongScrubAcross250KPoints() =>
        SegmentEraser.Erase(_largeStroke, _longEraser, 8);

    [Benchmark]
    public bool LassoDenseStrokeWith300Vertices() =>
        LassoSelection.Intersects(_largeStroke, _lasso);

    [Benchmark]
    public StrokeOutline BuildOutlineForPointHeavyStroke() =>
        StrokeOutlineBuilder.Build(_outlineStroke.Points, _outlineStroke.Style);
}
