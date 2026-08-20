using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SRdeck.DSP;

public sealed class GpuFftRunner : IDisposable
{
    private static class NativeMethods
    {
        private const string DllName = "sr_gpu";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_create")]
        public static extern int Create(int fftSize, int logN, int maxBatchSize, IntPtr window, out IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_destroy")]
        public static extern void Destroy(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_process_packed")]
        public static extern int ProcessPacked(
            IntPtr handle,
            short[] inputI,
            short[] inputQ,
            int inputLength,
            int[] offsets,
            int batchCount,
            float offset,
            [Out] float[] outputDbFlat);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_process_float")]
        public static extern int ProcessFloat(
            IntPtr handle,
            [In] float[] inputIFlat,
            [In] float[] inputQFlat,
            int batchCount,
            float offset,
            [Out] float[] outputDbFlat);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_get_last_timings")]
        public static extern int GetLastTimings(
            IntPtr handle,
            out double packMs,
            out double uploadMs,
            out double dispatchMs,
            out double readbackMs);
    }

    private readonly int _fftSize;
    private readonly int _logN;
    private readonly int _maxBatchSize;
    private readonly int _capacity;
    private readonly IntPtr _nativeHandle;
    private readonly float[] _nativeOut;
    private float[]? _flatI;
    private float[]? _flatQ;
    private FastFourierTransform[]? _cpuFfts;
    private Complex[][]? _cpuInputs;
    private bool _disposed;
    private bool _hasPackedOutput;

    public bool IsAvailable => _nativeHandle != IntPtr.Zero && !_disposed;
    public int FftSize => _fftSize;
    public int LogN => _logN;
    public int MaxBatchSize => _maxBatchSize;
    public string GpuInfo { get; private set; } = "DirectGPU(native)";

    public double LastTimePrep { get; private set; }
    public double LastTimeCopyFrom { get; private set; }
    public double LastTimeShader { get; private set; }
    public double LastTimeCopyTo { get; private set; }
    public double LastTimePost { get; private set; }
    public double LastTimePack { get; private set; }
    public double LastTimeUploadNative { get; private set; }
    public double LastTimeDispatch { get; private set; }
    public double LastTimeReadback { get; private set; }

    public GpuFftRunner(int fftSize, int logN, int maxBatchSize = 32, float[]? window = null)
    {
        _fftSize = fftSize;
        _logN = logN;
        _maxBatchSize = maxBatchSize;
        _capacity = _fftSize * _maxBatchSize;
        _nativeOut = new float[_capacity];
        try
        {
            IntPtr wptr = IntPtr.Zero;
            GCHandle wHandle = default;
            if (window != null && window.Length >= _fftSize)
            {
                wHandle = GCHandle.Alloc(window, GCHandleType.Pinned);
                wptr = wHandle.AddrOfPinnedObject();
            }

            try
            {
                int hr = NativeMethods.Create(_fftSize, _logN, _maxBatchSize, wptr, out _nativeHandle);
                if (hr != 0 || _nativeHandle == IntPtr.Zero)
                {
                    GpuInfo = $"DirectGPU init failed (code={hr})";
                }
            }
            finally
            {
                if (wHandle.IsAllocated) wHandle.Free();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
        {
            GpuInfo = $"DirectGPU unavailable: {ex.GetType().Name}";
        }
    }

    public void ProcessBatch(float[][] inputI, float[][] inputQ, float[][] outputDb, float offset, int batchCount = 10)
    {
        if (_disposed) return;
        if (batchCount > _maxBatchSize) batchCount = _maxBatchSize;
        int elementCount = _fftSize * batchCount;
        EnsureFloatInputBuffers(elementCount);
        var flatI = _flatI!;
        var flatQ = _flatQ!;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, batchCount, b =>
        {
            int baseIdx = b * _fftSize;
            Array.Copy(inputI[b], 0, flatI, baseIdx, _fftSize);
            Array.Copy(inputQ[b], 0, flatQ, baseIdx, _fftSize);
        });
        sw.Stop();
        LastTimePrep = sw.Elapsed.TotalMilliseconds;

        if (IsAvailable)
        {
            sw.Restart();
            int rc = NativeMethods.ProcessFloat(_nativeHandle, flatI, flatQ, batchCount, offset, _nativeOut);
            sw.Stop();
            LastTimeShader = sw.Elapsed.TotalMilliseconds;
            LastTimeCopyFrom = 0;
            LastTimeCopyTo = 0;

            if (rc != 0)
            {
                ProcessBatchCpuFallback(batchCount, outputDb, offset);
                return;
            }
        }
        else
        {
            ProcessBatchCpuFallback(batchCount, outputDb, offset);
            return;
        }

        sw.Restart();
        Parallel.For(0, batchCount, b =>
        {
            Array.Copy(_nativeOut, b * _fftSize, outputDb[b], 0, _fftSize);
        });
        sw.Stop();
        LastTimePost = sw.Elapsed.TotalMilliseconds;
    }

    public bool ProcessBatchPacked(short[] inputI, short[] inputQ, int[] offsets, float[][] outputDb, float offset, int batchCount = 10)
    {
        if (_disposed) return false;
        if (batchCount > _maxBatchSize) batchCount = _maxBatchSize;

        if (!IsAvailable)
        {
            ClearGpuTiming();
            return false;
        }

        var sw = Stopwatch.StartNew();
        int rc = NativeMethods.ProcessPacked(_nativeHandle, inputI, inputQ, inputI.Length, offsets, batchCount, offset, _nativeOut);
        sw.Stop();
        LastTimeShader = sw.Elapsed.TotalMilliseconds;
        LastTimePrep = 0;
        LastTimeCopyFrom = 0;
        LastTimeCopyTo = 0;
        ReadNativeTimings();

        if (rc != 0)
        {
            if (rc == 1)
            {
                if (_hasPackedOutput)
                {
                    CopyNativeOutput(outputDb, batchCount);
                    return true;
                }

                LastTimePost = 0;
                return false;
            }

            ClearGpuTiming();
            _hasPackedOutput = false;
            return false;
        }

        _hasPackedOutput = true;
        CopyNativeOutput(outputDb, batchCount);
        return true;
    }

    private void CopyNativeOutput(float[][] outputDb, int batchCount)
    {
        var sw = Stopwatch.StartNew();
        if (batchCount == 1)
        {
            Array.Copy(_nativeOut, 0, outputDb[0], 0, _fftSize);
        }
        else
        {
            Parallel.For(0, batchCount, b =>
            {
                Array.Copy(_nativeOut, b * _fftSize, outputDb[b], 0, _fftSize);
            });
        }
        sw.Stop();
        LastTimePost = sw.Elapsed.TotalMilliseconds;
    }

    private void ProcessBatchCpuFallback(int batchCount, float[][] outputDb, float bias)
    {
        EnsureCpuFallbackBuffers(batchCount);
        var cpuFfts = _cpuFfts!;
        var cpuInputs = _cpuInputs!;
        var flatI = _flatI!;
        var flatQ = _flatQ!;

        var sw = Stopwatch.StartNew();
        Parallel.For(0, batchCount, b =>
        {
            int baseIdx = b * _fftSize;
            var input = cpuInputs[b];
            for (int i = 0; i < _fftSize; i++)
            {
                input[i].X = flatI[baseIdx + i];
                input[i].Y = flatQ[baseIdx + i];
            }
            cpuFfts[b].Execute(_logN, bias);
            Array.Copy(cpuFfts[b].OutputData, 0, outputDb[b], 0, _fftSize);
        });
        sw.Stop();
        LastTimeShader = sw.Elapsed.TotalMilliseconds;
        LastTimeCopyFrom = 0;
        LastTimeCopyTo = 0;
        LastTimePost = 0;
        ClearNativeTimings();
    }

    private void EnsureFloatInputBuffers(int requiredLength)
    {
        if (_flatI != null && _flatI.Length >= requiredLength && _flatQ != null && _flatQ.Length >= requiredLength)
        {
            return;
        }

        _flatI = new float[requiredLength];
        _flatQ = new float[requiredLength];
    }

    private void EnsureCpuFallbackBuffers(int batchCount)
    {
        if (_cpuFfts == null || _cpuInputs == null)
        {
            _cpuFfts = new FastFourierTransform[_maxBatchSize];
            _cpuInputs = new Complex[_maxBatchSize][];
        }

        for (int i = 0; i < batchCount; i++)
        {
            if (_cpuFfts[i] != null) continue;
            _cpuFfts[i] = new FastFourierTransform(_fftSize);
            _cpuInputs[i] = _cpuFfts[i].InputData;
        }
    }

    private void ClearGpuTiming()
    {
        LastTimePrep = 0;
        LastTimeCopyFrom = 0;
        LastTimeShader = 0;
        LastTimeCopyTo = 0;
        LastTimePost = 0;
        ClearNativeTimings();
    }

    private void ReadNativeTimings()
    {
        if (!IsAvailable)
        {
            ClearNativeTimings();
            return;
        }

        try
        {
            int rc = NativeMethods.GetLastTimings(_nativeHandle, out double pack, out double upload, out double dispatch, out double readback);
            if (rc == 0)
            {
                LastTimePack = pack;
                LastTimeUploadNative = upload;
                LastTimeDispatch = dispatch;
                LastTimeReadback = readback;
                return;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException || ex is BadImageFormatException)
        {
        }

        ClearNativeTimings();
    }

    private void ClearNativeTimings()
    {
        LastTimePack = 0;
        LastTimeUploadNative = 0;
        LastTimeDispatch = 0;
        LastTimeReadback = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_nativeHandle != IntPtr.Zero)
        {
            try { NativeMethods.Destroy(_nativeHandle); } catch { }
        }
        GC.SuppressFinalize(this);
    }
}
