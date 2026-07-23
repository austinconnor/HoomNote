using HoomNote.Canvas.Geometry;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Spatial;

public sealed class SpatialIndex(double cellSize = 256)
{
    private readonly Dictionary<(int X, int Y), HashSet<Guid>> _cells = [];
    private readonly Dictionary<Guid, CanvasObject> _objects = [];
    private readonly Dictionary<Guid, RectD> _bounds = [];

    public int Count => _objects.Count;

    public void Rebuild(IEnumerable<CanvasObject> objects)
    {
        _cells.Clear();
        _objects.Clear();
        _bounds.Clear();
        foreach (var canvasObject in objects) Add(canvasObject);
    }

    public void Add(CanvasObject canvasObject)
    {
        Remove(canvasObject.Id);
        _objects[canvasObject.Id] = canvasObject;
        var bounds = StrokeGeometry.GetWorldBounds(canvasObject);
        _bounds[canvasObject.Id] = bounds;
        foreach (var cell in CellsFor(bounds))
        {
            if (!_cells.TryGetValue(cell, out var ids)) _cells[cell] = ids = [];
            ids.Add(canvasObject.Id);
        }
    }

    public bool Remove(Guid objectId)
    {
        if (!_objects.Remove(objectId, out _)) return false;
        if (!_bounds.Remove(objectId, out var bounds)) return true;
        foreach (var cell in CellsFor(bounds))
        {
            if (!_cells.TryGetValue(cell, out var ids)) continue;
            ids.Remove(objectId);
            if (ids.Count == 0) _cells.Remove(cell);
        }
        return true;
    }

    public IReadOnlyList<CanvasObject> Query(RectD area)
    {
        var ids = new HashSet<Guid>();
        var results = new List<CanvasObject>();
        Query(area, ids, results);
        return results;
    }

    /// <summary>
    /// Allocation-free query overload for frame-critical callers. The supplied buffers are
    /// cleared and reused, preventing a HashSet, LINQ iterator chain, and array allocation on
    /// every pen or pan frame.
    /// </summary>
    public void Query(RectD area, HashSet<Guid> idBuffer, List<CanvasObject> resultBuffer)
    {
        idBuffer.Clear();
        resultBuffer.Clear();
        var minX = (int)Math.Floor(area.Left / cellSize);
        var maxX = (int)Math.Floor(area.Right / cellSize);
        var minY = (int)Math.Floor(area.Top / cellSize);
        var maxY = (int)Math.Floor(area.Bottom / cellSize);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
            if (_cells.TryGetValue((x, y), out var values))
                foreach (var id in values) idBuffer.Add(id);

        foreach (var id in idBuffer)
        {
            if (_bounds[id].Intersects(area)) resultBuffer.Add(_objects[id]);
        }
        resultBuffer.Sort(static (left, right) => left.ZIndex.CompareTo(right.ZIndex));
    }

    private IEnumerable<(int X, int Y)> CellsFor(RectD bounds)
    {
        var minX = (int)Math.Floor(bounds.Left / cellSize);
        var maxX = (int)Math.Floor(bounds.Right / cellSize);
        var minY = (int)Math.Floor(bounds.Top / cellSize);
        var maxY = (int)Math.Floor(bounds.Bottom / cellSize);
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
            yield return (x, y);
    }
}
