using HoomNote.Canvas.Geometry;
using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Rendering;

public static class InkBatchPolicy
{
    public static int CompatiblePrefixLength(
        IReadOnlyList<CanvasObject> objects,
        int start,
        int maximumStrokes,
        int maximumSourcePoints)
    {
        if (start < 0 || start >= objects.Count || maximumStrokes < 2 ||
            maximumSourcePoints < 2 || objects[start] is not InkStrokeObject first ||
            first.IsHidden || first.Points.Count < 2 ||
            first.Style.Tool == InkToolKind.Highlighter ||
            first.Points.Count > maximumSourcePoints)
            return 0;

        var firstStyle = first.Style.Normalize();
        var centerline = StrokeOutlineBuilder.UsesCenterlineStroke(first);
        var count = 0;
        var sourcePoints = 0;
        while (start + count < objects.Count && count < maximumStrokes &&
               objects[start + count] is InkStrokeObject candidate &&
               CanBatch(first, firstStyle, centerline, candidate))
        {
            if (sourcePoints + candidate.Points.Count > maximumSourcePoints) break;
            sourcePoints += candidate.Points.Count;
            count++;
        }
        return count;
    }

    private static bool CanBatch(
        InkStrokeObject first,
        InkStyle firstStyle,
        bool centerline,
        InkStrokeObject candidate) =>
        !candidate.IsHidden && candidate.Points.Count >= 2 &&
        candidate.Style.Tool != InkToolKind.Highlighter &&
        candidate.Transform == first.Transform &&
        candidate.Style.Normalize() == firstStyle &&
        StrokeOutlineBuilder.UsesCenterlineStroke(candidate) == centerline;
}
