using HoomNote.Core.Documents;

namespace HoomNote.Canvas.Rendering;

/// <summary>
/// Capture policy for source-of-truth vector ink. Thresholds are document-space values and
/// deliberately have no viewport-zoom input; zoom-dependent simplification belongs to rendering.
/// </summary>
public static class CanonicalInkPolicy
{
    public const double MinimumSampleDistance = 0.04;

    public static bool ShouldAccept(InkPoint previous, PointD candidate, bool force = false)
    {
        if (force) return true;
        var deltaX = previous.X - candidate.X;
        var deltaY = previous.Y - candidate.Y;
        return deltaX * deltaX + deltaY * deltaY >=
               MinimumSampleDistance * MinimumSampleDistance;
    }
}

/// <summary>
/// Bounds refinement work performed by a single presentation frame.
/// </summary>
public static class NavigationRefinementPolicy
{
    public static int TileBuildBudget(bool interactionActive) => interactionActive ? 0 : 1;

    public static bool ShouldPresentTiles(int visibleTileCount, int readyTileCount) =>
        visibleTileCount > 0 && readyTileCount >= visibleTileCount;
}

public readonly record struct NavigationTileMetrics(
    int CorePixelLeft,
    int CorePixelTop,
    int CorePixelWidth,
    int CorePixelHeight,
    int RenderPixelLeft,
    int RenderPixelTop,
    int RenderPixelWidth,
    int RenderPixelHeight)
{
    public static NavigationTileMetrics Create(
        int tileX,
        int tileY,
        int tilePixels,
        int fullPixelWidth,
        int fullPixelHeight,
        int gutterPixels)
    {
        var coreLeft = tileX * tilePixels;
        var coreTop = tileY * tilePixels;
        var coreWidth = Math.Min(tilePixels, fullPixelWidth - coreLeft);
        var coreHeight = Math.Min(tilePixels, fullPixelHeight - coreTop);
        var leftGutter = Math.Min(gutterPixels, coreLeft);
        var topGutter = Math.Min(gutterPixels, coreTop);
        var rightGutter = Math.Min(gutterPixels,
            Math.Max(0, fullPixelWidth - coreLeft - coreWidth));
        var bottomGutter = Math.Min(gutterPixels,
            Math.Max(0, fullPixelHeight - coreTop - coreHeight));
        return new NavigationTileMetrics(
            coreLeft,
            coreTop,
            coreWidth,
            coreHeight,
            coreLeft - leftGutter,
            coreTop - topGutter,
            coreWidth + leftGutter + rightGutter,
            coreHeight + topGutter + bottomGutter);
    }
}
