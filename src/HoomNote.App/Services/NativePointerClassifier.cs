using System.Runtime.InteropServices;

namespace HoomNote_App.Services;

internal static class NativePointerClassifier
{
    private const uint PointerTypeTouch = 2;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);

    public static bool IsTouch(uint pointerId) =>
        GetPointerType(pointerId, out var pointerType) && pointerType == PointerTypeTouch;
}
