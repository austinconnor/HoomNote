using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Rendering;

public static class CanvasObjectRenderPolicy
{
    public const float HighlighterStrengthScale = 0.76f;
    public const float MaximumHighlighterBlendStrength = 0.48f;
    public const float MaximumDarkSurfaceHighlighterBlendStrength = 0.24f;

    public static float SourceOverOpacity(InkStyle style) => style.Normalize().Opacity;

    public static float HighlighterBlendStrength(InkStyle style) =>
        Math.Clamp(
            style.Normalize().Opacity * HighlighterStrengthScale,
            0.06f,
            MaximumHighlighterBlendStrength);

    public static byte LightSurfaceHighlighterChannel(byte channel, InkStyle style)
    {
        var strength = HighlighterBlendStrength(style);
        return (byte)Math.Clamp(
            Math.Round(255 - (255 - channel) * strength), 0, 255);
    }

    public static float DarkSurfaceHighlighterBlendStrength(InkStyle style) =>
        Math.Clamp(
            style.Normalize().Opacity * 0.40f,
            0.05f,
            MaximumDarkSurfaceHighlighterBlendStrength);

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
