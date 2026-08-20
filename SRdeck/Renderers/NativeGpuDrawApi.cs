using System;
using System.Runtime.InteropServices;

namespace SRdeck.Renderers;

internal static class NativeGpuDrawApi
{
    private const string DllName = "sr_gpu";

    [StructLayout(LayoutKind.Sequential)]
    internal struct LineVertex
    {
        public float X;
        public float Y;
        public uint Bgra;

        public LineVertex(float x, float y, uint bgra)
        {
            X = x;
            Y = y;
            Bgra = bgra;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_create_surface")]
    internal static extern int CreateSurface(int width, int height, out IntPtr handle, out IntPtr sharedHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_destroy_surface")]
    internal static extern void DestroySurface(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_clear_surface")]
    internal static extern int ClearSurface(IntPtr handle, uint bgra);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_upload_bgra_surface")]
    internal static extern int UploadBgraSurface(IntPtr handle, IntPtr pixels, int width, int height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_scroll_upload_top_row")]
    internal static extern int ScrollUploadTopRow(IntPtr handle, IntPtr rowPixels, int width);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_scroll_upload_row_region")]
    internal static extern int ScrollUploadRowRegion(IntPtr handle, IntPtr rowPixels, int width, int top, int height, int flushAfter);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_draw_lines")]
    internal static extern int DrawLines(IntPtr handle, IntPtr vertices, int vertexCount, int clearFirst, uint clearBgra);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_draw_triangles")]
    internal static extern int DrawTriangles(IntPtr handle, IntPtr vertices, int vertexCount, int clearFirst, uint clearBgra);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_draw_lines_ex")]
    internal static extern int DrawLinesEx(IntPtr handle, IntPtr vertices, int vertexCount, int clearFirst, uint clearBgra, int flushAfter);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_draw_triangles_ex")]
    internal static extern int DrawTrianglesEx(IntPtr handle, IntPtr vertices, int vertexCount, int clearFirst, uint clearBgra, int flushAfter);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpudraw_shutdown")]
    internal static extern void Shutdown();
}
