using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using SRdeck.DSP;
using SRdeck.Models;

namespace SRdeck.Models.SDR;

public interface IFftProcessor : IDisposable
{
    double LastGpuPrep { get; }
    double LastGpuUpload { get; }
    double LastGpuShader { get; }
    double LastGpuDownload { get; }
    double LastGpuPost { get; }
    double LastGpuPack { get; }
    double LastGpuUploadNative { get; }
    double LastGpuDispatch { get; }
    double LastGpuReadback { get; }
    double LastCpuPrep { get; }
    double LastCpuPost { get; }
    double LastFftCore { get; }

    double LastFullResCopy { get; }
    double LastAggregate { get; }

    void ProcessFft(
        IqSampleRingBuffer buffer,
        int referencePtr,
        RadioControl control,
        int requestedWidth,
        ref float[] spectrumFftData,
        ref float[] waterfallFftData,
        ref float[] waterfallAveragingBuffer,
        ref float[] fullResFftData,
        ref float[] noiseFloorFftData);

    void CalculateSingleFft(
        IqSampleRingBuffer buffer,
        int referencePtr,
        int mode,
        bool isGpuEnabled,
        float[] outputBuffer,
        RadioControl control);
}

public interface IFftProcessorFactory
{
    IFftProcessor Create();
}

public sealed class FftProcessorFactory : IFftProcessorFactory
{
    public IFftProcessor Create() => new FftProcessor();
}

/// <summary>
/// FFT解析（CPU/GPU）および集約処理を担当するプロセッサです。
/// </summary>
public partial class FftProcessor : IFftProcessor
{
    private readonly HanningWindow[][] _hamsPool = new HanningWindow[MAX_RESOLUTION_MODES][];
    private readonly FastFourierTransform[][] _fftsPool = new FastFourierTransform[MAX_RESOLUTION_MODES][];
    private readonly GpuFftRunner?[] _gpuFfts = new GpuFftRunner[MAX_RESOLUTION_MODES];
    private int _lastMode = -1;
    private readonly object _gpuLock = new();
    
    // 基準となるバイアス値（4096点モード時の基準値）。ハードウェアの設定値とは別に数学的に固定します。
    public const float BASE_FFT_BIAS = -165.5f;

    // 定数定義
    private const int MAX_RESOLUTION_MODES = 11;
    private const int MAX_FFT_SIZE = 4194304;
    private const float REFERENCE_FFT_SIZE = 4096f;
    private const float DB_SCALE = 20.0f;

    // バッチ処理の最大数。UI設定（MainViewModel）の上限値に合わせて32に制限し、メモリ消費を最適化します。
    private const int MAX_BATCH_COUNT = 32;

    private readonly float[][] _gpuInI = new float[MAX_BATCH_COUNT][];
    private readonly float[][] _gpuInQ = new float[MAX_BATCH_COUNT][];
    private readonly float[][] _gpuOutDb = new float[MAX_BATCH_COUNT][];
    private readonly int[] _gpuInputOffsets = new int[MAX_BATCH_COUNT];
    
    // レンダリング（拡大画面等）専用のバッファとプロセッサ
    private readonly HanningWindow[] _renderHams = new HanningWindow[MAX_RESOLUTION_MODES];
    private readonly FastFourierTransform[] _renderFfts = new FastFourierTransform[MAX_RESOLUTION_MODES];
    private readonly float[] _renderGpuInI = new float[MAX_FFT_SIZE];
    private readonly float[] _renderGpuInQ = new float[MAX_FFT_SIZE];
    private readonly float[] _renderGpuOutDb = new float[MAX_FFT_SIZE];
    
    private readonly float[] _fftOutputMovingAverageBuffer = new float[MAX_FFT_SIZE];

    private float[] _renderShiftBuffer = Array.Empty<float>();
    private short[] _packedRingI = Array.Empty<short>();
    private short[] _packedRingQ = Array.Empty<short>();
    
    private readonly int[] _fftSizes = { 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152, 4194304 };
    private readonly int[] _fftSizeBs = { 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22 };

    // 直近の診断結果
    public double LastGpuPrep { get; private set; }
    public double LastGpuUpload { get; private set; }
    public double LastGpuShader { get; private set; }
    public double LastGpuDownload { get; private set; }
    public double LastGpuPost { get; private set; }
    public double LastGpuPack { get; private set; }
    public double LastGpuUploadNative { get; private set; }
    public double LastGpuDispatch { get; private set; }
    public double LastGpuReadback { get; private set; }
    public double LastCpuPrep { get; private set; }
    public double LastCpuPost { get; private set; }
    public double LastFftCore { get; private set; }

    public double LastFullResCopy { get; private set; }
    public double LastAggregate { get; private set; }

    public FftProcessor()
    {
        for (int i = 0; i < MAX_RESOLUTION_MODES; i++)
        {
            _hamsPool[i] = new HanningWindow[MAX_BATCH_COUNT];
            _fftsPool[i] = new FastFourierTransform[MAX_BATCH_COUNT];
        }
    }

    private float GetFftBias(int fftSize) => BASE_FFT_BIAS - DB_SCALE * MathF.Log10((float)fftSize / REFERENCE_FFT_SIZE);

    public void ProcessFft(
        IqSampleRingBuffer buffer,
        int referencePtr, 
        RadioControl control, 
        int requestedWidth, 
        ref float[] spectrumFftData, 
        ref float[] waterfallFftData,
        ref float[] waterfallAveragingBuffer,
        ref float[] fullResFftData,
        ref float[] noiseFloorFftData)
    {
        int mode = control.FftResolutionMode;
        if (mode < 0 || mode >= MAX_RESOLUTION_MODES) mode = 0;

        lock (_gpuLock)
        {
            if (mode != _lastMode)
            {
                ClearPoolsExcept(mode);
                _lastMode = mode;
            }
        }

        int batchSize = control.FftBatchCount;
        if (batchSize <= 0) batchSize = 10;
        if (batchSize > MAX_BATCH_COUNT) batchSize = MAX_BATCH_COUNT;
        int stepSize = (int)(control.FsHz / 10 / batchSize);

        int fftSize = _fftSizes[mode];
        int fftSizeB = _fftSizeBs[mode];

        var hams = _hamsPool[mode];
        var ffts = _fftsPool[mode];
        LastFftCore = 0;

        LastFullResCopy = 0;
        LastAggregate = 0;
        LastGpuPack = 0;
        LastGpuUploadNative = 0;
        LastGpuDispatch = 0;
        LastGpuReadback = 0;

        try
        {
            for (int i = 0; i < batchSize; i++)
            {
                if (hams[i] == null) hams[i] = new HanningWindow(fftSize);
                if (!control.IsGpuFftEnabled && ffts[i] == null) ffts[i] = new FastFourierTransform(fftSize);

                if (_gpuOutDb[i] == null || _gpuOutDb[i].Length < fftSize)
                {
                    _gpuOutDb[i] = new float[fftSize];
                }
            }
        }
        catch (OutOfMemoryException)
        {
            ClearPoolsExcept(-1);
            LastFftCore = 0;
            return;
        }
        
        if (control.IsGpuFftEnabled)
        {
            var swCore = Stopwatch.StartNew();
            int packedLength = fftSize + Math.Max(0, batchSize - 1) * stepSize;
            try
            {
                EnsurePackedRingCapacity(packedLength);
                buffer.CopyTo(referencePtr - packedLength, _packedRingI, _packedRingQ, 0, packedLength);
            }
            catch (OutOfMemoryException)
            {
                ReleasePackedRingBuffers();
                LastFftCore = swCore.Elapsed.TotalMilliseconds;
                return;
            }

            bool hasFreshFrame = ProcessGpuFft(_packedRingI, _packedRingQ, packedLength, mode, batchSize, fftSize, fftSizeB, stepSize, hams);
            LastFftCore = swCore.Elapsed.TotalMilliseconds;
            if (!hasFreshFrame)
            {
                return;
            }
        }
        else
        {
            Array.Clear(_fftOutputMovingAverageBuffer, 0, fftSize);
            var swCore = Stopwatch.StartNew();
            ProcessCpuFft(buffer, referencePtr, batchSize, fftSize, fftSizeB, stepSize, hams, ffts);
            LastFftCore = swCore.Elapsed.TotalMilliseconds;
        }

        AggregateNoiseFloorResults(
            batchSize,
            fftSize,
            requestedWidth,
            ref noiseFloorFftData,
            control.BaseMainSpanHz,
            control.FsHz);

        var swFullRes = Stopwatch.StartNew();
        if (fullResFftData.Length != fftSize)
        {
            try
            {
                fullResFftData = new float[fftSize];
            }
            catch (OutOfMemoryException)
            {
                ClearPoolsExcept(-1);
                LastFullResCopy = swFullRes.Elapsed.TotalMilliseconds;
                return;
            }
        }
        float invBatchSize = 1.0f / (float)batchSize;
        for (int i = 0; i < fftSize; i++)
        {
            fullResFftData[i] = _fftOutputMovingAverageBuffer[i] * invBatchSize;
        }
        LastFullResCopy = swFullRes.Elapsed.TotalMilliseconds;

        var swAggregate = Stopwatch.StartNew();
        AggregateResults(batchSize, fftSize, requestedWidth, ref spectrumFftData, ref waterfallFftData, ref waterfallAveragingBuffer, control.MainSpanHz, control.FsHz);
        LastAggregate = swAggregate.Elapsed.TotalMilliseconds;

        LastCpuPost += LastFullResCopy + LastAggregate;
    }

    private void ProcessCpuFft(short[] bufferI, short[] bufferQ, int referencePtr, int batchSize, int fftSize, int fftSizeB, int stepSize, HanningWindow[] hams, FastFourierTransform[] ffts)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        LastGpuPrep = 0; LastGpuUpload = 0; LastGpuShader = 0; LastGpuDownload = 0; LastGpuPost = 0;
        LastGpuPack = 0; LastGpuUploadNative = 0; LastGpuDispatch = 0; LastGpuReadback = 0;

        float bias = GetFftBias(fftSize);

        if (batchSize == 1)
        {
            hams[0].ApplyWindow(bufferI, bufferQ, referencePtr - fftSize, ffts[0].InputData);
            ffts[0].Execute(fftSizeB, bias);
        }
        else
        {
            Parallel.For(0, batchSize, k =>
            {
                hams[k].ApplyWindow(bufferI, bufferQ, referencePtr - fftSize - k * stepSize, ffts[k].InputData);
                ffts[k].Execute(fftSizeB, bias);
            });
        }

        LastCpuPrep = sw.Elapsed.TotalMilliseconds;
        
        sw.Restart();

        if (batchSize == 1)
        {
            Array.Copy(ffts[0].OutputData, _fftOutputMovingAverageBuffer, fftSize);
        }
        else
        {
            AggregateBatchSums(ffts, _fftOutputMovingAverageBuffer, fftSize, batchSize);
        }

        LastCpuPost = sw.Elapsed.TotalMilliseconds;
    }

    private void ProcessCpuFft(IqSampleRingBuffer buffer, int referencePtr, int batchSize, int fftSize, int fftSizeB, int stepSize, HanningWindow[] hams, FastFourierTransform[] ffts)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        LastGpuPrep = 0; LastGpuUpload = 0; LastGpuShader = 0; LastGpuDownload = 0; LastGpuPost = 0;
        LastGpuPack = 0; LastGpuUploadNative = 0; LastGpuDispatch = 0; LastGpuReadback = 0;

        float bias = GetFftBias(fftSize);

        if (batchSize == 1)
        {
            hams[0].ApplyWindow(buffer, referencePtr - fftSize, ffts[0].InputData);
            ffts[0].Execute(fftSizeB, bias);
        }
        else
        {
            Parallel.For(0, batchSize, k =>
            {
                hams[k].ApplyWindow(buffer, referencePtr - fftSize - k * stepSize, ffts[k].InputData);
                ffts[k].Execute(fftSizeB, bias);
            });
        }

        LastCpuPrep = sw.Elapsed.TotalMilliseconds;

        sw.Restart();

        if (batchSize == 1)
        {
            Array.Copy(ffts[0].OutputData, _fftOutputMovingAverageBuffer, fftSize);
        }
        else
        {
            AggregateBatchSums(ffts, _fftOutputMovingAverageBuffer, fftSize, batchSize);
        }

        LastCpuPost = sw.Elapsed.TotalMilliseconds;
    }

    private void AggregateResults(int batchSize, int fftSize, int requestedWidth, ref float[] spectrumFftData, ref float[] waterfallFftData, ref float[] waterfallAveragingBuffer, int mainSpanHz, int fsHz)
    {
        int targetWidth = GetAggregatedDisplayWidth(
            requestedWidth, fftSize, mainSpanHz, fsHz);

        if (spectrumFftData.Length != targetWidth)
        {
            spectrumFftData = new float[targetWidth];
            waterfallFftData = new float[targetWidth];
            waterfallAveragingBuffer = new float[targetWidth];
        }

        float invBatchSize = 1.0f / (float)batchSize;
        for (int i = 0; i < targetWidth; i++)
        {
            int startBin = (int)((long)i * fftSize / targetWidth);
            int endBin = (int)((long)(i + 1) * fftSize / targetWidth);
            if (endBin <= startBin) endBin = startBin + 1;
            if (endBin > fftSize) endBin = fftSize;

            float maxVal = float.MinValue;
            for (int j = startBin; j < endBin; j++)
            {
                float val = _fftOutputMovingAverageBuffer[j];
                if (val > maxVal) maxVal = val;
            }
            float scaledMax = maxVal * invBatchSize;
            spectrumFftData[i] = scaledMax;
            waterfallAveragingBuffer[i] += scaledMax;
        }

        for (int n = 0; n < waterfallFftData.Length; n++)
        {
            waterfallFftData[n] = spectrumFftData[n];
        }
    }

    private void ClearPoolsExcept(int currentMode)
    {
        lock (_gpuLock)
        {
            for (int i = 0; i < MAX_RESOLUTION_MODES; i++)
            {
                if (i == currentMode) continue;

                if (_hamsPool[i] != null)
                {
                    for (int j = 0; j < _hamsPool[i].Length; j++) _hamsPool[i][j] = null!;
                }
                if (_fftsPool[i] != null)
                {
                    for (int j = 0; j < _fftsPool[i].Length; j++) _fftsPool[i][j] = null!;
                }

                if (_gpuFfts[i] != null)
                {
                    _gpuFfts[i]?.Dispose();
                    _gpuFfts[i] = null;
                }
            }

            for (int i = 0; i < _gpuInI.Length; i++)
            {
                _gpuInI[i] = null!;
                _gpuInQ[i] = null!;
                _gpuOutDb[i] = null!;
            }
        }
    }

    public void Dispose()
    {
        lock (_gpuLock)
        {
            for (int i = 0; i < _gpuFfts.Length; i++)
            {
                _gpuFfts[i]?.Dispose();
            }
        }
    }
}
