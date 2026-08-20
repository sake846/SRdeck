using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SRdeck.DSP;
using SRdeck.Models;
using SRdeck.Models.SDR;
using Color4 = SRdeck.Renderers.Compat.Mathematics.Color4;

namespace SRdeck.Renderers;

internal sealed partial class NativeDemodGpuPresenter : IDisposable
{
    private const int TotalDatLenX5 = 16000;
    private const int LissajousMapSize = 150;
    private const float LissajousDensityThreshold = 0.01f;
    private const int HeatmapLutSize = 256;
    private IntPtr _nativeSurface = IntPtr.Zero;
    private IntPtr _sharedHandle = IntPtr.Zero;
    private D3DImageInterop? _interop;
    private int _width = 10;
    private int _height = 10;
    private int _stride = 40;
    private byte[] _pixelBytes = new byte[40 * 10];
    private NativeGpuDrawApi.LineVertex[] _lineVertices = new NativeGpuDrawApi.LineVertex[4096];
    private NativeGpuDrawApi.LineVertex[] _triangleVertices = new NativeGpuDrawApi.LineVertex[2048];
    private readonly FastFourierTransform _fftL = new(512);
    private readonly FastFourierTransform _fftR = new(512);
    private readonly HanningWindow _window512 = new(512);
    private readonly float[] _fftPeaksL = new float[30];
    private readonly float[] _fftPeaksR = new float[30];
    private readonly int[] _fftPeakHoldL = new int[30];
    private readonly int[] _fftPeakHoldR = new int[30];
    private readonly uint[] _heatmapLut = BuildHeatmapLut();
    private RenderTargetBitmap? _rtb;
    public int LastInitStatus { get; private set; }

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
        _stride = _width * 4;
        EnsurePixelBuffer();
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

        if (rc != 0 || _nativeSurface == IntPtr.Zero || _sharedHandle == IntPtr.Zero)
        {
            LastInitStatus = (rc != 0) ? rc : -902;
            return false;
        }

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
                try
                {
                    _ = NativeGpuDrawApi.ClearSurface(_nativeSurface, 0xFF000000u);
                }
                catch
                {
                }
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

    public unsafe void Upload(ImageSource? source)
    {
        if (!IsReady || source == null) return;

        var srcBitmap = source as BitmapSource;
        if (srcBitmap == null)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, _width, _height));
                dc.DrawImage(source, new Rect(0, 0, _width, _height));
            }

            _rtb ??= new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
            if (_rtb.PixelWidth != _width || _rtb.PixelHeight != _height)
            {
                _rtb = new RenderTargetBitmap(_width, _height, 96, 96, PixelFormats.Pbgra32);
            }
            _rtb.Render(dv);
            srcBitmap = _rtb;
        }

        if (srcBitmap.PixelWidth != _width || srcBitmap.PixelHeight != _height)
        {
            srcBitmap = new TransformedBitmap(srcBitmap, new ScaleTransform(
                (double)_width / Math.Max(1, srcBitmap.PixelWidth),
                (double)_height / Math.Max(1, srcBitmap.PixelHeight)));
        }

        EnsurePixelBuffer();
        srcBitmap.CopyPixels(_pixelBytes, _stride, 0);

        if (!_interop!.TryBeginUpdate(out var update)) return;
        using (update)
        fixed (byte* ptr = _pixelBytes)
        {
            try
            {
                _ = NativeGpuDrawApi.UploadBgraSurface(_nativeSurface, (IntPtr)ptr, _width, _height);
            }
            catch
            {
                return;
            }
        }

    }

    public unsafe bool RenderDirect(float[] historyL, float[] historyR, bool[] sqStates, DemodWaveMode mode, int waterfallColorMode)
    {
        if (!IsReady || historyL.Length < 2 || historyR.Length < 2) return false;
        _currentTriangleCount = 0;

        int vertexCount = mode switch
        {
            DemodWaveMode.Wave => BuildWaveVertices(historyL, historyR, sqStates, waterfallColorMode),
            DemodWaveMode.FFT => BuildFftVertices(historyL, historyR, waterfallColorMode, sqStates.Length > 0 && sqStates[^1], out var triangleCount),
            DemodWaveMode.Lissajous => BuildLissajousVectorVertices(historyL, historyR, sqStates, waterfallColorMode, sqStates.Length > 0 && sqStates[^1], DemodWaveMode.Lissajous),
            DemodWaveMode.Vector => BuildLissajousVectorVertices(historyL, historyR, sqStates, waterfallColorMode, sqStates.Length > 0 && sqStates[^1], DemodWaveMode.Vector),
            DemodWaveMode.Compare => BuildLissajousVectorVertices(historyL, historyR, sqStates, waterfallColorMode, sqStates.Length > 0 && sqStates[^1], DemodWaveMode.Compare),
            _ => BuildWaveVertices(historyL, historyR, sqStates, waterfallColorMode)
        };
        if (vertexCount <= 0) return false;

        // A busy WPF render thread is not a native-rendering failure. Skip this display
        // frame instead of falling back to the CPU renderer and adding more work.
        if (!_interop!.TryBeginUpdate(out var update)) return true;
        using (update)
        fixed (NativeGpuDrawApi.LineVertex* ptr = _lineVertices)
        fixed (NativeGpuDrawApi.LineVertex* triPtr = _triangleVertices)
        {
            try
            {
                int rc;
                int triangles = _currentTriangleCount;
                if (triangles > 0)
                {
                    rc = NativeGpuDrawApi.DrawLinesEx(_nativeSurface, (IntPtr)ptr, vertexCount, 1, 0xFF000000u, 0);
                    if (rc != 0) return false;
                    rc = NativeGpuDrawApi.DrawTriangles(_nativeSurface, (IntPtr)triPtr, triangles, 0, 0xFF000000u);
                }
                else
                {
                    rc = NativeGpuDrawApi.DrawLines(_nativeSurface, (IntPtr)ptr, vertexCount, 1, 0xFF000000u);
                }
                if (rc != 0) return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private int _currentTriangleCount;

    public void Dispose()
    {
        if (_interop != null) _interop.Dispose();
        _interop = null;
        DisposeSurfaceOnly();
    }

    private void EnsurePixelBuffer()
    {
        int required = _stride * _height;
        if (_pixelBytes.Length != required)
        {
            _pixelBytes = new byte[required];
        }
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
