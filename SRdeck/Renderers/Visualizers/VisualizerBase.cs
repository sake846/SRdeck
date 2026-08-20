using System;
using System.Numerics;
using SRdeck.Renderers.Compat;
using SRdeck.Renderers.Compat.Direct2D1;
using Color = SRdeck.Renderers.Compat.Mathematics.Color4;

namespace SRdeck.Renderers.Visualizers
{
    internal abstract class VisualizerBase
    {
        public abstract void Draw(RenderContext ctx);

        protected Color GetHeatmapColor(float value)
        {
            if (value <= 0.1f)
            {
                float t = value / 0.1f;
                return new Color(0, 0, 1.0f, t);
            }
            else if (value <= 0.35f)
            {
                float t = (value - 0.1f) / 0.25f;
                return new Color(0, t, 1.0f, 1.0f);
            }
            else if (value <= 0.6f)
            {
                float t = (value - 0.35f) / 0.25f;
                return new Color(0, 1.0f, 1.0f - t, 1.0f);
            }
            else if (value <= 0.85f)
            {
                float t = (value - 0.6f) / 0.25f;
                return new Color(t, 1.0f, 0, 1.0f);
            }
            else
            {
                float t = Math.Min(1.0f, (value - 0.85f) / 0.15f);
                return new Color(1.0f, 1.0f - t, 0, 1.0f);
            }
        }

        protected Color GetFillColorByMode(int waterfallColorMode, float alphaScale)
        {
            if (waterfallColorMode == 1) // Green
            {
                float a = (200f / 255f) * alphaScale;
                return new Color(20f / 255f * a, 60f / 255f * a, 40f / 255f * a, 0.8f * alphaScale);
            }
            else if (waterfallColorMode == 2) // Amber
            {
                float a = (140f / 255f) * alphaScale;
                return new Color(200f / 255f * a, 110f / 255f * a, 0f, 0.8f * alphaScale);
            }
            else // Color (0)
            {
                float a = (120f / 255f) * alphaScale;
                return new Color(0f, 80f / 255f * a, 210f / 255f * a, 0.8f * alphaScale);
            }
        }

        protected Color GetStrokeColorByMode(int waterfallColorMode, float alphaScale, bool sqOpen = true)
        {
            if (waterfallColorMode == 1) // Green
                return sqOpen ? new Color(0f, 1f * alphaScale, 200f / 255f * alphaScale, 0.8f * alphaScale) : new Color(20f / 255f, 60f / 255f, 40f / 255f, 0.8f);
            else if (waterfallColorMode == 2) // Amber
                return sqOpen ? new Color(1f * alphaScale, 165f / 255f * alphaScale, 0f, 0.8f * alphaScale) : new Color(110f / 255f, 60f / 255f, 0f, 0.8f);
            else // Color (0)
                return sqOpen ? new Color(40f / 255f * alphaScale, 150f / 255f * alphaScale, 1f * alphaScale, 0.8f * alphaScale) : new Color(20f / 255f, 50f / 255f, 120f / 255f, 0.8f);
        }

        protected Color GetColorByMode(int waterfallColorMode, float alpha, bool sqOpen = true)
        {
            return GetStrokeColorByMode(waterfallColorMode, alpha, sqOpen);
        }

        protected void DrawLabels(RenderContext ctx, string timeText, float labelX, string labelL, bool drawRLabel, string labelR = "R")
        {
            var rt = ctx.RenderTarget;
            int width = ctx.Width;
            int height = ctx.Height;

            var oldMode = rt.AntialiasMode;
            rt.AntialiasMode = AntialiasMode.Aliased;

            using var labelBgBrush = rt.CreateSolidColorBrush(new Color(0, 0, 0, 160f / 255f));
            using var labelTextBrush = rt.CreateSolidColorBrush(new Color(214f / 255f, 214f / 255f, 214f / 255f, 1.0f));

            float textW = Math.Max(50f, timeText.Length * 7f);
            var rectTime = new RawRectF(width - textW, 0, width, 15);
            rt.FillRoundedRectangle(new RoundedRectangle { Rect = rectTime, RadiusX = 2, RadiusY = 2 }, labelBgBrush);
            rt.DrawText(timeText, ctx.LabelFormat, rectTime, labelTextBrush);

            float boxW = string.IsNullOrEmpty(labelL) ? 0f : (labelL.Length > 1 ? 30f : 15f);
            if (boxW > 0)
            {
                var rectL = new RawRectF(labelX, 0, labelX + boxW, 15);
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = rectL, RadiusX = 2, RadiusY = 2 }, labelBgBrush);
                rt.DrawText(labelL, ctx.LabelFormat, rectL, labelTextBrush);
            }

            if (drawRLabel)
            {
                var rectR = new RawRectF(labelX, height * 0.5f, labelX + 15, height * 0.5f + 15);
                rt.FillRoundedRectangle(new RoundedRectangle { Rect = rectR, RadiusX = 2, RadiusY = 2 }, labelBgBrush);
                rt.DrawText(labelR, ctx.LabelFormat, rectR, labelTextBrush);
            }

            rt.AntialiasMode = oldMode;
        }
    }
}
