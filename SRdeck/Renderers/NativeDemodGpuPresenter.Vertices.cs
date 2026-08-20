using System;
using System.Windows.Media;
using SRdeck.DSP;
using SRdeck.Models;
using SRdeck.Models.SDR;
using Color4 = SRdeck.Renderers.Compat.Mathematics.Color4;

namespace SRdeck.Renderers;

internal sealed partial class NativeDemodGpuPresenter
{
    private int BuildWaveVertices(float[] historyL, float[] historyR, bool[] sqStates, int waterfallColorMode)
    {
        int points = Math.Min(TotalDatLenX5, Math.Min(historyL.Length, historyR.Length));
        if (points < 2) return 0;
        const int channelCount = 5;
        int estimate = 2 * (9 + 24 + 1 + 4 + channelCount * Math.Max(0, points / 50));
        EnsureLineCapacity(estimate);

        int n = 0;
        uint normalGrid = 0xFF464646u;
        uint emphGrid = 0xFF2D2D2Du;
        for (int i = 1; i < 10; i++)
        {
            if (i == 5) continue;
            float y = MathF.Round(_height / 10f * i);
            AddLine(ref n, 0, y, _width, y, emphGrid);
        }
        for (int j = 1; j < 25; j++)
        {
            if (j % 5 == 0) continue;
            float x = MathF.Round(_width / 25f * j);
            AddLine(ref n, x, 0, x, _height, emphGrid);
        }
        float centerY = MathF.Round(_height * 0.5f);
        AddLine(ref n, 0, centerY, _width, centerY, normalGrid);
        for (int k = 1; k < 5; k++)
        {
            float x = MathF.Round(_width / 5f * k);
            AddLine(ref n, x, 0, x, _height, normalGrid);
        }

        float step = (float)_width / Math.Max(1, points - 1);
        for (int k = 0; k < channelCount; k++)
        {
            bool sq = sqStates != null && k < sqStates.Length && sqStates[k];
            uint color = ToBgra(GetStrokeColorByMode(waterfallColorMode, 0.75f, sq));
            int start = k * (points / channelCount);
            int end = (k == channelCount - 1) ? points : (k + 1) * (points / channelCount) + 1;
            if (end - start < 2) continue;

            int prev = start;
            for (int i = start + 10; i < end; i += 10)
            {
                if (i >= points) break;
                float mix0 = (historyL[prev] + historyR[prev]) * 0.5f;
                float mix1 = (historyL[i] + historyR[i]) * 0.5f;
                AddLine(ref n, prev * step, centerY - mix0 * 0.45f * _height, i * step, centerY - mix1 * 0.45f * _height, color);
                prev = i;
            }
        }

        return n;
    }

    private int BuildFftVertices(float[] historyL, float[] historyR, int waterfallColorMode, bool sqOpen, out int triangleCount)
    {
        _currentTriangleCount = 0;
        triangleCount = 0;
        int count = Math.Min(historyL.Length, historyR.Length);
        if (count < 512) return 0;
        EnsureLineCapacity(256);
        EnsureTriangleCapacity(2400);

        int lines = 0;
        BuildFftHorizontalGrid(ref lines);
        uint color = ToBgra(GetStrokeColorByMode(waterfallColorMode, 0.75f, sqOpen));
        BuildFftLogGrid(ref lines);
        DrawFftBars(historyL, _fftL, _fftPeaksL, _fftPeakHoldL, 0, _height * 0.5f, color, ref triangleCount, ref lines);
        DrawFftBars(historyR, _fftR, _fftPeaksR, _fftPeakHoldR, _height * 0.5f, _height * 0.5f, color, ref triangleCount, ref lines);
        _currentTriangleCount = triangleCount;
        triangleCount = _currentTriangleCount;
        return lines;
    }

    private void DrawFftBars(float[] data, FastFourierTransform fft, float[] peaks, int[] counters, float top, float paneH, uint stroke, ref int tri, ref int lines)
    {
        int start = Math.Max(0, data.Length - 512);
        _window512.ApplyWindow(data, data, start, fft.InputData);
        fft.Execute(9, -14.2f);
        int bars = RtaBands.Length;
        uint dark = ScaleBgra(stroke, 0.4f);
        uint mid = ScaleBgra(stroke, 1.2f);
        uint baseColor = stroke;
        for (int i = 0; i < bars; i++)
        {
            float centerF = RtaBands[i];
            float binIdx = centerF / 16000f * 256f + 256f;
            int idx0 = Math.Clamp((int)MathF.Floor(binIdx), 256, 511);
            int idx1 = Math.Clamp((int)MathF.Ceiling(binIdx), 256, 511);
            float frac = binIdx - idx0;
            float fftVal = (1f - frac) * fft.OutputData[idx0] + frac * fft.OutputData[idx1];
            float v = Math.Clamp((fftVal + 40f) / 80f, 0f, 1f);
            if (v >= peaks[i])
            {
                peaks[i] = v;
                counters[i] = 10;
            }
            else if (counters[i] > 0)
            {
                counters[i]--;
            }
            else
            {
                peaks[i] = Math.Max(0f, peaks[i] - 0.03f);
            }

            float barH = MathF.Round(v * paneH);
            float peakH = MathF.Round(peaks[i] * paneH);
            float x0 = i * 10f;
            float x1 = MathF.Min(_width, x0 + 10f);
            if (x0 >= _width || x1 <= x0) continue;

            float y0 = MathF.Round(top + paneH - barH);
            float y1 = MathF.Round(top + paneH);
            if (barH > 0f)
            {
                AddFftCylinderBar(ref tri, x0, y0, x1, y1, dark, mid, baseColor);
            }

            float peakY = MathF.Round(top + paneH - Math.Max(1f, peakH));
            AddRect(ref tri, x0, peakY, x1, peakY + 1f, stroke);
        }
    }

    private void AddFftCylinderBar(ref int tri, float x0, float y0, float x1, float y1, uint dark, uint mid, uint baseColor)
    {
        float w = x1 - x0;
        float xMid = x0 + w * 0.30f;
        float xBase = x0 + w * 0.70f;
        AddHorizontalGradientRect(ref tri, x0, y0, xMid, y1, dark, mid);
        AddHorizontalGradientRect(ref tri, xMid, y0, xBase, y1, mid, baseColor);
        AddHorizontalGradientRect(ref tri, xBase, y0, x1, y1, baseColor, dark);
    }

    private static readonly float[] RtaBands =
    {
        20, 25, 31.5f, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
        1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000
    };

    private float GetFftLogX(float f)
    {
        const float logMin = 1.30103f; // log10(20)
        const float logMax = 4.20412f; // log10(16000)
        return _width * (5f / 300f + (290f / 300f) * ((MathF.Log10(f) - logMin) / (logMax - logMin)));
    }

    private void BuildFftLogGrid(ref int n)
    {
        uint sub = 0xFF282828u;
        uint main = 0xFF3C3C3Cu;
        for (int f = 20; f < 100; f += 10) AddLine(ref n, GetFftLogX(f), 0, GetFftLogX(f), _height, sub);
        for (int f = 100; f < 1000; f += 100) AddLine(ref n, GetFftLogX(f), 0, GetFftLogX(f), _height, f == 100 ? main : sub);
        for (int f = 1000; f <= 10000; f += 1000) AddLine(ref n, GetFftLogX(f), 0, GetFftLogX(f), _height, (f == 1000 || f == 10000) ? main : sub);
    }

    private void BuildFftHorizontalGrid(ref int n)
    {
        uint normal = 0xFF3C3C3Cu;
        uint emph = 0xFF282828u;
        float paneH = _height * 0.5f;
        for (int i = 1; i < 5; i++)
        {
            float y1 = paneH * i / 5f;
            float y2 = paneH + y1;
            AddLine(ref n, 0, y1, _width, y1, emph);
            AddLine(ref n, 0, y2, _width, y2, emph);
        }
        AddLine(ref n, 0, paneH, _width, paneH, normal);
    }

    private int BuildLissajousVectorVertices(float[] historyL, float[] historyR, bool[] sqStates, int waterfallColorMode, bool sqOpen, DemodWaveMode mode)
    {
        int count = Math.Min(historyL.Length, historyR.Length);
        if (count < 2) return 0;
        EnsureLineCapacity(2600);
        EnsureTriangleCapacity(160000);
        int n = 0;
        uint color = ToBgra(GetStrokeColorByMode(waterfallColorMode, 0.42f, sqOpen));
        uint timeColor = ToBgra(GetStrokeColorByMode(waterfallColorMode, 1.0f, sqOpen));
        uint grid = 0xFF2D2D2Du;
        uint emph = 0xFF464646u;
        float h = _height;
        float wLis = _width * 0.4f;
        int tri = 0;
        if (mode == DemodWaveMode.Compare)
        {
            DrawShapeGrid(ref n, 0, wLis, grid, emph, true);
            DrawShapeGrid(ref n, _width - wLis, wLis, grid, emph, false);
            AddDensityMap(ref tri, historyL, historyR, sqStates, 0, 0, wLis, h, LissajousAnalyzer.AnalysisType.Lissajous);
            AddDensityMap(ref tri, historyL, historyR, sqStates, _width - wLis, 0, wLis, h, LissajousAnalyzer.AnalysisType.Vector);
            AddSeparatorRect(ref tri, MathF.Round(wLis) - 0.5f, 0, h, 0xFF555555u);
            AddSeparatorRect(ref tri, MathF.Round(_width - wLis) - 0.5f, 0, h, 0xFF555555u);
        }
        else
        {
            DrawShapeGrid(ref n, 0, wLis, grid, emph, mode == DemodWaveMode.Lissajous);
            AddDensityMap(
                ref tri,
                historyL,
                historyR,
                sqStates,
                0,
                0,
                wLis,
                h,
                mode == DemodWaveMode.Vector ? LissajousAnalyzer.AnalysisType.Vector : LissajousAnalyzer.AnalysisType.Lissajous);
            DrawTimePane(ref n, historyL, historyR, wLis, _width - wLis, timeColor);
            AddSeparatorRect(ref tri, MathF.Round(wLis) - 0.5f, 0, h, 0x33FFFFFFu);
        }
        _currentTriangleCount = tri;
        return n;
    }

    private void DrawShapeGrid(ref int n, float left, float width, uint grid, uint emph, bool lissajous)
    {
        float cx = left + width * 0.5f;
        float cy = _height * 0.5f;
        float scale = MathF.Min(width, _height) * 0.45f;
        AddLine(ref n, cx, 0, cx, _height, emph);
        AddLine(ref n, left, cy, left + width, cy, emph);
        if (lissajous)
        {
            AddLine(ref n, cx - scale, cy - scale, cx + scale, cy + scale, grid);
            AddLine(ref n, cx + scale, cy - scale, cx - scale, cy + scale, grid);
        }
        else
        {
            AddCircle(ref n, cx, cy, scale * 0.5f, grid);
            AddCircle(ref n, cx, cy, scale, grid);
        }
    }

    private void AddDensityMap(
        ref int tri,
        float[] historyL,
        float[] historyR,
        bool[] sqStates,
        float left,
        float top,
        float width,
        float height,
        LissajousAnalyzer.AnalysisType type)
    {
        float[] map = LissajousAnalyzer.CalculateDensityMap(historyL, historyR, sqStates, LissajousMapSize, type);
        float offsetX = MathF.Round(left + (width - LissajousMapSize) * 0.5f);
        float offsetY = MathF.Round(top + (height - LissajousMapSize) * 0.5f);

        for (int y = 0; y < LissajousMapSize; y++)
        {
            int row = y * LissajousMapSize;
            for (int x = 0; x < LissajousMapSize; x++)
            {
                float d = map[row + x];
                if (d <= LissajousDensityThreshold) continue;

                int idx = (int)(Math.Clamp(d, 0f, 1f) * (HeatmapLutSize - 1));
                uint color = _heatmapLut[idx];
                float px = offsetX + x;
                float py = offsetY + y;
                AddRect(ref tri, px, py, px + 1f, py + 1f, color);
            }
        }
    }

    private void AddSeparatorRect(ref int tri, float x, float y, float height, uint color)
    {
        AddRect(ref tri, x, y, x + 1f, y + height, color);
    }

    private void DrawTimePane(ref int n, float[] l, float[] r, float left, float width, uint color)
    {
        const uint waveNormal = 0xFF464646u;
        const uint waveEmph = 0xFF2D2D2Du;
        AddLine(ref n, left, _height * 0.5f, _width, _height * 0.5f, waveNormal);
        DrawChannelGrid(ref n, left, _width, _height * 0.25f, _height * 0.25f, waveNormal, waveEmph);
        DrawChannelGrid(ref n, left, _width, _height * 0.75f, _height * 0.25f, waveNormal, waveEmph);
        for (int j = 1; j < 20; j++)
        {
            float x = left + MathF.Round(width / 20f * j);
            AddLine(ref n, x, 0, x, _height, j % 4 == 0 ? waveNormal : waveEmph);
        }
        DrawLissajousTimeSeries(ref n, l, TotalDatLenX5, left, width, _height * 0.25f, _height * 0.22f, color);
        DrawLissajousTimeSeries(ref n, r, TotalDatLenX5, left, width, _height * 0.75f, _height * 0.22f, color);
    }

    private void DrawLissajousTimeSeries(ref int n, float[] data, int points, float left, float width, float centerY, float waveH, uint color)
    {
        points = Math.Min(points, data.Length);
        if (points < 2) return;

        float step = width / Math.Max(1, points - 1);
        float px = left;
        float py = centerY - data[0] * waveH;
        for (int i = 10; i < points; i += 10)
        {
            float x = left + i * step;
            float y = centerY - data[i] * waveH;
            AddLine(ref n, px, py, x, y, color);
            px = x;
            py = y;
        }
    }

    private void DrawChannelGrid(ref int n, float left, float right, float centerY, float waveH, uint normal, uint emph)
    {
        AddLine(ref n, left, centerY, right, centerY, normal);
        for (int k = 1; k <= 3; k++)
        {
            float offset = waveH * (k * 0.25f);
            AddLine(ref n, left, centerY - offset, right, centerY - offset, emph);
            AddLine(ref n, left, centerY + offset, right, centerY + offset, emph);
        }
    }

    private void AddCircle(ref int n, float cx, float cy, float r, uint color)
    {
        const int segments = 64;
        float px = cx + r;
        float py = cy;
        for (int i = 1; i <= segments; i++)
        {
            float a = i * (MathF.PI * 2f / segments);
            float x = cx + MathF.Cos(a) * r;
            float y = cy + MathF.Sin(a) * r;
            AddLine(ref n, px, py, x, y, color);
            px = x;
            py = y;
        }
    }

    private void AddLine(ref int n, float x0, float y0, float x1, float y1, uint color)
    {
        if (n + 2 > _lineVertices.Length) Array.Resize(ref _lineVertices, _lineVertices.Length * 2);
        _lineVertices[n++] = new NativeGpuDrawApi.LineVertex(x0, y0, color);
        _lineVertices[n++] = new NativeGpuDrawApi.LineVertex(x1, y1, color);
    }

    private void AddTriangle(ref int n, float x0, float y0, float x1, float y1, float x2, float y2, uint color)
    {
        if (n + 3 > _triangleVertices.Length) Array.Resize(ref _triangleVertices, _triangleVertices.Length * 2);
        _triangleVertices[n++] = new NativeGpuDrawApi.LineVertex(x0, y0, color);
        _triangleVertices[n++] = new NativeGpuDrawApi.LineVertex(x1, y1, color);
        _triangleVertices[n++] = new NativeGpuDrawApi.LineVertex(x2, y2, color);
    }

    private void AddTriangle(ref int n, float x0, float y0, uint c0, float x1, float y1, uint c1, float x2, float y2, uint c2)
    {
        if (n + 3 > _triangleVertices.Length) Array.Resize(ref _triangleVertices, _triangleVertices.Length * 2);
        _triangleVertices[n++] = new NativeGpuDrawApi.LineVertex(x0, y0, c0);
        _triangleVertices[n++] = new NativeGpuDrawApi.LineVertex(x1, y1, c1);
        _triangleVertices[n++] = new NativeGpuDrawApi.LineVertex(x2, y2, c2);
    }

    private void AddRect(ref int n, float x0, float y0, float x1, float y1, uint color)
    {
        AddTriangle(ref n, x0, y0, x1, y0, x1, y1, color);
        AddTriangle(ref n, x0, y0, x1, y1, x0, y1, color);
    }

    private void AddHorizontalGradientRect(ref int n, float x0, float y0, float x1, float y1, uint leftColor, uint rightColor)
    {
        AddTriangle(ref n, x0, y0, leftColor, x1, y0, rightColor, x1, y1, rightColor);
        AddTriangle(ref n, x0, y0, leftColor, x1, y1, rightColor, x0, y1, leftColor);
    }

    private static float Snap(float v) => MathF.Round(v) + 0.5f;

    private static uint ScaleBgra(uint bgra, float scale)
    {
        uint a = (bgra >> 24) & 0xFF;
        uint r = (uint)Math.Clamp((int)MathF.Round(((bgra >> 16) & 0xFF) * scale), 0, 255);
        uint g = (uint)Math.Clamp((int)MathF.Round(((bgra >> 8) & 0xFF) * scale), 0, 255);
        uint b = (uint)Math.Clamp((int)MathF.Round((bgra & 0xFF) * scale), 0, 255);
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    private void DrawSeriesLine(ref int n, float[] data, float left, float width, float centerY, float amp, uint color, int stepSamples)
    {
        int count = data.Length;
        int start = Math.Max(0, count - 16000);
        int span = count - start;
        if (span < 2) return;
        bool hasPrev = false;
        float px = 0, py = 0;
        for (int i = start; i < count; i += Math.Max(1, stepSamples))
        {
            float x = left + (i - start) * width / Math.Max(1, span - 1);
            float y = centerY - data[i] * amp;
            if (hasPrev) AddLine(ref n, px, py, x, y, color);
            px = x; py = y; hasPrev = true;
        }
    }

    private void EnsureLineCapacity(int required)
    {
        if (_lineVertices.Length < required)
        {
            Array.Resize(ref _lineVertices, required);
        }
    }

    private void EnsureTriangleCapacity(int required)
    {
        if (_triangleVertices.Length < required)
        {
            Array.Resize(ref _triangleVertices, required);
        }
    }

    private static uint ToBgra(Color4 c)
    {
        byte a = (byte)Math.Clamp((int)MathF.Round(c.A * 255f), 0, 255);
        byte r = (byte)Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255);
        byte g = (byte)Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255);
        byte b = (byte)Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255);
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    private static uint[] BuildHeatmapLut()
    {
        var lut = new uint[HeatmapLutSize];
        for (int i = 0; i < lut.Length; i++)
        {
            lut[i] = ToBgra(GetHeatmapColor(i / (float)(lut.Length - 1)));
        }
        return lut;
    }

    private static Color4 GetHeatmapColor(float value)
    {
        if (value <= 0.1f)
        {
            float t = value / 0.1f;
            return new Color4(0f, 0f, 1f, t);
        }
        if (value <= 0.35f)
        {
            float t = (value - 0.1f) / 0.25f;
            return new Color4(0f, t, 1f, 1f);
        }
        if (value <= 0.6f)
        {
            float t = (value - 0.35f) / 0.25f;
            return new Color4(0f, 1f, 1f - t, 1f);
        }
        if (value <= 0.85f)
        {
            float t = (value - 0.6f) / 0.25f;
            return new Color4(t, 1f, 0f, 1f);
        }

        float hot = Math.Min(1f, (value - 0.85f) / 0.15f);
        return new Color4(1f, 1f - hot, 0f, 1f);
    }

    private static Color4 GetStrokeColorByMode(int waterfallColorMode, float alphaScale, bool sqOpen)
    {
        return sqOpen ? new Color4(40f / 255f * alphaScale, 150f / 255f * alphaScale, 1f * alphaScale, 0.8f * alphaScale) : new Color4(20f / 255f, 50f / 255f, 120f / 255f, 0.8f);
    }
}
