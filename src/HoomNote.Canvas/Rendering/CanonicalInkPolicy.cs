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
    public static bool IsViewportNavigationActive(
        bool pointerDown,
        bool pointerPans,
        bool touchActive,
        bool touchInertiaActive,
        bool wheelZoomAnimating,
        bool wheelScrollAnimating,
        bool zoomNavigationActive) =>
        (pointerDown && pointerPans) ||
        touchActive ||
        touchInertiaActive ||
        wheelZoomAnimating ||
        wheelScrollAnimating ||
        zoomNavigationActive;

    public static int TileBuildBudget(bool interactionActive) => interactionActive ? 0 : 1;

    public static bool CanBuildTile(
        bool interactionActive,
        bool immediateInputRequested,
        bool inputIdle = true) =>
        inputIdle && !immediateInputRequested && TileBuildBudget(interactionActive) > 0;

    public static bool ShouldPresentTiles(int visibleTileCount, int readyTileCount) =>
        visibleTileCount > 0 && readyTileCount >= visibleTileCount;

    public static bool ShouldDrawVectorFallback(int visibleTileCount, int readyTileCount) =>
        visibleTileCount > 0 && readyTileCount < visibleTileCount;

    /// <summary>
    /// Pointer input may use a retained page to avoid replaying a dense scene, but an obsolete
    /// retained frame must never replace the current document. This is the source-of-truth
    /// fallback after structural edits while a replacement raster is still being composed.
    /// </summary>
    public static bool ShouldUseSourceVectorsForInteraction(
        bool interactionActive,
        bool retainedFrameCurrent) =>
        interactionActive && !retainedFrameCurrent;

    /// <summary>
    /// A retained texture is current only when it was composed from this exact page revision.
    /// Object ids alone are insufficient because moves preserve identity while changing transforms.
    /// </summary>
    public static bool IsRetainedFrameCurrent(
        Guid pageId,
        DateTimeOffset pageUpdatedAt,
        Guid? retainedPageId,
        DateTimeOffset? retainedUpdatedAt,
        bool invalidated = false) =>
        !invalidated && retainedPageId == pageId && retainedUpdatedAt == pageUpdatedAt;
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
