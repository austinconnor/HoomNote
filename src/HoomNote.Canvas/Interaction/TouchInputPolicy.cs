namespace HoomNote.Canvas.Interaction;

/// <summary>
/// Keeps platform-promoted touch contacts out of editing tools. Some digitizers expose
/// the raw contact as Touch, while others additionally (or exclusively) surface a generated
/// Mouse pointer through XAML. Both forms represent finger navigation.
/// </summary>
public static class TouchInputPolicy
{
    public static bool IsNavigationContact(
        bool reportedAsTouch,
        bool reportedAsMouse,
        bool isPlatformGenerated,
        bool nativePointerIsTouch,
        bool hasTouchContactArea) =>
        reportedAsTouch ||
        nativePointerIsTouch ||
        (reportedAsMouse && (isPlatformGenerated || hasTouchContactArea));
}
