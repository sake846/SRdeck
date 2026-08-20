using System;
using System.Windows.Media;
using SRdeck.Models;
using SRdeckPlugin.Contracts;

namespace SRdeck.Renderers;

internal sealed class NativeWaterfallGpuPresenter : IDisposable
{
    private IntPtr _nativeSurface = IntPtr.Zero;
    private D3DImageInterop? _interop;
    private uint[] _rowPixels = Array.Empty<uint>();
    private uint[] _historyPixels = Array.Empty<uint>();
    private NativeGpuDrawApi.LineVertex[] _idleVertices = new NativeGpuDrawApi.LineVertex[12];
    private float[]? _maxFftDat;
    private int _width = 10;
    private int _height = 10;
    private long _lastBlockSequence;
    private double _pendingWaterfallMs;
    private int _lastColorMode = 1;
    public int LastInitStatus { get; private set; }

    public int BiasDb { get; set; }
    public float RfCalOffset { get; set; } = AppConstants.RF_CAL_OFFSET;
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
        IntPtr shared;
        try { rc = NativeGpuDrawApi.CreateSurface(_width, _height, out _nativeSurface, out shared); }
        catch { LastInitStatus = -911; return false; }
        if (rc != 0 || _nativeSurface == IntPtr.Zero) { LastInitStatus = (rc != 0) ? rc : -912; return false; }
        _interop = new D3DImageInterop();
        if (!_interop.TryInitializeFromSharedHandle(shared, _width, _height))
        {
            LastInitStatus = -913;
            _interop.Dispose();
            _interop = null;
            DisposeSurfaceOnly();
            return false;
        }
        _rowPixels = new uint[_width];
        if (_historyPixels.Length != _width * _height)
        {
            _historyPixels = new uint[_width * _height];
        }
        _lastBlockSequence = 0;
        _pendingWaterfallMs = 0.0;
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
        var oldHistory = _historyPixels;
        int oldWidth = _width;
        int oldHeight = _height;
        Initialize(width, height);
        RestoreResizedHistory(oldHistory, oldWidth, oldHeight);
    }

    public void Render(float[] fftDat, long blockSequence, RadioControl p, RadioState r, float displayBw, int fftCenterFreqHz = 0, WaterfallTimeMode timeMode = WaterfallTimeMode.ThreeMinutes)
    {
        if (!IsReady || fftDat == null || fftDat.Length == 0) return;
        if (p.WaterfallColorMode != _lastColorMode) _lastColorMode = p.WaterfallColorMode;
        int num = fftDat.Length;
        if (_maxFftDat == null || _maxFftDat.Length != num)
        {
            _maxFftDat = new float[num];
            for (int i = 0; i < num; i++) _maxFftDat[i] = float.MinValue;
            _lastBlockSequence = 0;
            _pendingWaterfallMs = 0.0;
        }
        for (int i = 0; i < num; i++) if (fftDat[i] > _maxFftDat[i]) _maxFftDat[i] = fftDat[i];

        double pixelDurationMs = WaterfallTimeModel.GetRowDurationMs(timeMode, Math.Max(1.0, _height));
        long blockDelta = GetBlockDelta(blockSequence);
        _pendingWaterfallMs += blockDelta * WaterfallTimeModel.SourceRowDurationMs;
        int rowsToAdvance = (int)Math.Floor((_pendingWaterfallMs + 0.05) / pixelDurationMs);
        if (rowsToAdvance > _height)
        {
            rowsToAdvance = _height;
            _pendingWaterfallMs = 0.0;
        }
        float fullBw = p.FsHz > 0 ? p.FsHz : AppConstants.FULL_BW;

        if (rowsToAdvance > 0)
        {
            if (!_interop!.TryBeginUpdate(out var update)) return;
            using (update)
            {
                for (int n = 0; n < rowsToAdvance; n++)
                {
                    // Draw the held peak row for every elapsed display row; inserting black rows
                    // here creates periodic one-line dropouts when frame timing skips a row.
                    SyncWaterfallRow(p, r, fftDat.Length, displayBw, fullBw, fftCenterFreqHz);
                }
            }
            _pendingWaterfallMs -= rowsToAdvance * pixelDurationMs;
            if (_pendingWaterfallMs < 0.0) _pendingWaterfallMs = 0.0;
            for (int i = 0; i < num; i++) _maxFftDat[i] = float.MinValue;
        }
        else
        {
            // Keep accumulating max bins until the next row-advance timing.
        }
    }


    public void ResetTiming()
    {
        _lastBlockSequence = 0;
        _pendingWaterfallMs = 0.0;
        if (_maxFftDat != null)
        {
            for (int i = 0; i < _maxFftDat.Length; i++) _maxFftDat[i] = float.MinValue;
        }
    }

    public void ResetHistory()
    {
        ResetTiming();
        if (_historyPixels.Length == _width * _height)
            Array.Fill(_historyPixels, 0xFF000000u);
        if (!IsReady || !_interop!.TryBeginUpdate(out var update)) return;
        using (update)
        {
            try { _ = NativeGpuDrawApi.ClearSurface(_nativeSurface, 0xFF000000u); }
            catch { }
        }
    }

    public unsafe void RenderIdle()
    {
        if (!IsReady) return;
        float split = Math.Max(1, (float)Math.Round(_height * 0.38));
        uint blue = 0xFF0D3D80u;
        uint red = 0xFFFF1010u;

        int n = 0;
        AddIdleRect(ref n, 0, 0, _width, split, blue);
        AddIdleRect(ref n, 0, split, _width, _height, red);
        if (!_interop!.TryBeginUpdate(out var update)) return;
        using (update)
        fixed (NativeGpuDrawApi.LineVertex* ptr = _idleVertices)
        {
            try { _ = NativeGpuDrawApi.DrawTriangles(_nativeSurface, (IntPtr)ptr, n, 1, 0xFF000000u); }
            catch { return; }
        }
    }

    public void RenderBlank()
    {
        if (!IsReady) return;
        if (!_interop!.TryBeginUpdate(out var update)) return;
        using (update)
        try
        {
            _ = NativeGpuDrawApi.ClearSurface(_nativeSurface, 0xFF000000u);
            if (_historyPixels.Length == _width * _height) Array.Fill(_historyPixels, 0xFF000000u);
        }
        catch { }
    }

    private void SyncWaterfallRow(RadioControl p, RadioState r, int fullFftSize, float displayBw, float fullBw, int fftCenterFreqHz)
    {
        uint[] lut = ColorLUT.GetLutBgr32(p.WaterfallColorMode);
        float safeDisplayBw = Math.Max(1f, displayBw);
        float halfDisplay = safeDisplayBw * 0.5f;
        float halfFull = fullBw * 0.5f;
        float centerOffsetHz = fftCenterFreqHz > 0 ? p.CenterFreqHz - fftCenterFreqHz : 0f;
        for (int x = 0; x < _width; x++)
        {
            float pixelStartNorm = (float)x / Math.Max(1, _width);
            float pixelEndNorm = (float)(x + 1) / Math.Max(1, _width);
            float hzStart = (pixelStartNorm * safeDisplayBw) - halfDisplay + centerOffsetHz;
            float hzEnd = (pixelEndNorm * safeDisplayBw) - halfDisplay + centerOffsetHz;
            if (hzEnd <= -halfFull || hzStart >= halfFull)
            {
                _rowPixels[x] = 0xFF000000u;
                continue;
            }
            float clippedHzStart = MathF.Max(hzStart, -halfFull);
            float clippedHzEnd = MathF.Min(hzEnd, halfFull);
            float sourceStartNorm = (clippedHzStart / fullBw) + 0.5f;
            float sourceEndNorm = (clippedHzEnd / fullBw) + 0.5f;
            int startIdx = Math.Clamp((int)MathF.Floor(sourceStartNorm * fullFftSize), 0, fullFftSize - 1);
            int endIdx = Math.Clamp((int)MathF.Ceiling(sourceEndNorm * fullFftSize), startIdx + 1, fullFftSize);
            float pMax = float.MinValue;
            for (int m = startIdx; m < endIdx; m++) if (_maxFftDat![m] > pMax) pMax = _maxFftDat[m];
            float systemDb = float.IsFinite(p.SystemDb) ? p.SystemDb : 0f;
            float minFloor = float.IsFinite(r.Min2FftPwr) ? r.Min2FftPwr : -120f;
            float physical = pMax - systemDb + RfCalOffset;
            int colorIdx = Math.Clamp((int)((physical - minFloor) * 4.0f + BiasDb), 0, 255);
            _rowPixels[x] = lut[colorIdx];
        }
        ScrollUploadTopRow();
    }

    private unsafe void ScrollUploadTopRow()
    {
        SyncHistoryTopRow();
        fixed (uint* ptr = _rowPixels)
        {
            try { _ = NativeGpuDrawApi.ScrollUploadTopRow(_nativeSurface, (IntPtr)ptr, _width); }
            catch { return; }
        }
    }

    private void SyncHistoryTopRow()
    {
        int required = _width * _height;
        if (_historyPixels.Length != required) _historyPixels = new uint[required];
        if (_height > 1)
        {
            Array.Copy(_historyPixels, 0, _historyPixels, _width, _width * (_height - 1));
        }
        Array.Copy(_rowPixels, 0, _historyPixels, 0, _width);
    }

    private unsafe void RestoreResizedHistory(uint[] oldHistory, int oldWidth, int oldHeight)
    {
        if (!IsReady || oldHistory == null || oldHistory.Length == 0 || oldWidth <= 0 || oldHeight <= 0) return;
        if (_historyPixels.Length != _width * _height) _historyPixels = new uint[_width * _height];
        float scaleX = oldWidth / (float)_width;
        float scaleY = oldHeight / (float)_height;
        for (int y = 0; y < _height; y++)
        {
            int sy = Math.Clamp((int)MathF.Round((y + 0.5f) * scaleY - 0.5f), 0, oldHeight - 1);
            int dstRow = y * _width;
            int srcRow = sy * oldWidth;
            for (int x = 0; x < _width; x++)
            {
                int sx = Math.Clamp((int)MathF.Round((x + 0.5f) * scaleX - 0.5f), 0, oldWidth - 1);
                _historyPixels[dstRow + x] = oldHistory[srcRow + sx];
            }
        }
        if (!_interop!.TryBeginUpdate(out var update)) return;
        using (update)
        fixed (uint* ptr = _historyPixels)
        {
            try { _ = NativeGpuDrawApi.UploadBgraSurface(_nativeSurface, (IntPtr)ptr, _width, _height); }
            catch { return; }
        }
    }

    private void AddIdleRect(ref int n, float x0, float y0, float x1, float y1, uint color)
    {
        _idleVertices[n++] = new NativeGpuDrawApi.LineVertex(x0, y0, color);
        _idleVertices[n++] = new NativeGpuDrawApi.LineVertex(x1, y0, color);
        _idleVertices[n++] = new NativeGpuDrawApi.LineVertex(x1, y1, color);
        _idleVertices[n++] = new NativeGpuDrawApi.LineVertex(x0, y0, color);
        _idleVertices[n++] = new NativeGpuDrawApi.LineVertex(x1, y1, color);
        _idleVertices[n++] = new NativeGpuDrawApi.LineVertex(x0, y1, color);
    }

    private long GetBlockDelta(long blockSequence)
    {
        if (blockSequence <= 0)
        {
            return 0;
        }

        if (_lastBlockSequence <= 0)
        {
            _lastBlockSequence = blockSequence;
            return 1;
        }

        if (blockSequence <= _lastBlockSequence)
        {
            return 0;
        }

        long delta = blockSequence - _lastBlockSequence;
        _lastBlockSequence = blockSequence;
        return delta;
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
            try { NativeGpuDrawApi.DestroySurface(_nativeSurface); } catch { }
            _nativeSurface = IntPtr.Zero;
        }
    }
}
