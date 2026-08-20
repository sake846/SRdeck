using System;
using System.Numerics;
using System.Windows;
using System.Windows.Media;

namespace SRdeck.Renderers.Compat.Direct2D1
{
    internal sealed class ID2D1PathGeometry : IDisposable
    {
        internal StreamGeometry Geometry = new();
        public GeometrySink Open() => new(this);
        public void Dispose() { }
    }

    internal sealed class GeometrySink : IDisposable
    {
        private readonly ID2D1PathGeometry _owner;
        private readonly StreamGeometryContext _ctx;
        public GeometrySink(ID2D1PathGeometry owner) { _owner = owner; _ctx = owner.Geometry.Open(); }
        public void BeginFigure(Vector2 startPoint, FigureBegin figureBegin) => _ctx.BeginFigure(new Point(startPoint.X, startPoint.Y), figureBegin == FigureBegin.Filled, false);
        public void AddLines(Vector2[] points)
        {
            if (points.Length == 0) return;
            Point[] arr = new Point[points.Length];
            for (int i = 0; i < points.Length; i++) arr[i] = new Point(points[i].X, points[i].Y);
            _ctx.PolyLineTo(arr, true, false);
        }
        public void AddLine(Vector2 point) => _ctx.LineTo(new Point(point.X, point.Y), true, false);
        public void EndFigure(FigureEnd figureEnd) { }
        public void Close() { _ctx.Close(); _owner.Geometry.Freeze(); }
        public void Dispose() { }
    }
}
