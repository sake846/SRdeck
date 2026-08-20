using System.Runtime.InteropServices;
using System.Windows;

namespace SRdeck.Helpers;

/// <summary>
/// Windows API (user32.dll) を呼び出し、システムレベルでのマウスポインタの絶対座標を取得するなど、
/// アプリケーション全体で利用できる汎用的な静的メソッドを提供します。
/// </summary>
public static class Win32Api
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Win32Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(ref Win32Point cursorPoint);

    public static Point GetCursorPosition()
    {
        Win32Point cursorPoint = default;
        GetCursorPos(ref cursorPoint);
        return new Point(cursorPoint.X, cursorPoint.Y);
    }
}
