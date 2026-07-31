using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Rendering;

public static class CanvasObjectRenderPolicy
{
    public static float SourceOverOpacity(InkStyle style) => style.Normalize().Opacity;

    /// <summary>
    /// Returns visible objects without regrouping them by type. Callers supply objects in
    /// authored Z-order; keeping that sequence intact ensures translucent highlighter ink
    /// appears identically in live, cached, tiled, and thumbnail rendering.
    /// </summary>
    public static IEnumerable<CanvasObject> VisibleInAuthoredOrder(
        IEnumerable<CanvasObject> objects)
    {
        foreach (var canvasObject in objects)
            if (!canvasObject.IsHidden)
                yield return canvasObject;
    }
}
