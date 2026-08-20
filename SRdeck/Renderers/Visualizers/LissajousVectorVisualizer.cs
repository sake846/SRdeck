using System;
using System.Numerics;
using SRdeck.Renderers.Compat;
using SRdeck.Renderers.Compat.DCommon;
using SRdeck.Renderers.Compat.Direct2D1;
using SRdeck.Renderers.Compat.DXGI;
using Color = SRdeck.Renderers.Compat.Mathematics.Color4;
using Rect = SRdeck.Renderers.Compat.Mathematics.Rect;
using SizeI = SRdeck.Renderers.Compat.Mathematics.SizeI;
using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.Renderers.Visualizers
{
    internal class LissajousVectorVisualizer : VisualizerBase, IDemodVisualizer
    {
        private const int DefaultMapSize = 150;
        private const float DensityThreshold = 0.01f;
        private const int HeatmapLutSize = 256;

        // Static color definitions to avoid magic color values and ensure readability
        private static readonly Color MainSeparatorColor = new Color(85f / 255f, 85f / 255f, 85f / 255f, 1f);
        private static readonly Color TimeSeparatorColor = new Color(1f, 1f, 1f, 0.2f);
        private static readonly Color GridWaveNormalColor = new Color(70f / 255f, 70f / 255f, 70f / 255f, 1f);
        private static readonly Color GridWaveEmphColor = new Color(45f / 255f, 45f / 255f, 45f / 255f, 1f);
        private static readonly Color GridShapeNormalColor = new Color(45f / 255f, 45f / 255f, 45f / 255f, 1f);
        private static readonly Color GridShapeEmphColor = new Color(70f / 255f, 70f / 255f, 70f / 255f, 1f);
        private readonly uint[] _heatmapLut = new uint[HeatmapLutSize];
        private uint[] _mapPixels = Array.Empty<uint>();
        private ID2D1Bitmap? _mapBitmap;
        private ID2D1RenderTarget? _mapBitmapOwner;
        private int _mapBitmapSize = -1;

        public LissajousVectorVisualizer()
        {
            for (int i = 0; i < HeatmapLutSize; i++)
            {
                float d = i / (float)(HeatmapLutSize - 1);
                _heatmapLut[i] = PackToBgra(GetHeatmapColor(d));
            }
        }

        public override void Draw(RenderContext ctx)
        {
            if (ctx.Datx5L.Length < ctx.TotalDatLenX5 || ctx.Datx5R.Length < ctx.TotalDatLenX5) return;

            var rt = ctx.RenderTarget;
            int w = ctx.Width;
            int h = ctx.Height;
            var mode = ctx.Mode;

            DrawGrid(ctx);

            float wLis = w * 0.4f;
            float wVec = (mode == DemodWaveMode.Compare) ? w * 0.4f : 0;
            float wTime = (mode == DemodWaveMode.Compare) ? 0 : w - wLis;

            rt.PushAxisAlignedClip(new RawRectF(0, 0, w, h), AntialiasMode.Aliased);
            try
            {
                float mapW = wLis;
                float mapH = h;
                
                if (mode == DemodWaveMode.Compare)
                {
                    // Left: Lissajous
                    var mapL = LissajousAnalyzer.CalculateDensityMap(ctx.Datx5L, ctx.Datx5R, ctx.SqStates, DefaultMapSize, LissajousAnalyzer.AnalysisType.Lissajous);
                    DrawMap(rt, mapL, DefaultMapSize, 0f, 0f, mapW, mapH);

                    // Right: Vector
                    var mapV = LissajousAnalyzer.CalculateDensityMap(ctx.Datx5L, ctx.Datx5R, ctx.SqStates, DefaultMapSize, LissajousAnalyzer.AnalysisType.Vector);
                    DrawMap(rt, mapV, DefaultMapSize, w - wVec, 0f, wVec, mapH);
                }
                else
                {
                    var type = (mode == DemodWaveMode.Vector) ? LissajousAnalyzer.AnalysisType.Vector : LissajousAnalyzer.AnalysisType.Lissajous;
                    var map = LissajousAnalyzer.CalculateDensityMap(ctx.Datx5L, ctx.Datx5R, ctx.SqStates, DefaultMapSize, type);
                    DrawMap(rt, map, DefaultMapSize, 0f, 0f, mapW, mapH);
                }

                var oldAliased = rt.AntialiasMode;
                rt.AntialiasMode = AntialiasMode.Aliased;
                using (var separatorBrush = rt.CreateSolidColorBrush(MainSeparatorColor))
                {
                    float lineX = (float)Math.Round(wLis) - 0.5f;
                    rt.DrawLine(new Vector2(lineX, 0), new Vector2(lineX, h), separatorBrush, 1.0f);
                }
                rt.AntialiasMode = oldAliased;
            }
            finally
            {
                rt.PopAxisAlignedClip();
            }

            if (wTime > 0)
            {
                int points500ms = ctx.TotalDatLenX5;
                float step = wTime / (float)(points500ms - 1);
                
                float centerY_L = h * 0.25f;
                float centerY_R = h * 0.75f;
                float waveH = h * 0.22f;

                Color timeColor = GetColorByMode(ctx.WaterfallColorMode, 1.0f, ctx.IsSquelchOpen);
                using var brush = rt.CreateSolidColorBrush(timeColor);

                DrawWaveformGeometry(rt, brush, ctx.Datx5L, points500ms, wLis, centerY_L, waveH, step);
                DrawWaveformGeometry(rt, brush, ctx.Datx5R, points500ms, wLis, centerY_R, waveH, step);
            }

            using var sepBrush = rt.CreateSolidColorBrush(TimeSeparatorColor);
            rt.DrawLine(new Vector2(wLis, 0), new Vector2(wLis, h), sepBrush, 1.0f);

            if (mode != DemodWaveMode.Compare)
            {
                DrawLabels(ctx, "500 msec", wLis, "L", true);
            }
        }

        private static void DrawWaveformGeometry(ID2D1RenderTarget rt, ID2D1SolidColorBrush brush, float[] data, int points, float startX, float centerY, float waveH, float step)
        {
            using var geom = DirectXManager.Instance.D2DFactory.CreatePathGeometry();
            using (var sink = geom.Open())
            {
                sink.BeginFigure(new Vector2(startX, centerY - data[0] * waveH), FigureBegin.Hollow);
                for (int i = 10; i < points; i += 10)
                {
                    sink.AddLine(new Vector2(startX + i * step, centerY - data[i] * waveH));
                }
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            rt.DrawGeometry(geom, brush, 1.0f);
        }

        private void DrawMap(ID2D1RenderTarget rt, float[] map, int mapSize, float tx, float ty, float tw, float th)
        {
            float offsetX = (tw - mapSize) * 0.5f;
            float offsetY = (th - mapSize) * 0.5f;
            EnsureMapResources(rt, mapSize);
            for (int i = 0; i < map.Length; i++)
            {
                float d = map[i];
                if (d <= DensityThreshold)
                {
                    _mapPixels[i] = 0;
                    continue;
                }
                int idx = (int)(Math.Clamp(d, 0f, 1f) * (HeatmapLutSize - 1));
                _mapPixels[i] = _heatmapLut[idx];
            }
            unsafe
            {
                fixed (uint* pMap = _mapPixels)
                {
                    _mapBitmap!.CopyFromMemory((IntPtr)pMap, (uint)(mapSize * sizeof(uint)));
                }
            }
            var destRect = new Rect(tx + offsetX, ty + offsetY, mapSize, mapSize);
            var srcRect = new Rect(0, 0, mapSize, mapSize);
            rt.DrawBitmap(_mapBitmap!, destRect, 1.0f, BitmapInterpolationMode.NearestNeighbor, srcRect);
        }

        private void EnsureMapResources(ID2D1RenderTarget rt, int mapSize)
        {
            int requiredLength = mapSize * mapSize;
            if (_mapPixels.Length != requiredLength)
            {
                _mapPixels = new uint[requiredLength];
            }
            if (_mapBitmap != null && _mapBitmapSize == mapSize && ReferenceEquals(_mapBitmapOwner, rt)) return;

            _mapBitmap?.Dispose();
            var bitmapProps = new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied));
            _mapBitmap = rt.CreateBitmap(new SizeI(mapSize, mapSize), IntPtr.Zero, 0, bitmapProps);
            _mapBitmapOwner = rt;
            _mapBitmapSize = mapSize;
        }

        private static uint PackToBgra(Color c)
        {
            uint b = (uint)Math.Clamp((int)(c.B * 255f), 0, 255);
            uint g = (uint)Math.Clamp((int)(c.G * 255f), 0, 255);
            uint r = (uint)Math.Clamp((int)(c.R * 255f), 0, 255);
            uint a = (uint)Math.Clamp((int)(c.A * 255f), 0, 255);
            return b | (g << 8) | (r << 16) | (a << 24);
        }

        private void DrawGrid(RenderContext ctx)
        {
            var rt = ctx.RenderTarget;
            int width = ctx.Width;
            int height = ctx.Height;
            var mode = ctx.Mode;

            var oldMode = rt.AntialiasMode;
            rt.AntialiasMode = AntialiasMode.Aliased;

            using var normalBrush = rt.CreateSolidColorBrush(GridWaveNormalColor);
            using var emphBrush = rt.CreateSolidColorBrush(GridWaveEmphColor);

            float wLis = width * 0.4f;
            float wTime = (mode == DemodWaveMode.Compare) ? 0 : width - wLis;

            using (var separatorBrush = rt.CreateSolidColorBrush(MainSeparatorColor))
            {
                float lineX1 = (float)Math.Round(wLis) - 0.5f;
                rt.DrawLine(new Vector2(lineX1, 0), new Vector2(lineX1, height), separatorBrush, 1.0f);

                if (mode == DemodWaveMode.Compare)
                {
                    float wVec = width * 0.4f;
                    float lineX2 = (float)Math.Round(width - wVec) - 0.5f;
                    rt.DrawLine(new Vector2(lineX2, 0), new Vector2(lineX2, height), separatorBrush, 1.0f);
                }
            }

            if (mode == DemodWaveMode.Compare)
            {
                float cxL = width * 0.2f;
                float cy = height * 0.5f;
                rt.DrawLine(new Vector2(cxL, 0), new Vector2(cxL, height), emphBrush);
                rt.DrawLine(new Vector2(0, cy), new Vector2(width * 0.4f, cy), emphBrush);

                float cxR = width * 0.8f;
                rt.DrawLine(new Vector2(cxR, 0), new Vector2(cxR, height), emphBrush);
                rt.DrawLine(new Vector2(width * 0.6f, cy), new Vector2(width, cy), emphBrush);
            }
            else if (wTime > 0)
            {
                float waveH = height * 0.25f;

                rt.DrawLine(new Vector2(wLis, height * 0.5f), new Vector2(width, height * 0.5f), normalBrush);

                DrawChannelGrid(rt, wLis, width, height * 0.25f, waveH, normalBrush, emphBrush);
                DrawChannelGrid(rt, wLis, width, height * 0.75f, waveH, normalBrush, emphBrush);

                for (int j = 1; j < 20; j++)
                {
                    float x = wLis + (float)Math.Round(((double)wTime / 20.0 * j));
                    if (j % 4 == 0)
                        rt.DrawLine(new Vector2(x, 0), new Vector2(x, height), normalBrush);
                    else
                        rt.DrawLine(new Vector2(x, 0), new Vector2(x, height), emphBrush);
                }
            }

            rt.AntialiasMode = AntialiasMode.PerPrimitive;
            float scale = Math.Min(wLis, height) * 0.45f;
            float centerX1 = wLis / 2f;
            float centerY = height / 2f;

            if (mode == DemodWaveMode.Lissajous || mode == DemodWaveMode.Compare)
                DrawLissajousGrid(rt, wLis, height, centerX1, centerY, scale);
            
            if (mode == DemodWaveMode.Vector || mode == DemodWaveMode.Compare)
            {
                float wVec = (mode == DemodWaveMode.Compare) ? width * 0.4f : wLis;
                float startX = (mode == DemodWaveMode.Compare) ? width - wVec : 0;
                float cx = (mode == DemodWaveMode.Compare) ? startX + wVec / 2f : centerX1;
                DrawVectorGrid(rt, wVec, height, cx, centerY, scale, startX);
            }
            rt.AntialiasMode = oldMode;
        }

        private static void DrawChannelGrid(ID2D1RenderTarget rt, float startX, float endX, float centerY, float waveH, ID2D1SolidColorBrush normalBrush, ID2D1SolidColorBrush emphBrush)
        {
            rt.DrawLine(new Vector2(startX, centerY), new Vector2(endX, centerY), normalBrush);
            for (int k = 1; k <= 3; k++)
            {
                float offset = waveH * (k * 0.25f);
                rt.DrawLine(new Vector2(startX, centerY - offset), new Vector2(endX, centerY - offset), emphBrush);
                rt.DrawLine(new Vector2(startX, centerY + offset), new Vector2(endX, centerY + offset), emphBrush);
            }
        }

        private void DrawLissajousGrid(ID2D1RenderTarget rt, float width, float height, float centerX, float centerY, float scale, float startX = 0)
        {
            using var normalBrush = rt.CreateSolidColorBrush(GridShapeNormalColor);
            using var emphBrush = rt.CreateSolidColorBrush(GridShapeEmphColor);

            rt.DrawLine(new Vector2(centerX, 0), new Vector2(centerX, height), emphBrush, 1.0f);
            rt.DrawLine(new Vector2(startX, centerY), new Vector2(startX + width, centerY), emphBrush, 1.0f);

            float s = scale;
            rt.DrawLine(new Vector2(centerX - s, centerY - s), new Vector2(centerX + s, centerY + s), normalBrush, 1.0f);
            rt.DrawLine(new Vector2(centerX + s, centerY - s), new Vector2(centerX - s, centerY + s), normalBrush, 1.0f);
        }

        private void DrawVectorGrid(ID2D1RenderTarget rt, float width, float height, float centerX, float centerY, float scale, float startX = 0)
        {
            using var normalBrush = rt.CreateSolidColorBrush(GridShapeNormalColor);
            using var emphBrush = rt.CreateSolidColorBrush(GridShapeEmphColor);

            rt.DrawLine(new Vector2(centerX, 0), new Vector2(centerX, height), emphBrush, 1.0f);
            rt.DrawLine(new Vector2(startX, centerY), new Vector2(startX + width, centerY), emphBrush, 1.0f);

            for (float r = 0.5f; r <= 1.0f; r += 0.5f)
            {
                rt.DrawEllipse(new Ellipse(new Vector2(centerX, centerY), r * scale, r * scale), normalBrush, 0.5f);
            }
        }
    }
}
