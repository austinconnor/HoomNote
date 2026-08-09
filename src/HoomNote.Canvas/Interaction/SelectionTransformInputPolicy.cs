namespace HoomNote.Canvas.Interaction;

public static class SelectionTransformInputPolicy
{
    public const double MinimumCommitMovementPixels = 1;

    public static bool ShouldRefreshForCommit(
        bool hasPreview,
        double deltaX,
        double deltaY)
    {
        if (hasPreview) return true;
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY)) return false;
        return deltaX * deltaX + deltaY * deltaY >=
               MinimumCommitMovementPixels * MinimumCommitMovementPixels;
    }
}
