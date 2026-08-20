using System;
using System.Numerics;
using SRdeck.Renderers.Compat.Direct2D1;
using Color = SRdeck.Renderers.Compat.Mathematics.Color4;

namespace SRdeck.Renderers.Visualizers
{
    internal class WaveVisualizer : VisualizerBase, IDemodVisualizer
    {
        public override void Draw(RenderContext ctx)
        {
            if (ctx.Datx5L.Length < ctx.TotalDatLenX5 || ctx.Datx5R.Length < ctx.TotalDatLenX5) return;

            var rt = ctx.RenderTarget;
            int width = ctx.Width;
            int height = ctx.Height;

            DrawGrid(ctx);

            int points = ctx.TotalDatLenX5;
            float step = (float)width / Math.Max(1f, (float)(points - 1));
            float centerY = height / 2f;

            for (int k = 0; k < 5; k++)
            {
                bool sq = ctx.SqStates != null && k < ctx.SqStates.Length ? ctx.SqStates[k] : false;
                Color color = GetColorByMode(ctx.WaterfallColorMode, ctx.BaseAlpha, sq);

                int startIdx = k * (points / 5);
                int endIdx = (k == 4) ? points : (k + 1) * (points / 5) + 1;

                if (endIdx - startIdx >= 2)
                {
                    using var brush = rt.CreateSolidColorBrush(color);
                    using var geom = DirectXManager.Instance.D2DFactory.CreatePathGeometry();
                    using (var sink = geom.Open())
                    {
                        float mix0 = (ctx.Datx5L[startIdx] + ctx.Datx5R[startIdx]) * 0.5f;
                        sink.BeginFigure(new Vector2((float)startIdx * step, centerY - mix0 * 0.45f * height), FigureBegin.Hollow);
                        for (int i = startIdx + 10; i < endIdx; i += 10)
                        {
                            if (i >= points) break;
                            float mix = (ctx.Datx5L[i] + ctx.Datx5R[i]) * 0.5f;
                            sink.AddLine(new Vector2((float)i * step, centerY - mix * 0.45f * height));
                        }
                        sink.EndFigure(FigureEnd.Open);
                        sink.Close();
                    }
                    rt.DrawGeometry(geom, brush, 1.0f);
                }
            }

            DrawLabels(ctx, "500 msec", 0, "L+R", false);
        }

        private void DrawGrid(RenderContext ctx)
        {
            var rt = ctx.RenderTarget;
            int width = ctx.Width;
            int height = ctx.Height;

            var oldMode = rt.AntialiasMode;
            rt.AntialiasMode = AntialiasMode.Aliased;

            using var normalBrush = rt.CreateSolidColorBrush(new Color(70f/255f, 70f/255f, 70f/255f, 1f));
            using var emphBrush = rt.CreateSolidColorBrush(new Color(45f/255f, 45f/255f, 45f/255f, 1f));

            for (int i = 1; i < 10; i++)
            {
                if (i == 5) continue;
                float y = (float)Math.Round(((double)height / 10.0 * i));
                rt.DrawLine(new Vector2(0, y), new Vector2(width, y), emphBrush);
            }
            for (int j = 1; j < 25; j++)
            {
                if (j % 5 == 0) continue;
                float x = (float)Math.Round(((double)width / 25.0 * j));
                rt.DrawLine(new Vector2(x, 0), new Vector2(x, height), emphBrush);
            }
            float num3 = (float)Math.Round(((double)height / 10.0 * 5.0));
            rt.DrawLine(new Vector2(0, num3), new Vector2(width, num3), normalBrush);
            for (int k = 1; k < 5; k++)
            {
                float x = (float)Math.Round(((double)width / 5.0 * k));
                rt.DrawLine(new Vector2(x, 0), new Vector2(x, height), normalBrush);
            }

            rt.AntialiasMode = oldMode;
        }
    }
}
