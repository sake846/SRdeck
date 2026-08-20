using System;
using System.Windows.Media;

namespace SRdeck.Renderers.Compat.Direct2D1
{
    internal class ID2D1Brush : IDisposable
    {
        internal Brush Brush = Brushes.Transparent;
        public virtual void Dispose() { }
    }

    internal class ID2D1SolidColorBrush : ID2D1Brush
    {
        private SRdeck.Renderers.Compat.Mathematics.Color4 _color;
        public SRdeck.Renderers.Compat.Mathematics.Color4 Color
        {
            get => _color;
            set
            {
                _color = value;
                Brush = new SolidColorBrush(Conv.ToMediaColor(value));
            }
        }
    }

    internal class ID2D1LinearGradientBrush : ID2D1Brush { }
    internal class ID2D1GradientStopCollection : IDisposable
    {
        internal GradientStop[] Stops = Array.Empty<GradientStop>();
        public void Dispose() { }
    }
}
