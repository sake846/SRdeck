using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SRdeck.Views;

internal static class WindowTheme
{
    public static void ApplyDarkTitleBar(Window window)
    {
        int enabled = 1;
        _ = DwmSetWindowAttribute(
            new WindowInteropHelper(window).Handle,
            20,
            ref enabled,
            sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
