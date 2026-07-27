namespace HoomNote.Canvas.Interaction;

/// <summary>
/// Keeps platform-promoted touch contacts out of editing tools. Some digitizers expose
/// the raw contact as Touch, while others additionally (or exclusively) surface a generated
/// Mouse pointer through XAML. Both forms represent finger navigation.
/// </summary>
public static class TouchInputPolicy
{
    public const double PageScrollEdgeWidth = 52;
    public const double PageScrollStepDistance = 72;

    public static bool IsNavigationContact(
        bool reportedAsTouch,
        bool reportedAsMouse,
        bool isPlatformGenerated,
        bool nativePointerIsTouch,
        bool hasTouchContactArea) =>
        reportedAsTouch ||
        nativePointerIsTouch ||
        (reportedAsMouse && (isPlatformGenerated || hasTouchContactArea));

    public static bool CanSelectText(
        bool reportedAsPen,
        bool reportedAsMouse,
        bool isPlatformGenerated,
        bool nativePointerIsTouch,
        bool hasTouchContactArea) =>
        reportedAsPen ||
        (reportedAsMouse &&
         !isPlatformGenerated &&
         !nativePointerIsTouch &&
         !hasTouchContactArea);

    public static bool ShouldBeginPageScroll(
        double contactX,
        double viewportWidth,
        double edgeWidth = PageScrollEdgeWidth) =>
        double.IsFinite(contactX) &&
        double.IsFinite(viewportWidth) &&
        double.IsFinite(edgeWidth) &&
        viewportWidth > 0 &&
        edgeWidth > 0 &&
        contactX >= Math.Max(0, viewportWidth - edgeWidth);

    /// <summary>
    /// A finger moving upward advances through the notebook, matching direct-manipulation
    /// scrolling where the current page follows the finger off the top of the viewport.
    /// </summary>
    public static int PageScrollSteps(
        double verticalDelta,
        double stepDistance = PageScrollStepDistance)
    {
        if (!double.IsFinite(verticalDelta) || !double.IsFinite(stepDistance) || stepDistance <= 0)
            return 0;
        var magnitude = (int)Math.Floor(Math.Abs(verticalDelta) / stepDistance);
        return verticalDelta < 0 ? magnitude : -magnitude;
    }
}
