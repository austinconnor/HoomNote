namespace HoomNote.Core.Documents;

public static class CanvasObjectOrdering
{
    public static void SortStable(List<CanvasObject> objects)
    {
        var ordered = objects.OrderBy(item => item.ZIndex).ToArray();
        objects.Clear();
        objects.AddRange(ordered);
    }
}
