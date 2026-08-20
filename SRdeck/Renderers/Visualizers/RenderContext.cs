using System.Numerics;
using SRdeck.Renderers.Compat.Direct2D1;
using SRdeck.Renderers.Compat.DirectWrite;

namespace SRdeck.Renderers.Visualizers
{
    internal class RenderContext
    {
        public required ID2D1RenderTarget RenderTarget { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public required float[] Datx5L { get; set; }
        public required float[] Datx5R { get; set; }
        public required bool[] SqStates { get; set; }
        public int TotalDatLenX5 { get; set; }
        public int DatLen { get; set; }
        public int WaterfallColorMode { get; set; }
        public required IDWriteTextFormat LabelFormat { get; set; }
        public Models.DemodWaveMode Mode { get; set; }

        public bool IsSquelchOpen => SqStates[4];
        public float BaseAlpha => 1.0f;
    }
}
