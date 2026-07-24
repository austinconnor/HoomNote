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
}
