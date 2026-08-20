using System;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;

namespace SRdeck.Renderers.Compat.Direct2D1
{
    internal class ID2D1RenderTarget : IDisposable
    {
        public ID2D1Factory1 Factory { get; }
        public AntialiasMode AntialiasMode { get; set; }
        public ImageSource? LastImageSource { get; private set; }
        private readonly int _width;
        private readonly int _height;
        private DrawingContext? _dc;
        private DrawingGroup? _drawingGroup;
        private int _clipDepth;
        private static double Snap(double v) => Math.Round(v) + 0.5;

        internal ID2D1RenderTarget(ID2D1Factory1 factory, int width, int height)
        {
            Factory = factory;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
        }

        public void BeginDraw()
        {
            _drawingGroup = new DrawingGroup();
            _dc = _drawingGroup.Open();
        }

        public void EndDraw()
        {
            _dc?.Close();
            if (_drawingGroup == null) return;
            LastImageSource = new DrawingImage(_drawingGroup);
            _drawingGroup = null;
            _dc = null;
            _clipDepth = 0;
        }

        public void Clear(SRdeck.Renderers.Compat.Mathematics.Color4 color) => _dc?.DrawRectangle(new SolidColorBrush(Conv.ToMediaColor(color)), null, new Rect(0, 0, _width, _height));
        public ID2D1SolidColorBrush CreateSolidColorBrush(SRdeck.Renderers.Compat.Mathematics.Color4 color) => new() { Color = color };
        public ID2D1Bitmap CreateBitmap(SRdeck.Renderers.Compat.Mathematics.SizeI size, IntPtr srcData, uint pitch, BitmapProperties props) => new(size.Width, size.Height);
        public ID2D1GradientStopCollection CreateGradientStopCollection(GradientStop[] stops) => new() { Stops = stops };
        public ID2D1LinearGradientBrush CreateLinearGradientBrush(LinearGradientBrushProperties props, ID2D1GradientStopCollection stops)
        {
            var l = new LinearGradientBrush
            {
                StartPoint = new Point(props.StartPoint.X, props.StartPoint.Y),
                EndPoint = new Point(props.EndPoint.X, props.EndPoint.Y),
                MappingMode = BrushMappingMode.Absolute
            };
            foreach (var s in stops.Stops) l.GradientStops.Add(new System.Windows.Media.GradientStop(Conv.ToMediaColor(s.Color), s.Position));
            return new ID2D1LinearGradientBrush { Brush = l };
        }
        public void DrawLine(Vector2 p0, Vector2 p1, ID2D1Brush brush, float strokeWidth = 1.0f)
        {
            if (_dc == null) return;
            Point a = new(p0.X, p0.Y);
            Point b = new(p1.X, p1.Y);
            if (AntialiasMode == AntialiasMode.Aliased && Math.Abs(strokeWidth - 1.0f) < 0.001f)
            {
                a = new Point(Snap(a.X), Snap(a.Y));
                b = new Point(Snap(b.X), Snap(b.Y));
            }
            _dc.DrawLine(new Pen(brush.Brush, strokeWidth), a, b);
        }
        public void FillGeometry(ID2D1PathGeometry geometry, ID2D1Brush brush) => _dc?.DrawGeometry(brush.Brush, null, geometry.Geometry);
        public void DrawGeometry(ID2D1PathGeometry geometry, ID2D1Brush brush, float strokeWidth = 1.0f) => _dc?.DrawGeometry(null, new Pen(brush.Brush, strokeWidth), geometry.Geometry);
        public void FillRectangle(SRdeck.Renderers.Compat.RawRectF rect, ID2D1Brush brush) => _dc?.DrawRectangle(brush.Brush, null, Conv.ToRect(rect));
        public void FillRoundedRectangle(RoundedRectangle rect, ID2D1Brush brush) => _dc?.DrawRoundedRectangle(brush.Brush, null, Conv.ToRect(rect.Rect), rect.RadiusX, rect.RadiusY);
        public void DrawEllipse(Ellipse ellipse, ID2D1Brush brush, float strokeWidth = 1.0f) => _dc?.DrawEllipse(null, new Pen(brush.Brush, strokeWidth), new Point(ellipse.Point.X, ellipse.Point.Y), ellipse.RadiusX, ellipse.RadiusY);
        public void DrawText(string text, SRdeck.Renderers.Compat.DirectWrite.IDWriteTextFormat format, SRdeck.Renderers.Compat.RawRectF layoutRect, ID2D1Brush brush)
        {
            var ft = new FormattedText(text ?? string.Empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(format.FontFamilyName), format.FontSize, brush.Brush, 1.0);
            double x = layoutRect.Left;
            double y = layoutRect.Top;
            double w = Math.Max(0.0, layoutRect.Right - layoutRect.Left);
            double h = Math.Max(0.0, layoutRect.Bottom - layoutRect.Top);

            if (format.TextAlignment == SRdeck.Renderers.Compat.DirectWrite.TextAlignment.Center)
            {
                x += (w - ft.Width) * 0.5;
            }
            else if (format.TextAlignment == SRdeck.Renderers.Compat.DirectWrite.TextAlignment.Trailing)
            {
                x += w - ft.Width;
            }

            if (format.ParagraphAlignment == SRdeck.Renderers.Compat.DirectWrite.ParagraphAlignment.Center)
            {
                y += (h - ft.Height) * 0.5;
            }
            else if (format.ParagraphAlignment == SRdeck.Renderers.Compat.DirectWrite.ParagraphAlignment.Far)
            {
                y += h - ft.Height;
            }

            var p = new Point(x, y);
            _dc?.DrawText(ft, p);
        }
        public void DrawBitmap(ID2D1Bitmap bitmap, float opacity, BitmapInterpolationMode interpolationMode) => _dc?.DrawImage(bitmap.Bitmap, new Rect(0, 0, _width, _height));
        public void DrawBitmap(ID2D1Bitmap bitmap, SRdeck.Renderers.Compat.Mathematics.Rect destRect, float opacity, BitmapInterpolationMode interpolationMode) => _dc?.DrawImage(bitmap.Bitmap, Conv.ToRect(destRect));
        public void DrawBitmap(ID2D1Bitmap bitmap, SRdeck.Renderers.Compat.Mathematics.Rect destRect, float opacity, BitmapInterpolationMode interpolationMode, SRdeck.Renderers.Compat.Mathematics.Rect srcRect)
        {
            if (_dc == null) return;
            var dst = Conv.ToRect(destRect);
            double srcW = Math.Max(1.0, srcRect.Width);
            double srcH = Math.Max(1.0, srcRect.Height);
            double scaleX = dst.Width / srcW;
            double scaleY = dst.Height / srcH;
            var fullMapped = new Rect(
                dst.X - srcRect.X * scaleX,
                dst.Y - srcRect.Y * scaleY,
                bitmap.Bitmap.PixelWidth * scaleX,
                bitmap.Bitmap.PixelHeight * scaleY);

            _dc.PushClip(new RectangleGeometry(dst));
            _dc.DrawImage(bitmap.Bitmap, fullMapped);
            _dc.Pop();
        }
        public void PushAxisAlignedClip(SRdeck.Renderers.Compat.RawRectF clipRect, AntialiasMode antialiasMode)
        {
            var r = Conv.ToRect(clipRect);
            _dc?.PushClip(new RectangleGeometry(r));
            _clipDepth++;
        }
        public void PopAxisAlignedClip()
        {
            if (_clipDepth > 0)
            {
                _clipDepth--;
                _dc?.Pop();
            }
        }
        public void Dispose() { }
    }

    internal class ID2D1Factory1 : IDisposable
    {
        public ID2D1RenderTarget CreateRenderTarget(int width, int height) => new(this, width, height);
        public ID2D1PathGeometry CreatePathGeometry() => new();
        public void Dispose() { }
    }
}
