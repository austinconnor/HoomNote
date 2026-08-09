using System.Runtime.InteropServices;

namespace HoomNote_App.Services;

internal enum NativeTouchAction
{
    Move,
    Down,
    Up
}

internal readonly record struct NativeTouchContact(
    uint Id,
    double ClientX,
    double ClientY,
    NativeTouchAction Action);

internal sealed class NativeTouchFrameEventArgs(IReadOnlyList<NativeTouchContact> contacts) : EventArgs
{
    public IReadOnlyList<NativeTouchContact> Contacts { get; } = contacts;
}

/// <summary>
/// Receives the WM_TOUCH stream that some Wacom drivers expose beneath WinUI's mouse
/// emulation. Returning the message as handled prevents the same finger from reaching editing
/// tools as a synthetic click.
/// </summary>
internal sealed class NativeTouchWindowSource : IDisposable
{
    private const uint WmTouch = 0x0240;
    private const uint WmDpiChanged = 0x02E0;
    private const uint TouchEventMove = 0x0001;
    private const uint TouchEventDown = 0x0002;
    private const uint TouchEventUp = 0x0004;
    private const nuint SubclassId = 0x4F504E54;

    private readonly nint _windowHandle;
    private readonly SubclassProcedure _subclassProcedure;
    private readonly List<NativeTouchContact> _contacts = [];
    private readonly NativeTouchFrameEventArgs _frameArgs;
    private TouchInput[] _nativeInputs = [];
    private uint _dpi;
    private bool _disposed;
    private static readonly int TouchInputSize = Marshal.SizeOf<TouchInput>();

    public event EventHandler<NativeTouchFrameEventArgs>? Frame;

    private NativeTouchWindowSource(nint windowHandle)
    {
        _windowHandle = windowHandle;
        _subclassProcedure = WindowProcedure;
        _dpi = Math.Max(96u, GetDpiForWindow(windowHandle));
        _frameArgs = new NativeTouchFrameEventArgs(_contacts);
    }

    public static NativeTouchWindowSource? TryCreate(nint windowHandle)
    {
        if (windowHandle == 0 || !RegisterTouchWindow(windowHandle, 0))
        {
            DiagnosticsLog.Warning("input.native_touch_registration_failed",
                ("error", Marshal.GetLastWin32Error()));
            return null;
        }

        var source = new NativeTouchWindowSource(windowHandle);
        if (!SetWindowSubclass(windowHandle, source._subclassProcedure, SubclassId, 0))
        {
            var error = Marshal.GetLastWin32Error();
            UnregisterTouchWindow(windowHandle);
            DiagnosticsLog.Warning("input.native_touch_subclass_failed", ("error", error));
            return null;
        }

        DiagnosticsLog.Info("input.native_touch_registered");
        return source;
    }

    private nint WindowProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        _ = subclassId;
        _ = referenceData;
        if (message == WmDpiChanged)
        {
            _dpi = Math.Max(96u, GetDpiForWindow(windowHandle));
            return DefSubclassProc(windowHandle, message, wParam, lParam);
        }
        if (message != WmTouch)
            return DefSubclassProc(windowHandle, message, wParam, lParam);

        var count = (int)(wParam & 0xffff);
        if (count <= 0)
        {
            CloseTouchInputHandle(lParam);
            return 0;
        }

        try
        {
            if (_nativeInputs.Length < count)
                Array.Resize(ref _nativeInputs, Math.Max(count, Math.Max(4, _nativeInputs.Length * 2)));
            if (!GetTouchInputInfo(lParam, count, _nativeInputs, TouchInputSize))
            {
                DiagnosticsLog.Warning("input.native_touch_read_failed",
                    ("error", Marshal.GetLastWin32Error()));
                return 0;
            }

            _contacts.Clear();
            for (var index = 0; index < count; index++)
            {
                var item = _nativeInputs[index];
                var point = new NativePoint
                {
                    X = (int)Math.Round(item.X / 100d),
                    Y = (int)Math.Round(item.Y / 100d)
                };
                if (!ScreenToClient(windowHandle, ref point)) continue;
                var action = (item.Flags & TouchEventDown) != 0
                    ? NativeTouchAction.Down
                    : (item.Flags & TouchEventUp) != 0
                        ? NativeTouchAction.Up
                        : NativeTouchAction.Move;
                _contacts.Add(new NativeTouchContact(
                    item.Id,
                    point.X * 96d / _dpi,
                    point.Y * 96d / _dpi,
                    action));
            }

            if (_contacts.Count > 0)
                Frame?.Invoke(this, _frameArgs);
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("input.native_touch_dispatch_failed", exception);
        }
        finally
        {
            CloseTouchInputHandle(lParam);
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
        UnregisterTouchWindow(_windowHandle);
        Frame = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TouchInput
    {
        public int X;
        public int Y;
        public nint Source;
        public uint Id;
        public uint Flags;
        public uint Mask;
        public uint Time;
        public nuint ExtraInfo;
        public uint ContactWidth;
        public uint ContactHeight;
    }

    private delegate nint SubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterTouchWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterTouchWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTouchInputInfo(
        nint touchInputHandle,
        int inputCount,
        [Out] TouchInput[] inputs,
        int touchInputSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseTouchInputHandle(nint touchInputHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure subclassProcedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure subclassProcedure,
        nuint subclassId);
}
