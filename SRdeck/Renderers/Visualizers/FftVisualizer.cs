using System;
using System.Numerics;
using SRdeck.Renderers.Compat;
using SRdeck.Renderers.Compat.Direct2D1;
using Color = SRdeck.Renderers.Compat.Mathematics.Color4;
using SRdeck.DSP;

namespace SRdeck.Renderers.Visualizers
{
    internal class FftVisualizer : VisualizerBase, IDemodVisualizer
    {
        private const float F_MIN = 20.0f;
        private const float F_MAX = 16000.0f;
        private static readonly float LOG_MIN = MathF.Log10(F_MIN);
        private static readonly float LOG_MAX = MathF.Log10(F_MAX);
        private static readonly float LOG_RANGE = LOG_MAX - LOG_MIN;

        private static readonly float[] RTA_BANDS = {
            20, 25, 31.5f, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 
            1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000
        };

        private float[] _peaksL = new float[30];
        private float[] _peaksR = new float[30];
        private int[] _peakHoldCountsL = new int[30];
        private int[] _peakHoldCountsR = new int[30];

        private FastFourierTransform _fftL = new FastFourierTransform(512);
        private FastFourierTransform _fftR = new FastFourierTransform(512);
        private HanningWindow _window = new HanningWindow(512);

        public override void Draw(RenderContext ctx)
        {
            if (ctx.Datx5L.Length < ctx.TotalDatLenX5 || ctx.Datx5R.Length < ctx.TotalDatLenX5) return;

            var rt = ctx.RenderTarget;
            int width = ctx.Width;
            int height = ctx.Height;

            DrawGrid(ctx);

            float halfH = height / 2f;
            Color colorStroke = GetStrokeColorByMode(ctx.WaterfallColorMode, ctx.BaseAlpha);
            Color colorFill = colorStroke;
            
            using var fillBrush = rt.CreateSolidColorBrush(colorFill);
            using var strokeBrush = rt.CreateSolidColorBrush(colorStroke);

            DrawFftPane(ctx, fillBrush, strokeBrush, ctx.Datx5L, _fftL, 0, halfH, width, 0);
            DrawFftPane(ctx, fillBrush, strokeBrush, ctx.Datx5R, _fftR, halfH, halfH, width, 1);

            DrawLabels(ctx, "1/3 Oct", 0, "L", true);
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
            float num3 = (float)Math.Round(((double)height / 10.0 * 5.0));
            rt.DrawLine(new Vector2(0, num3), new Vector2(width, num3), normalBrush);

            using var subGridBrush = rt.CreateSolidColorBrush(new Color(45f / 255f, 45f / 255f, 45f / 255f, 1f));
            using var mainGridBrush = rt.CreateSolidColorBrush(new Color(70f / 255f, 70f / 255f, 70f / 255f, 1f));

            float getLogX(float f)
            {
                float logF = MathF.Log10(f);
                return 5f + 290f * (logF - LOG_MIN) / LOG_RANGE;
            }

            for (int f = 20; f < 100; f += 10)
            {
                float x = getLogX(f);
                rt.DrawLine(new Vector2(x, 0), new Vector2(x, height), subGridBrush, 1.0f);
            }
            for (int f = 100; f < 1000; f += 100)
            {
                float x = getLogX(f);
                var brush = (f == 100) ? mainGridBrush : subGridBrush;
                rt.DrawLine(new Vector2(x, 0), new Vector2(x, height), brush, 1.0f);
            }
            for (int f = 1000; f <= 10000; f += 1000)
            {
                float x = getLogX(f);
                var brush = (f == 1000 || f == 10000) ? mainGridBrush : subGridBrush;
                rt.DrawLine(new Vector2(x, 0), new Vector2(x, height), brush, 1.0f);
            }

            rt.AntialiasMode = oldMode;
        }

        private void DrawFftPane(RenderContext ctx, ID2D1SolidColorBrush fillBrush, ID2D1SolidColorBrush strokeBrush, float[] data, FastFourierTransform fft, float top, float paneH, int w, int ch)
        {
            var rt = ctx.RenderTarget;
            const int FFT_SIZE = 512;
            int startIdx = ctx.TotalDatLenX5 - FFT_SIZE;
            
            _window.ApplyWindow(data, data, startIdx, fft.InputData); 
            fft.Execute(9, -14.2f); 
            
            float getValAtFreq(float freq)
            {
                float binIdx = (freq / 16000f) * 256f + 256f;
                int idx0 = (int)MathF.Floor(binIdx);
                int idx1 = (int)MathF.Ceiling(binIdx);
                idx0 = Math.Clamp(idx0, 256, 511);
                idx1 = Math.Clamp(idx1, 256, 511);
                float frac = binIdx - idx0;
                float v = (1f - frac) * fft.OutputData[idx0] + frac * fft.OutputData[idx1];
                return Math.Clamp((v + 40f) / 80f, 0f, 1f);
            }

            var oldAntialiasMode = rt.AntialiasMode;
            rt.AntialiasMode = AntialiasMode.Aliased;

            float[] peaks = (ch == 0) ? _peaksL : _peaksR;
            int[] counters = (ch == 0) ? _peakHoldCountsL : _peakHoldCountsR;

            for (int i = 0; i < RTA_BANDS.Length; i++)
            {
                float centerF = RTA_BANDS[i];
                float x0 = i * 10f;
                float val = getValAtFreq(centerF);
                
                if (val >= peaks[i])
                {
                    peaks[i] = val;
                    counters[i] = 10;
                }
                else
                {
                    if (counters[i] > 0)
                        counters[i]--;
                    else
                        peaks[i] = Math.Max(0f, peaks[i] - 0.03f);
                }

                float barH = (float)Math.Round(val * paneH);
                float peakH = (float)Math.Round(peaks[i] * paneH);

                float barY = (float)Math.Round(top + paneH - barH);
                float barBottom = (float)Math.Round(top + paneH);
                var rect = new RawRectF(x0, barY, x0 + 10f, barBottom);

                if (barH > 0)
                {
                    var baseColor = fillBrush.Color;
                    var darkColor = new Color(baseColor.R * 0.4f, baseColor.G * 0.4f, baseColor.B * 0.4f, baseColor.A);
                    var midColor = new Color(baseColor.R * 1.2f, baseColor.G * 1.2f, baseColor.B * 1.2f, baseColor.A);

                    var gradientStops = new ID2D1GradientStopCollection[] {
                        rt.CreateGradientStopCollection(new GradientStop[] {
                            new GradientStop { Color = darkColor, Position = 0.0f },
                            new GradientStop { Color = midColor, Position = 0.3f },
                            new GradientStop { Color = baseColor, Position = 0.7f },
                            new GradientStop { Color = darkColor, Position = 1.0f }
                        })
                    };

                    using (var cylinderBrush = rt.CreateLinearGradientBrush(
                        new LinearGradientBrushProperties { StartPoint = new Vector2(x0, 0), EndPoint = new Vector2(x0 + 10f, 0) },
                        gradientStops[0]))
                    {
                        rt.FillRectangle(rect, cylinderBrush);
                    }
                    
                    foreach(var gs in gradientStops) gs.Dispose();
                }

                if (peakH >= 0)
                {
                    float peakY = (float)Math.Round(top + paneH - Math.Max(1f, peakH));
                    var peakRect = new RawRectF(x0, peakY, x0 + 10f, peakY + 1f);
                    rt.FillRectangle(peakRect, strokeBrush);
                }
            }
            rt.AntialiasMode = oldAntialiasMode;
        }
    }
}
