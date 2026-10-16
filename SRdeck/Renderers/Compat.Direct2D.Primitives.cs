using System.Windows;
using System.Windows.Media;

namespace SRdeck.Renderers.Compat.Direct2D1
{
    internal enum AntialiasMode { PerPrimitive, Aliased }
    internal enum BitmapInterpolationMode { Linear, NearestNeighbor }
    internal enum FigureBegin { Filled, Hollow }
    internal enum FigureEnd { Open, Closed }

    internal class BitmapProperties { public BitmapProperties(SRdeck.Renderers.Compat.DCommon.PixelFormat pixelFormat) { } }

    internal static class Conv
    {
        private static byte ToByte(float v)
        {
            var clamped = Math.Clamp(v, 0f, 1f);
            return (byte)Math.Round(clamped * 255f);
        }

        public static Color ToMediaColor(SRdeck.Renderers.Compat.Mathematics.Color4 c) => Color.FromArgb(ToByte(c.A), ToByte(c.R), ToByte(c.G), ToByte(c.B));
        public static Rect ToRect(SRdeck.Renderers.Compat.Mathematics.Rect r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));
    }
}
