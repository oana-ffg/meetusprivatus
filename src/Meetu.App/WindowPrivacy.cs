using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Meetu.App;

internal static class WindowPrivacy
{
    private const uint DisplayAffinityNone = 0x00;
    private const uint DisplayAffinityExcludeFromCapture = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    // Live text can include notes spoken while muted, so by default the window does not
    // appear in screen shares or screenshots. Returns false if Windows refused the request.
    public static bool Apply(Window window, bool hideFromCapture) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, hideFromCapture ? DisplayAffinityExcludeFromCapture : DisplayAffinityNone);
}
