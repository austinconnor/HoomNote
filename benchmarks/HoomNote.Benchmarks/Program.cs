using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using HoomNote.Canvas.Geometry;
using HoomNote.Canvas.Spatial;
using HoomNote.Core.Documents;

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
}
