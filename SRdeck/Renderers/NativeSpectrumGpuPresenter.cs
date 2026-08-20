using System;
using System.Diagnostics;
using System.Windows.Media;
using SRdeck.Models;

namespace SRdeck.Renderers;

internal sealed class NativeSpectrumGpuPresenter : IDisposable
{
    private IntPtr _nativeSurface = IntPtr.Zero;
    private IntPtr _sharedHandle = IntPtr.Zero;
    private D3DImageInterop? _interop;
    private NativeGpuDrawApi.LineVertex[] _lineVertices = new NativeGpuDrawApi.LineVertex[4096];
    private NativeGpuDrawApi.LineVertex[] _curveVertices = new NativeGpuDrawApi.LineVertex[8192];
    private NativeGpuDrawApi.LineVertex[] _triangleVertices = new NativeGpuDrawApi.LineVertex[24576];
    private int _width = 10;
    private int _height = 10;
    public int LastInitStatus { get; private set; }
    public double LastPrepareMilliseconds { get; private set; }
    public double LastLockMilliseconds { get; private set; }
    public double LastDrawMilliseconds { get; private set; }
    public double LastUnlockMilliseconds { get; private set; }

    public ImageSource? ImageSource => _interop?.Image;
    public bool IsReady => _nativeSurface != IntPtr.Zero && _interop != null && _interop.IsReady;

    public bool Initialize(int width, int height)
    {
        LastInitStatus = 0;
        _interop?.Dispose();
        _interop = null;
        DisposeSurfaceOnly();
        _width = Math.Max(10, width);
        _height = Math.Max(10, height);
        int rc;
        try
        {
            rc = NativeGpuDrawApi.CreateSurface(_width, _height, out _nativeSurface, out _sharedHandle);
        }
        catch
        {
            LastInitStatus = -901;
            return false;
        }
        if (rc != 0 || _nativeSurface == IntPtr.Zero || _sharedHandle == IntPtr.Zero) { LastInitStatus = (rc != 0) ? rc : -902; return false; }

        _interop = new D3DImageInterop();
        if (!_interop.TryInitializeFromSharedHandle(_sharedHandle, _width, _height))
        {
            LastInitStatus = -903;
            _interop.Dispose();
            _interop = null;
            DisposeSurfaceOnly();
            return false;
        }

        if (_interop.TryBeginUpdate(out var update))
        {
            using (update)
            {
                try { _ = NativeGpuDrawApi.ClearSurface(_nativeSurface, 0xFF000000u); } catch { }
            }
        }
        return true;
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(10, width);
        height = Math.Max(10, height);
        if (width == _width && height == _height && IsReady) return;
        Initialize(width, height);
    }

    public unsafe void Render(float[] fftDat, RadioControl p, float gridTopDb, float rfCalOffset)
    {
        Render(fftDat, p, gridTopDb, rfCalOffset, p.FsHz > 0 ? p.FsHz : AppConstants.FULL_BW);
    }

    public unsafe void Render(float[] fftDat, RadioControl p, float gridTopDb, float rfCalOffset, float displayBw, bool gridAnchorEnabled = false, double gridAnchorOffsetHz = 0.0, double gridAnchorRatio = 0.5, int fftCenterFreqHz = 0)
    {
        ResetTimings();
        if (!IsReady || fftDat == null || fftDat.Length == 0) return;
        long prepareStarted = Stopwatch.GetTimestamp();
        const float rangeDb = AppConstants.SPECTRUM_VIEW_RANGE_DB;

        int fftSize = fftDat.Length;
        float fullBw = p.FsHz > 0 ? p.FsHz : AppConstants.FULL_BW;
        float safeDisplayBw = Math.Max(1f, displayBw);
        double centerOffsetHz = fftCenterFreqHz > 0 ? (double)p.CenterFreqHz - fftCenterFreqHz : 0.0;
        int displayPointCount = Math.Max(2, _width);

        ColorLUT.GetSpectrumColors(p.WaterfallColorMode, out var fillColor4, out var strokeColor4);
        uint lineColor = ToBgra(strokeColor4);
        uint fillColor = ToBgra(fillColor4);

        EnsureLineCapacity(256);
        EnsureCurveCapacity(2 * displayPointCount);
        EnsureTriangleCapacity(6 * (displayPointCount - 1));
        int lineCount = BuildBackgroundGridLines(gridTopDb, displayBw, p.CenterFreqHz);
        int curveCount = 0;
        int triangleCount = 0;

        float prevX = 0f;
        float prevY = 0f;
        bool hasPrev = false;

        for (int i = 0; i < displayPointCount; i++)
        {
            float x = i;
            double xRatio = displayPointCount > 1 ? (double)i / (displayPointCount - 1) : 0.5;
            double relativeHz = (xRatio - 0.5) * safeDisplayBw + centerOffsetHz;
            double sourcePosition = (relativeHz / fullBw + 0.5) * (fftSize - 1);
            if (sourcePosition < 0.0 || sourcePosition > fftSize - 1)
            {
                hasPrev = false;
                continue;
            }
            int fftIdx0 = (int)Math.Floor(sourcePosition);
            int fftIdx1 = Math.Min(fftSize - 1, fftIdx0 + 1);
            float fraction = (float)(sourcePosition - fftIdx0);
            float fftValue = fftDat[fftIdx0] + (fftDat[fftIdx1] - fftDat[fftIdx0]) * fraction;
            float sysDb = float.IsFinite(p.SystemDb) ? p.SystemDb : 0f;
            float physicalDbm = fftValue - sysDb + rfCalOffset;
            float y = (physicalDbm - gridTopDb) / -rangeDb * _height;
            y = Math.Clamp(y, 0, _height - 1);

            if (hasPrev)
            {
                AddTriangle(ref triangleCount, prevX, prevY, x, y, x, _height, fillColor);
                AddTriangle(ref triangleCount, prevX, prevY, x, _height, prevX, _height, fillColor);
                AddCurveLine(ref curveCount, prevX, prevY, x, y, lineColor);
            }
            prevX = x;
            prevY = y;
            hasPrev = true;
        }

        LastPrepareMilliseconds = Stopwatch.GetElapsedTime(prepareStarted).TotalMilliseconds;
        if (!_interop!.TryBeginUpdate(out var update)) return;
        LastLockMilliseconds = _interop.LastLockMilliseconds;
        long drawStarted = Stopwatch.GetTimestamp();
        try
        {
            fixed (NativeGpuDrawApi.LineVertex* triPtr = _triangleVertices)
            fixed (NativeGpuDrawApi.LineVertex* linePtr = _lineVertices)
            fixed (NativeGpuDrawApi.LineVertex* curvePtr = _curveVertices)
            {
                try
                {
                    if (lineCount > 0)
                    {
                        _ = NativeGpuDrawApi.DrawLinesEx(_nativeSurface, (IntPtr)linePtr, lineCount, 1, 0xFF000000u, 0);
                    }
                    if (triangleCount > 0)
                    {
                        int rc = NativeGpuDrawApi.DrawTrianglesEx(_nativeSurface, (IntPtr)triPtr, triangleCount, lineCount > 0 ? 0 : 1, 0xFF000000u, curveCount > 0 ? 0 : 1);
                        if (rc != 0) return;
                    }
                    if (curveCount > 0)
                    {
                        _ = NativeGpuDrawApi.DrawLines(_nativeSurface, (IntPtr)curvePtr, curveCount, 0, 0xFF000000u);
                    }
                    else if (triangleCount == 0 && lineCount > 0)
                    {
                        _ = NativeGpuDrawApi.DrawLines(_nativeSurface, (IntPtr)linePtr, lineCount, 1, 0xFF000000u);
                    }
                }
                catch { return; }
            }
        }
        finally
        {
            LastDrawMilliseconds = Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
            update.Dispose();
            LastUnlockMilliseconds = _interop.LastUnlockMilliseconds;
        }
    }

    private void ResetTimings()
    {
        LastPrepareMilliseconds = 0;
        LastLockMilliseconds = 0;
        LastDrawMilliseconds = 0;
        LastUnlockMilliseconds = 0;
    }

    public unsafe void RenderGridOnly(float gridTopDb, float displayBw, bool gridAnchorEnabled = false, double gridAnchorOffsetHz = 0.0, double gridAnchorRatio = 0.5, int centerFreqHz = 0)
    {
        if (!IsReady) return;
        int lineCount = BuildBackgroundGridLines(gridTopDb, displayBw, centerFreqHz);
        if (!_interop!.TryBeginUpdate(out var update)) return;
        using (update)
        fixed (NativeGpuDrawApi.LineVertex* ptr = _lineVertices)
        {
            try { _ = NativeGpuDrawApi.DrawLines(_nativeSurface, (IntPtr)ptr, lineCount, 1, 0xFF000000u); }
            catch { return; }
        }
    }

    private int BuildBackgroundGridLines(float gridTopDb, float displayBw, int centerFreqHz)
    {
        EnsureLineCapacity(256);
        int n = 0;
        const float rangeDb = AppConstants.SPECTRUM_VIEW_RANGE_DB;
        for (int db = (int)MathF.Floor(gridTopDb / 10f) * 10; db > gridTopDb - rangeDb; db -= 10)
        {
            float y = MathF.Round((gridTopDb - db) / rangeDb * _height);
            if (y <= 0 || y >= _height) continue;
            uint c = (db % 50 == 0) ? 0xFF464646u : 0xFF2D2D2Du;
            AddLine(ref n, 0, y, _width, y, c);
        }

        double safeDisplayBwD = Math.Max(1.0, displayBw);
        var steps = RenderUtils.GetFrequencyGridSteps(safeDisplayBwD);
        double subGridStep = steps.SubHz > 0 ? steps.SubHz : Math.Max(1.0, 500000.0 / 5.0);
        int mainEvery = Math.Max(1, (int)Math.Round(steps.MainHz / subGridStep));
        double halfSpan = safeDisplayBwD * 0.5;
        long kMin = (long)Math.Ceiling((centerFreqHz - halfSpan) / subGridStep);
        long kMax = (long)Math.Floor((centerFreqHz + halfSpan) / subGridStep);
        for (long k = kMin; k <= kMax; k++)
        {
            double relHz = k * subGridStep - centerFreqHz;
            if (relHz <= -halfSpan || relHz >= halfSpan) continue;
            float x = (float)Math.Round(((relHz + halfSpan) / safeDisplayBwD) * _width);
            if (x <= 0 || x >= _width) continue;
            uint c = (k % mainEvery == 0) ? 0xFF464646u : 0xFF2D2D2Du;
            AddLine(ref n, x, 0, x, _height, c);
        }
        return n;
    }

    private void AddLine(ref int n, float x0, float y0, float x1, float y1, uint color)
    {
        if (n + 2 > _lineVertices.Length) Array.Resize(ref _lineVertices, _lineVertices.Length * 2);
        _lineVertices[n++] = new NativeGpuDrawApi.LineVertex(x0, y0, color);
        _lineVertices[n++] = new NativeGpuDrawApi.LineVertex(x1, y1, color);
    }

    private void AddCurveLine(ref int n, float x0, float y0, float x1, float y1, uint color)
    {
        if (n + 2 > _curveVertices.Length) Array.Resize(ref _curveVertices, _curveVertices.Length * 2);
        _curveVertices[n++] = new NativeGpuDrawApi.LineVertex(x0, y0, color);
        _curveVertices[n++] = new NativeGpuDrawApi.LineVertex(x1, y1, color);
    }

    private void AddTriangle(ref int n, float x0, float y0, float x1, float y1, float x2, float y2, uint color)
    {
        if (n + 3 > _triangleVertices.Length) Array.Resize(ref _triangleVertices, _triangleVertices.Length * 2);
        _triangleVertices[n++] = new NativeGpuDrawApi.LineVertex(x0, y0, color);
        _triangleVertices[n++] = new NativeGpuDrawApi.LineVertex(x1, y1, color);
        _triangleVertices[n++] = new NativeGpuDrawApi.LineVertex(x2, y2, color);
    }

    private void EnsureLineCapacity(int required)
    {
        if (_lineVertices.Length < required) Array.Resize(ref _lineVertices, required);
    }

    private void EnsureTriangleCapacity(int required)
    {
        if (_triangleVertices.Length < required) Array.Resize(ref _triangleVertices, required);
    }

    private void EnsureCurveCapacity(int required)
    {
        if (_curveVertices.Length < required) Array.Resize(ref _curveVertices, required);
    }

    private static uint ToBgra(SRdeck.Renderers.Compat.Mathematics.Color4 c)
    {
        byte a = (byte)Math.Clamp((int)MathF.Round(c.A * 255f), 0, 255);
        byte r = (byte)Math.Clamp((int)MathF.Round(c.R * 255f), 0, 255);
        byte g = (byte)Math.Clamp((int)MathF.Round(c.G * 255f), 0, 255);
        byte b = (byte)Math.Clamp((int)MathF.Round(c.B * 255f), 0, 255);
        return (uint)((a << 24) | (r << 16) | (g << 8) | b);
    }

    public void Dispose()
    {
        if (_interop != null) _interop.Dispose();
        _interop = null;
        DisposeSurfaceOnly();
    }

    private void DisposeSurfaceOnly()
    {
        if (_nativeSurface != IntPtr.Zero)
        {
            NativeGpuDrawApi.DestroySurface(_nativeSurface);
            _nativeSurface = IntPtr.Zero;
            _sharedHandle = IntPtr.Zero;
        }
    }
}
