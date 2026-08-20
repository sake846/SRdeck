using System;
using System.IO;
using System.Windows;
using SRdeck.Renderers.Compat.DCommon;
using SRdeck.Renderers.Compat.Direct2D1;
using SRdeck.Renderers.Compat.DXGI;
using SRdeck.Models;
using Color = SRdeck.Renderers.Compat.Mathematics.Color4;
using SizeI = SRdeck.Renderers.Compat.Mathematics.SizeI;
using SRdeckPlugin.Contracts;

namespace SRdeck.Renderers;

internal partial class WaterfallRenderer : IDisposable
{
    private readonly object _lockObj = new object();
    private ID2D1RenderTarget? _d2dRenderTarget;
    private ID2D1Bitmap? _d2dWaterfallBitmap;
    private uint[]? _waterfallData;
    private byte[]? _rowByteBuffer;

    private int _biasDb;
    private bool _disposed;

    private int _lastWidth;
    private int _lastHeight;
    private int _lastWfColor;
    private float _rfCalOffset = AppConstants.RF_CAL_OFFSET; 
    public float RfCalOffset { get => _rfCalOffset; set => _rfCalOffset = value; }

    private float[]? _maxFftDat;
    private long _lastBlockSequence;
    private double _pendingWaterfallMs;
    private int _writeY;

    public void SetBias(int b) { _biasDb = b; }
    public int BiasDb { get => _biasDb; set => _biasDb = value; }
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

    public WaterfallRenderer()
    {
        _biasDb = 0; _lastWidth = 10; _lastHeight = 10; _lastWfColor = 1;
        Application.Current?.Dispatcher?.Invoke(() => { CreateRenderTarget(); });
    }

    private unsafe void CreateRenderTarget()
    {
        lock (_lockObj)
        {
            _d2dRenderTarget?.Dispose(); _d2dWaterfallBitmap?.Dispose(); _d2dWaterfallBitmap = null; _writeY = 0;
            _d2dRenderTarget = DirectXManager.Instance.CreateRenderTarget(_lastWidth, _lastHeight);
            var bmpProps = new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore));
            _d2dWaterfallBitmap = _d2dRenderTarget.CreateBitmap(new SizeI(_lastWidth, _lastHeight), IntPtr.Zero, 0, bmpProps);
            if (_waterfallData == null || _waterfallData.Length != _lastWidth * _lastHeight) _waterfallData = new uint[_lastWidth * _lastHeight];
            else { fixed (uint* ptr = _waterfallData) _d2dWaterfallBitmap.CopyFromMemory((IntPtr)ptr, (uint)(_lastWidth * 4)); }
            _d2dRenderTarget.BeginDraw(); _d2dRenderTarget.Clear(new Color(0f, 0f, 0f, 1f));
            if (_d2dWaterfallBitmap != null) _d2dRenderTarget.DrawBitmap(_d2dWaterfallBitmap, 1.0f, BitmapInterpolationMode.NearestNeighbor);
            _d2dRenderTarget.EndDraw();
            if (_d2dRenderTarget.LastImageSource != null) OnImageUpdated?.Invoke(_d2dRenderTarget.LastImageSource);
        }
    }


    public void ResetTiming()
    {
        lock (_lockObj)
        {
            _lastBlockSequence = 0;
            _pendingWaterfallMs = 0.0;
            if (_maxFftDat != null)
            {
                for (int i = 0; i < _maxFftDat.Length; i++) _maxFftDat[i] = float.MinValue;
            }
        }
    }

    public unsafe void ResetHistory()
    {
        lock (_lockObj)
        {
            _lastBlockSequence = 0;
            _pendingWaterfallMs = 0.0;
            _writeY = 0;
            if (_maxFftDat != null) Array.Fill(_maxFftDat, float.MinValue);
            if (_waterfallData != null)
            {
                Array.Clear(_waterfallData);
                if (_d2dWaterfallBitmap != null)
                {
                    fixed (uint* ptr = _waterfallData)
                        _d2dWaterfallBitmap.CopyFromMemory((IntPtr)ptr, (uint)(_lastWidth * 4));
                }
            }
        }
    }

    public void SetImageSize(int width, int height, float rfHz = 0f)
    {
        lock (_lockObj)
        {
            int num = Math.Max(10, width); int num2 = Math.Max(10, height);
            if (_lastWidth != num || _lastHeight != num2 || _d2dRenderTarget == null)
            {
                int oldW = _lastWidth; int oldH = _lastHeight; _lastWidth = num; _lastHeight = num2;
                ResizeHistory(oldW, oldH, num, num2);
                Application.Current?.Dispatcher?.InvokeAsync(() => { CreateRenderTarget(); });
            }
        }
    }

    public void InitializeTargetImage(float[] fftDat, RadioControl p, RadioState r, object? pz = null) { }

    public unsafe void SetWaterfall(float[] fftDat, long blockSequence, RadioControl p, RadioState r, float displayBw = 7000000f, int fftCenterFreqHz = 0, WaterfallTimeMode timeMode = WaterfallTimeMode.ThreeMinutes)
    {
        try
        {
            lock (_lockObj)
            {
                if (p.WaterfallColorMode != _lastWfColor) _lastWfColor = p.WaterfallColorMode;
                if (_disposed || _d2dRenderTarget == null || _d2dWaterfallBitmap == null || _waterfallData == null || _lastWidth <= 0 || _lastHeight <= 0) return;
                int w = _lastWidth; int h = _lastHeight; int num = fftDat.Length;
                if (_maxFftDat == null || _maxFftDat.Length != num)
                {
                    _maxFftDat = new float[num];
                    for (int i = 0; i < num; i++) _maxFftDat[i] = float.MinValue;
                    _lastBlockSequence = 0;
                    _pendingWaterfallMs = 0.0;
                }
                for (int j = 0; j < num; j++) if (fftDat[j] > _maxFftDat[j]) _maxFftDat[j] = fftDat[j];

                // WPF側は既にプロット領域（ラベル18px除外後）の Image 高さが渡るため、
                // ここでさらに TopLabelHeight を引かない。
                double plotHeight = Math.Max(1.0, h);
                double pixelDurationMs = WaterfallTimeModel.GetRowDurationMs(timeMode, plotHeight);
                long blockDelta = GetBlockDelta(blockSequence);
                _pendingWaterfallMs += blockDelta * WaterfallTimeModel.SourceRowDurationMs;
                int rowsToAdvance = (int)Math.Floor((_pendingWaterfallMs + 0.05) / pixelDurationMs);
                if (rowsToAdvance > h)
                {
                    rowsToAdvance = h;
                    _pendingWaterfallMs = 0.0;
                }

                float fullBw = p.FsHz > 0 ? p.FsHz : AppConstants.FULL_BW;
                if (_rowByteBuffer == null || _rowByteBuffer.Length != w * 4) _rowByteBuffer = new byte[w * 4];

                if (rowsToAdvance > 0)
                {
                    for (int n = 0; n < rowsToAdvance; n++)
                    {
                        _writeY = (_writeY - 1 + h) % h;
                        // Draw the held peak row for every elapsed display row; inserting black rows
                        // here creates periodic one-line dropouts when frame timing skips a row.
                        UpdateWaterfallRow(p, r, w, num, displayBw, fullBw, fftCenterFreqHz);
                    }

                    _pendingWaterfallMs -= rowsToAdvance * pixelDurationMs;
                    if (_pendingWaterfallMs < 0.0) _pendingWaterfallMs = 0.0;
                    for (int i = 0; i < num; i++) _maxFftDat[i] = float.MinValue;
                }
                else
                {
                    // rowsToAdvance == 0: まだ行を進める時刻でない → ピーク蓄積のみ。描画は次フレームへ
                }

                SyncWaterfallScene(w, h);
                if (_d2dRenderTarget?.LastImageSource != null) OnImageUpdated?.Invoke(_d2dRenderTarget.LastImageSource);
            }
        }
        catch (Exception ex) { File.AppendAllText("waterfall_crash.txt", $"Render Loop Error: {ex}\n"); }
    }

    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }

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

    protected virtual void Dispose(bool disposing)
    {
        lock (_lockObj) { if (!_disposed) { _d2dWaterfallBitmap?.Dispose(); _d2dRenderTarget?.Dispose();
                _disposed = true; } }
    }
}
