using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Numerics;
using SRdeck.Renderers.Compat.Direct2D1;
using SRdeck.Renderers.Compat.DirectWrite;
using SRdeck.Models;
using Color = SRdeck.Renderers.Compat.Mathematics.Color4;
using DWTextAlignment = SRdeck.Renderers.Compat.DirectWrite.TextAlignment;
using SRdeck.DSP;
using SRdeck.Renderers.Visualizers;

namespace SRdeck.Renderers;

internal class DemodWaveRenderer : IDisposable
{
    private readonly object _lockObj = new object();
    private ID2D1RenderTarget? _d2dRenderTarget;

    private int _lastWidth;
    private int _lastHeight;
    private bool _disposed;
    private DemodWaveMode _currentMode = DemodWaveMode.Wave;
    
    private float[] _historyLData = new float[TOTAL_DATLEN_X5];
    private float[] _historyRData = new float[TOTAL_DATLEN_X5];
    private readonly bool[] _sqStates = new bool[5];
    private readonly int _dataLength = 3200;
    private const int TOTAL_DATLEN_X5 = 16000;

    private IDWriteTextFormat _labelFormat;

    private Dictionary<DemodWaveMode, IDemodVisualizer> _visualizers;

    private Action<ImageSource>? _onImageUpdated;
    public Action<ImageSource>? OnImageUpdated
    {
        get => _onImageUpdated;
        set
        {
            _onImageUpdated = value;
            var img = _d2dRenderTarget?.LastImageSource;
            if (img != null) _onImageUpdated?.Invoke(img);
        }
    }

    public DemodWaveRenderer()
    {
        _labelFormat = DirectXManager.Instance.DWriteFactory.CreateTextFormat("BIZ UDゴシック", 9.0f);
        _labelFormat.TextAlignment = DWTextAlignment.Center;
        _labelFormat.ParagraphAlignment = ParagraphAlignment.Center;
        _lastWidth = 10;
        _lastHeight = 10;

        _visualizers = new Dictionary<DemodWaveMode, IDemodVisualizer>
        {
            { DemodWaveMode.Wave, new WaveVisualizer() },
            { DemodWaveMode.FFT, new FftVisualizer() },
            { DemodWaveMode.Lissajous, new LissajousVectorVisualizer() },
            { DemodWaveMode.Vector, new LissajousVectorVisualizer() },
            { DemodWaveMode.Compare, new LissajousVectorVisualizer() }
        };

        Application.Current?.Dispatcher?.Invoke(() => {
            CreateRenderTarget();
        });
    }

    private void CreateRenderTarget()
    {
        lock (_lockObj)
        {
            _d2dRenderTarget?.Dispose();
            _d2dRenderTarget = DirectXManager.Instance.CreateRenderTarget(_lastWidth, _lastHeight);

            _d2dRenderTarget.BeginDraw();
            _d2dRenderTarget.Clear(new Color(0f, 0f, 0f, 1f));
            DrawCurrentMode();
            _d2dRenderTarget.EndDraw();

            if (_d2dRenderTarget?.LastImageSource != null) OnImageUpdated?.Invoke(_d2dRenderTarget.LastImageSource);
        }
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

    public void SetMode(DemodWaveMode mode)
    {
        lock (_lockObj)
        {
            if (_currentMode != mode)
            {
                _currentMode = mode;
                if (_d2dRenderTarget != null)
                {
                    Application.Current?.Dispatcher?.Invoke(() => {
                        lock (_lockObj)
                        {
                            if (_d2dRenderTarget == null) return;
                            _d2dRenderTarget.BeginDraw();
                            _d2dRenderTarget.Clear(new Color(0f, 0f, 0f, 1f));
                            DrawCurrentMode();
                            _d2dRenderTarget.EndDraw();
                            if (_d2dRenderTarget?.LastImageSource != null) OnImageUpdated?.Invoke(_d2dRenderTarget.LastImageSource);
                        }
                    });
                }
            }
        }
    }

    public void DrawWaveform(float[] historyLData, float[] historyRData, DemodWaveMode mode, bool isSquelchOpen, int waterfallColorMode)
    {
        lock (_lockObj)
        {
            _historyLData = historyLData;
            _historyRData = historyRData;
            
            Array.Copy(_sqStates, 1, _sqStates, 0, 4);
            _sqStates[4] = isSquelchOpen;
            
            if (_d2dRenderTarget != null && !_disposed)
            {
                _currentMode = mode;
                _d2dRenderTarget.BeginDraw();
                _d2dRenderTarget.Clear(new Color(0f, 0f, 0f, 1f));

                var ctx = new RenderContext
                {
                    RenderTarget = _d2dRenderTarget,
                    Width = _lastWidth,
                    Height = _lastHeight,
                    Datx5L = _historyLData,
                    Datx5R = _historyRData,
                    SqStates = _sqStates,
                    TotalDatLenX5 = TOTAL_DATLEN_X5,
                    DatLen = _dataLength,
                    WaterfallColorMode = waterfallColorMode,
                    LabelFormat = _labelFormat,
                    Mode = _currentMode
                };

                if (_visualizers.TryGetValue(_currentMode, out var visualizer))
                {
                    visualizer.Draw(ctx);
                }

                _d2dRenderTarget.EndDraw();
                
                if (_d2dRenderTarget?.LastImageSource != null) OnImageUpdated?.Invoke(_d2dRenderTarget.LastImageSource);
            }
        }
    }

    private void DrawCurrentMode()
    {
        if (_d2dRenderTarget == null) return;
        
        var ctx = new RenderContext
        {
            RenderTarget = _d2dRenderTarget,
            Width = _lastWidth,
            Height = _lastHeight,
            Datx5L = _historyLData,
            Datx5R = _historyRData,
            SqStates = _sqStates,
            TotalDatLenX5 = TOTAL_DATLEN_X5,
            DatLen = _dataLength,
            WaterfallColorMode = 0, // Default or passed if available
            LabelFormat = _labelFormat,
            Mode = _currentMode
        };

        if (_visualizers.TryGetValue(_currentMode, out var visualizer))
        {
            visualizer.Draw(ctx);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _d2dRenderTarget?.Dispose();
            _disposed = true;
        }
    }

    public ImageSource? GetLatestImageSource()
    {
        lock (_lockObj)
        {
            return _d2dRenderTarget?.LastImageSource;
        }
    }
}
