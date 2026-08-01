using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Interaction;

public static class SelectionRebinder
{
    public static IReadOnlyList<CanvasObject> Rebind(
        IEnumerable<Guid> selectedIds,
        IReadOnlyCollection<CanvasObject> pageObjects)
    {
        var objectsById = pageObjects.ToDictionary(item => item.Id);
        var rebound = new List<CanvasObject>();
        var included = new HashSet<Guid>();
        foreach (var id in selectedIds)
        {
            if (!included.Add(id) || !objectsById.TryGetValue(id, out var current)) continue;
            rebound.Add(current);
        }
        return rebound;
    }
}
