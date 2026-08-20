using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Numerics;
using SRdeck.Renderers.Compat.Direct2D1;
using SRdeck.Models;
using Color = SRdeck.Renderers.Compat.Mathematics.Color4;

namespace SRdeck.Renderers;

internal class SpectrumRenderer : IDisposable
{
    private readonly object _lockObj = new object();
    private ID2D1RenderTarget? _d2dRenderTarget;
    private ID2D1SolidColorBrush? _bgEmphBrush;
    private ID2D1SolidColorBrush? _bgNormalBrush;
    private ID2D1SolidColorBrush? _curveStrokeBrush;
    private ID2D1SolidColorBrush? _curveFillBrush;
    private int _lastCurveColorMode = int.MinValue;

    private bool _disposed;
    private int _lastWidth;
    private int _lastHeight;
    private const float _SPECTRUM_VIEW_RANGE_DB = AppConstants.SPECTRUM_VIEW_RANGE_DB; 
    private float _gridTopDb = AppConstants.DEFAULT_GRID_TOP_DB; 
    private float _rfCalOffset = AppConstants.RF_CAL_OFFSET; 
    private int _biasDb2;
    private float _lastDisplayBw = 7000000f;
    private float _lastRoundingHz = 500000f;

    private Vector2[]? _spectrumPoints;

    public int BiasDb2 { get => _biasDb2; set => _biasDb2 = value; }
    public float GridTopDb { get => _gridTopDb; set => _gridTopDb = value; }
    public float RfCalOffset { get => _rfCalOffset; set => _rfCalOffset = value; }

    private Action<System.Windows.Media.ImageSource>? _onImageUpdated;
    public Action<System.Windows.Media.ImageSource>? OnImageUpdated
    {
        get => _onImageUpdated;
        set
        {
            _onImageUpdated = value;
            var img = _d2dRenderTarget?.LastImageSource;
            if (img != null) _onImageUpdated?.Invoke(img);
        }
    }

    public SpectrumRenderer()
    {
        _lastWidth = 10;
        _lastHeight = 10;
        Application.Current?.Dispatcher?.Invoke(() => {
            CreateRenderTarget();
        });
    }

    public void SetImageSize(int width, int height)
    {
        lock (_lockObj)
        {
            int num = Math.Max(10, width);
            int num2 = Math.Max(10, height);
            if (_lastWidth != num || _lastHeight != num2 || _d2dRenderTarget == null)
            {
                _lastWidth = num;
                _lastHeight = num2;
                Application.Current?.Dispatcher?.InvokeAsync(() => {
                    CreateRenderTarget();
                });
            }
        }
    }

    private void CreateRenderTarget()
    {
        lock (_lockObj)
        {
            _d2dRenderTarget?.Dispose();
            _bgEmphBrush?.Dispose(); _bgEmphBrush = null;
            _bgNormalBrush?.Dispose(); _bgNormalBrush = null;
            _curveStrokeBrush?.Dispose(); _curveStrokeBrush = null;
            _curveFillBrush?.Dispose(); _curveFillBrush = null;
            _lastCurveColorMode = int.MinValue;
            _d2dRenderTarget = DirectXManager.Instance.CreateRenderTarget(_lastWidth, _lastHeight);
            _bgEmphBrush = _d2dRenderTarget.CreateSolidColorBrush(new Color(45f/255f, 45f/255f, 45f/255f, 1f));
            _bgNormalBrush = _d2dRenderTarget.CreateSolidColorBrush(new Color(70f/255f, 70f/255f, 70f/255f, 1f));
            
            _d2dRenderTarget.BeginDraw();
            _d2dRenderTarget.Clear(new Color(0f, 0f, 0f, 1f));
            DrawBackgroundLayers(_d2dRenderTarget, _lastWidth, _lastHeight, _lastDisplayBw, _lastRoundingHz);
            _d2dRenderTarget.EndDraw();
            OnImageUpdated?.Invoke(_d2dRenderTarget?.LastImageSource!);
        }
    }

    public void SetBias(int b2)
    {
        _biasDb2 = b2;
    }

    public void InitializeTargetImage(float[] fftDat, ref RadioState r, RadioControl p, float displayBw = 7000000f, float roundingHz = 500000f)
    {
        lock (_lockObj)
        {
            _lastDisplayBw = displayBw;
            _lastRoundingHz = roundingHz;
            if (_disposed || _d2dRenderTarget == null) return;

            int width = Math.Max(1, _lastWidth);
            int height = Math.Max(1, _lastHeight);

            SyncSpectrumPoints(fftDat, p, width, height, displayBw);
            SyncSpectrumScene(p, width, height, displayBw, roundingHz);

            if (_d2dRenderTarget?.LastImageSource != null) OnImageUpdated?.Invoke(_d2dRenderTarget.LastImageSource);
        }
    }

    private void SyncSpectrumScene(RadioControl p, int width, int height, float displayBw, float roundingHz)
    {
        _d2dRenderTarget!.BeginDraw();
        _d2dRenderTarget.Clear(new Color(0f, 0f, 0f, 1f));
        
        DrawBackgroundLayers(_d2dRenderTarget, width, height, displayBw, roundingHz, p.CenterFreqHz);
        
        if (_spectrumPoints != null)
        {
            DrawSpectrumCurve(_d2dRenderTarget, _spectrumPoints, p, height);
        }
        
        _d2dRenderTarget.EndDraw();
    }

    private void SyncSpectrumPoints(float[] fftDat, RadioControl p, int width, int height, float displayBw)
    {
        if (fftDat == null || fftDat.Length == 0) return;
        
        int fftSize = fftDat.Length;
        float fullBw = p.FsHz > 0 ? p.FsHz : AppConstants.FULL_BW;
        float safeDisplayBw = Math.Max(1f, displayBw);
        bool widerDisplayThanSignal = safeDisplayBw > fullBw;

        int displayBinCount;
        int marginBins;
        float drawRatio;
        if (widerDisplayThanSignal)
        {
            // Display span is wider than captured bandwidth:
            // keep frequency scale, draw spectrum only at center region.
            displayBinCount = fftSize;
            marginBins = 0;
            drawRatio = fullBw / safeDisplayBw;
        }
        else
        {
            displayBinCount = (int)(fftSize * (safeDisplayBw / fullBw));
            marginBins = (fftSize - displayBinCount) / 2;
            drawRatio = 1f;
        }
        displayBinCount = Math.Clamp(displayBinCount, 2, fftSize);
        float xStart = (1f - drawRatio) * width * 0.5f;
        float drawWidth = width * drawRatio;
        
        if (_spectrumPoints == null || _spectrumPoints.Length != displayBinCount)
        {
            _spectrumPoints = new Vector2[displayBinCount];
        }
        
        float xStep = (displayBinCount > 1) ? drawWidth / (displayBinCount - 1) : drawWidth;

        for (int i = 0; i < displayBinCount; i++)
        {
            int fftIdx = Math.Clamp(i + marginBins, 0, fftSize - 1);
            float sysDb = float.IsFinite(p.SystemDb) ? p.SystemDb : 0f;
            float physicalLevelDbm = fftDat[fftIdx] - sysDb + _rfCalOffset;
            float y = (physicalLevelDbm - _gridTopDb) / -_SPECTRUM_VIEW_RANGE_DB * height;
            y = Math.Clamp(y, 0f, (float)height - 1f);
            _spectrumPoints[i] = new Vector2(xStart + i * xStep, y);
        }
    }

    private void DrawBackgroundLayers(ID2D1RenderTarget rt, int width, int height, float displayBw, float roundingHz = 500000f, int centerFreqHz = 0)
    {
        var oldMode = rt.AntialiasMode;
        rt.AntialiasMode = AntialiasMode.Aliased;

        if (_bgEmphBrush == null || _bgNormalBrush == null) return;
        
        float stepDb = 10f;
        for (float db = (float)Math.Floor(_gridTopDb / stepDb) * stepDb; db > _gridTopDb - _SPECTRUM_VIEW_RANGE_DB; db -= stepDb)
        {
            if (db >= _gridTopDb) continue;
            float y = (float)Math.Round((_gridTopDb - db) / _SPECTRUM_VIEW_RANGE_DB * height);
            if (y <= 0 || y >= height) continue;
            long gridVal = (long)Math.Round(db);
            rt.DrawLine(new Vector2(0, y), new Vector2(width, y), (gridVal % 50 == 0) ? _bgNormalBrush : _bgEmphBrush);
        }

        // 垂直グリッド線（中心周波数を基準に対称配置）
        double safeDisplayBw = Math.Max(1.0, displayBw);
        var steps = RenderUtils.GetFrequencyGridSteps(safeDisplayBw);
        double subGridStep = steps.SubHz > 0 ? steps.SubHz : Math.Max(1.0, roundingHz / 5.0);
        int mainEvery = Math.Max(1, (int)Math.Round(steps.MainHz / subGridStep));
        double halfSpan = safeDisplayBw * 0.5;
        long kMin = (long)Math.Ceiling((centerFreqHz - halfSpan) / subGridStep);
        long kMax = (long)Math.Floor((centerFreqHz + halfSpan) / subGridStep);

        for (long k = kMin; k <= kMax; k++)
        {
            double relHz = k * subGridStep - centerFreqHz;
            if (relHz <= -halfSpan || relHz >= halfSpan) continue;
            float x = (float)Math.Round(((relHz + halfSpan) / safeDisplayBw) * width);
            rt.DrawLine(new Vector2(x, 0), new Vector2(x, height), (k % mainEvery == 0) ? _bgNormalBrush : _bgEmphBrush);
        }

        rt.AntialiasMode = oldMode;
    }

    private void DrawSpectrumCurve(ID2D1RenderTarget rt, Vector2[] pf, RadioControl p, int height)
    {
        if (pf.Length < 2) return;
        EnsureCurveBrushes(rt, p.WaterfallColorMode);
        if (_curveStrokeBrush == null || _curveFillBrush == null) return;
        
        using var pathGeometry = DirectXManager.Instance.D2DFactory.CreatePathGeometry();
        using (var sink = pathGeometry.Open())
        {
            sink.BeginFigure(new Vector2(pf[0].X, height), FigureBegin.Filled);
            sink.AddLines(pf);
            sink.AddLine(new Vector2(pf[pf.Length - 1].X, height));
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        rt.FillGeometry(pathGeometry, _curveFillBrush);
        
        using var lineGeometry = DirectXManager.Instance.D2DFactory.CreatePathGeometry();
        using (var sink = lineGeometry.Open())
        {
            sink.BeginFigure(pf[0], FigureBegin.Hollow);
            for (int i = 1; i < pf.Length; i++)
            {
                sink.AddLine(pf[i]);
            }
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        rt.DrawGeometry(lineGeometry, _curveStrokeBrush, 1.0f);
    }

    private void EnsureCurveBrushes(ID2D1RenderTarget rt, int colorMode)
    {
        if (_curveStrokeBrush != null && _curveFillBrush != null && _lastCurveColorMode == colorMode) return;
        ColorLUT.GetSpectrumColors(colorMode, out var fillColor, out var strokeColor);
        _curveStrokeBrush?.Dispose();
        _curveFillBrush?.Dispose();
        _curveStrokeBrush = rt.CreateSolidColorBrush(strokeColor);
        _curveFillBrush = rt.CreateSolidColorBrush(fillColor);
        _lastCurveColorMode = colorMode;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        lock (_lockObj)
        {
            if (!_disposed)
            {
                _d2dRenderTarget?.Dispose();
                _bgEmphBrush?.Dispose();
                _bgNormalBrush?.Dispose();
                _curveStrokeBrush?.Dispose();
                _curveFillBrush?.Dispose();
                _disposed = true;
            }
        }
    }

}
