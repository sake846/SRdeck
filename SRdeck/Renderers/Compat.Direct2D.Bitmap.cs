using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SRdeck.Renderers.Compat.Direct2D1
{
    internal class ID2D1Bitmap : IDisposable
    {
        internal WriteableBitmap Bitmap { get; }
        private byte[]? _copyBuffer;
        public ID2D1Bitmap(int width, int height)
        {
            Bitmap = new WriteableBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Bgra32, null);
        }

        public void CopyFromMemory(IntPtr memory, uint pitch)
        {
            int stride = (int)pitch;
            int size = stride * Bitmap.PixelHeight;
            if (_copyBuffer == null || _copyBuffer.Length < size)
            {
                _copyBuffer = new byte[size];
            }
            Marshal.Copy(memory, _copyBuffer, 0, size);
            Bitmap.WritePixels(new Int32Rect(0, 0, Bitmap.PixelWidth, Bitmap.PixelHeight), _copyBuffer, stride, 0);
        }

        public void CopyFromMemory(System.Drawing.Rectangle rect, byte[] data, uint pitch)
        {
            Bitmap.WritePixels(new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height), data, (int)pitch, 0);
        }

        public void Dispose() { }
    }
}
