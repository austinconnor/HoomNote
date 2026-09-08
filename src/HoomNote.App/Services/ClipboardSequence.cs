using System.Runtime.InteropServices;

namespace HoomNote_App.Services;

internal static class ClipboardSequence
{
    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern uint GetClipboardSequenceNumber();
}
