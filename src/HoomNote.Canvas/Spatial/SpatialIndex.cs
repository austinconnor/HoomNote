using HoomNote.Canvas.Geometry;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Spatial;

public sealed class SpatialIndex(double cellSize = 256)
{
    private readonly Dictionary<(int X, int Y), HashSet<Guid>> _cells = [];
    private readonly Dictionary<Guid, CanvasObject> _objects = [];
    private readonly Dictionary<Guid, RectD> _bounds = [];
    private readonly Dictionary<Guid, long> _order = [];
    private long _nextOrder;

    public int Count => _objects.Count;

    public void Rebuild(IEnumerable<CanvasObject> objects)
    {
        _cells.Clear();
        _objects.Clear();
        _bounds.Clear();
        _order.Clear();
        _nextOrder = 0;
        foreach (var canvasObject in objects) Add(canvasObject);
    }

    public void Synchronize(IReadOnlyList<CanvasObject> objects)
    {
        var retainedIds = new HashSet<Guid>();
        for (var index = 0; index < objects.Count; index++)
        {
            var canvasObject = objects[index];
            retainedIds.Add(canvasObject.Id);
            if (!_objects.TryGetValue(canvasObject.Id, out var existing) || !ReferenceEquals(existing, canvasObject))
                Add(canvasObject);
            _order[canvasObject.Id] = index;
        }

        foreach (var removedId in _objects.Keys.Where(id => !retainedIds.Contains(id)).ToArray())
            Remove(removedId);
        _nextOrder = Math.Max(_nextOrder, objects.Count);
    }

    public void Add(CanvasObject canvasObject)
    {
        var bounds = StrokeGeometry.GetWorldBounds(canvasObject);
        var hadOrder = _order.TryGetValue(canvasObject.Id, out var authoredOrder);
        Remove(canvasObject.Id);
        if (!bounds.IsFinite || bounds.Width < 0 || bounds.Height < 0) return;
        _objects[canvasObject.Id] = canvasObject;
        _bounds[canvasObject.Id] = bounds;
        _order[canvasObject.Id] = hadOrder ? authoredOrder : _nextOrder++;
        foreach (var cell in CellsFor(bounds))
        {
            if (!_cells.TryGetValue(cell, out var ids)) _cells[cell] = ids = [];
            ids.Add(canvasObject.Id);
        }
    }

    public bool Remove(Guid objectId)
    {
        if (!_objects.Remove(objectId, out _)) return false;
        _order.Remove(objectId);
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
        if (!area.IsFinite || area.Width < 0 || area.Height < 0) return;
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
        resultBuffer.Sort((left, right) =>
        {
            var zOrder = left.ZIndex.CompareTo(right.ZIndex);
            return zOrder != 0 ? zOrder : _order[left.Id].CompareTo(_order[right.Id]);
        });
    }

    public bool TryGetBounds(Guid objectId, out RectD bounds) => _bounds.TryGetValue(objectId, out bounds);

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
