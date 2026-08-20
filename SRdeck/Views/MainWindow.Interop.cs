using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SRdeck.Views;

/// <summary>
/// MainWindow の Win32 API インターオプ処理（DWM角丸、ウィンドウサイズ制約）を定義する部分クラスです。
/// </summary>
public partial class MainWindow : Window
{
    private IntPtr _windowHandle = IntPtr.Zero;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_windowHandle);
        source?.AddHook(WindowProc);

        // Windows 11 用の角丸設定 (DWMWA_WINDOW_CORNER_PREFERENCE)
        int cornerPreference = (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
        DwmSetWindowAttribute(_windowHandle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    public void RefreshHoverStateFromCurrentCursor()
    {
        if (_windowHandle == IntPtr.Zero) return;

        var screenPoint = Helpers.Win32Api.GetCursorPosition();
        POINT clientPoint = new POINT
        {
            X = (int)Math.Round(screenPoint.X),
            Y = (int)Math.Round(screenPoint.Y),
        };

        if (!ScreenToClient(_windowHandle, ref clientPoint)) return;
        RECT clientRect = default;
        if (!GetClientRect(_windowHandle, out clientRect)) return;
        if (clientPoint.X < 0 || clientPoint.Y < 0 || clientPoint.X >= clientRect.Right || clientPoint.Y >= clientRect.Bottom) return;

        int lParam = (clientPoint.Y << 16) | (clientPoint.X & 0xFFFF);
        SendMessage(_windowHandle, WM_MOUSEMOVE, IntPtr.Zero, new IntPtr(lParam));
        SendMessage(_windowHandle, WM_SETCURSOR, _windowHandle, new IntPtr((HTCLIENT & 0xFFFF) | (WM_MOUSEMOVE << 16)));
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        else if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_ACTIVATE);
        }
        return IntPtr.Zero;
    }

    private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var structure = Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
        if (structure == null) return;
        MINMAXINFO mmi = (MINMAXINFO)structure;

        IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor != IntPtr.Zero)
        {
            MONITORINFO mi = new MONITORINFO();
            if (GetMonitorInfo(hMonitor, mi))
            {
                RECT rcWorkArea = mi.rcWork;
                RECT rcMonitorArea = mi.rcMonitor;

                // WM_GETMINMAXINFO expects monitor coordinates in physical pixels.
                // WPF layout uses DIPs, but this Win32 message is handled before WPF converts sizes.
                DpiScale dpi = VisualTreeHelper.GetDpi(this);

                // マルチモニター環境におけるウィンドウ位置・サイズバグを修正します。
                mmi.ptMaxPosition.X = rcWorkArea.Left - rcMonitorArea.Left;
                mmi.ptMaxPosition.Y = rcWorkArea.Top - rcMonitorArea.Top;

                mmi.ptMaxSize.X = Math.Abs(rcWorkArea.Right - rcWorkArea.Left);
                mmi.ptMaxSize.Y = Math.Abs(rcWorkArea.Bottom - rcWorkArea.Top);

                // ptMaxTrackSize を ptMaxSize と同じ値に設定して、OSの制限を回避します。
                mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
                mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;

                // WPF の最小サイズ(DIP)を Win32 の物理ピクセルへ変換します。
                mmi.ptMinTrackSize.X = (int)Math.Ceiling(790 * dpi.DpiScaleX);
                mmi.ptMinTrackSize.Y = (int)Math.Ceiling(240 * dpi.DpiScaleY);
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    #region Win32 API Interop
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_SETCURSOR = 0x0020;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MA_ACTIVATE = 1;
    private const int HTCLIENT = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private enum DWM_WINDOW_CORNER_PREFERENCE
    {
        DWMWCP_DEFAULT = 0,
        DWMWCP_DONOTROUND = 1,
        DWMWCP_ROUND = 2,
        DWMWCP_ROUNDSMALL = 3
    }

    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MONITORINFO
    {
        public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        public RECT rcMonitor = new RECT();
        public RECT rcWork = new RECT();
        public int dwFlags = 0;
    }
    #endregion
}
