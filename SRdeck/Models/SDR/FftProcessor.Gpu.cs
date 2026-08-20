using System;
using System.Linq;
using System.Runtime.Intrinsics;
using SRdeck.DSP;
using SRdeck.Models;

namespace SRdeck.Models.SDR;

public partial class FftProcessor
{
    public void CalculateSingleFft(IqSampleRingBuffer buffer, int referencePtr, int mode, bool isGpuEnabled, float[] outputBuffer, RadioControl control)
    {
        if (mode < 0 || mode >= MAX_RESOLUTION_MODES) mode = 0;
        int fftSize = _fftSizes[mode];
        int fftSizeB = _fftSizeBs[mode];

        var ham = _renderHams[mode] ?? (_renderHams[mode] = new HanningWindow(fftSize));

        float bias = GetFftBias(fftSize);

        lock (_gpuLock)
        {
            var gpuFft = _gpuFfts[mode];
            if (isGpuEnabled && gpuFft != null && gpuFft.IsAvailable)
            {
                ham.ApplyWindowShort(buffer, referencePtr - fftSize, _renderGpuInI, _renderGpuInQ);
                
                var inI = new float[][] { _renderGpuInI };
                var inQ = new float[][] { _renderGpuInQ };
                var outDb = new float[][] { _renderGpuOutDb };
                gpuFft.ProcessBatch(inI, inQ, outDb, bias, 1);
                
                Array.Copy(_renderGpuOutDb, outputBuffer, fftSize);
            }
            else
            {
                var fft = _renderFfts[mode] ?? (_renderFfts[mode] = new FastFourierTransform(fftSize));
                ham.ApplyWindow(buffer, referencePtr - fftSize, fft.InputData);
                fft.Execute(fftSizeB, bias);
                Array.Copy(fft.OutputData, outputBuffer, fftSize);
            }
        }
    }

    private bool ProcessGpuFft(short[] bufferI, short[] bufferQ, int referencePtr, int mode, int batchSize, int fftSize, int fftSizeB, int stepSize, HanningWindow[] hams)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_gpuLock)
        {
            if (_gpuFfts[mode] == null || _gpuFfts[mode]!.MaxBatchSize != batchSize)
            {
                for (int i = 0; i < _gpuFfts.Length; i++)
                {
                    if (_gpuFfts[i] != null) { _gpuFfts[i]?.Dispose(); _gpuFfts[i] = null; }
                }
                try
                {
                    _gpuFfts[mode] = new GpuFftRunner(fftSize, fftSizeB, batchSize, hams[0].hData);
                }
                catch
                {
                    _gpuFfts[mode] = null;
                }
            }

            var runner = _gpuFfts[mode];
            if (runner != null && runner.IsAvailable)
            {
                for (int i = 0; i < batchSize; i++)
                {
                    _gpuInputOffsets[i] = referencePtr - fftSize - i * stepSize;
                }

                float bias = GetFftBias(fftSize);
                
                LastCpuPrep = sw.Elapsed.TotalMilliseconds;

                bool ok = runner.ProcessBatchPacked(bufferI, bufferQ, _gpuInputOffsets, _gpuOutDb, bias, batchSize);
                if (!ok)
                {
                    return false;
                }

                LastGpuPrep = runner.LastTimePrep;
                LastGpuUpload = runner.LastTimeCopyFrom;
                LastGpuShader = runner.LastTimeShader;
                LastGpuDownload = runner.LastTimeCopyTo;
                LastGpuPost = runner.LastTimePost;
                LastGpuPack = runner.LastTimePack;
                LastGpuUploadNative = runner.LastTimeUploadNative;
                LastGpuDispatch = runner.LastTimeDispatch;
                LastGpuReadback = runner.LastTimeReadback;
                
                sw.Restart();
                Array.Clear(_fftOutputMovingAverageBuffer, 0, fftSize);
                
                if (batchSize == 1)
                {
                    Array.Copy(_gpuOutDb[0], _fftOutputMovingAverageBuffer, fftSize);
                }
                else
                {
                    AggregateBatchSums(_gpuOutDb, _fftOutputMovingAverageBuffer, fftSize, batchSize);
                }

                LastCpuPost = sw.Elapsed.TotalMilliseconds;
                return true;
            }
            else
            {
                return false;
            }
        }
    }

    private void EnsurePackedRingCapacity(int requiredLength)
    {
        if (_packedRingI.Length >= requiredLength) return;

        _packedRingI = new short[requiredLength];
        _packedRingQ = new short[requiredLength];
    }

    private void ReleasePackedRingBuffers()
    {
        _packedRingI = Array.Empty<short>();
        _packedRingQ = Array.Empty<short>();
    }

    private unsafe void AggregateBatchSums(FastFourierTransform[] ffts, float[] output, int length, int batchSize)
    {
        fixed (float* pOutput = output)
        {
            float* pOut = pOutput;
            
            fixed (float* pBatch0 = ffts[0].OutputData)
            {
                Buffer.MemoryCopy(pBatch0, pOut, length * sizeof(float), length * sizeof(float));
            }

            for (int b = 1; b < batchSize; b++)
            {
                fixed (float* pBatch = ffts[b].OutputData)
                {
                    float* pB = pBatch;
                    int j = 0;
                    
                    if (Vector256.IsHardwareAccelerated)
                    {
                        int limit = length - Vector256<float>.Count;
                        for (; j <= limit; j += 8)
                        {
                            var vOut = Vector256.Load(pOut + j);
                            var vIn = Vector256.Load(pB + j);
                            Vector256.Store(vOut + vIn, pOut + j);
                        }
                    }
                    else if (Vector128.IsHardwareAccelerated)
                    {
                        int limit = length - Vector128<float>.Count;
                        for (; j <= limit; j += 4)
                        {
                            var vOut = Vector128.Load(pOut + j);
                            var vIn = Vector128.Load(pB + j);
                            Vector128.Store(vOut + vIn, pOut + j);
                        }
                    }

                    for (; j < length; j++)
                    {
                        pOut[j] += pB[j];
                    }
                }
            }
        }
    }

    private unsafe void AggregateBatchSums(float[][] batchData, float[] output, int length, int batchSize)
    {
        fixed (float* pOutput = output)
        {
            float* pOut = pOutput;

            fixed (float* pBatch0 = batchData[0])
            {
                Buffer.MemoryCopy(pBatch0, pOut, length * sizeof(float), length * sizeof(float));
            }

            for (int b = 1; b < batchSize; b++)
            {
                fixed (float* pBatch = batchData[b])
                {
                    float* pB = pBatch;
                    int j = 0;
                    
                    if (Vector256.IsHardwareAccelerated)
                    {
                        int limit = length - Vector256<float>.Count;
                        for (; j <= limit; j += 8)
                        {
                            var vOut = Vector256.Load(pOut + j);
                            var vIn = Vector256.Load(pB + j);
                            Vector256.Store(vOut + vIn, pOut + j);
                        }
                    }
                    else if (Vector128.IsHardwareAccelerated)
                    {
                        int limit = length - Vector128<float>.Count;
                        for (; j <= limit; j += 4)
                        {
                            var vOut = Vector128.Load(pOut + j);
                            var vIn = Vector128.Load(pB + j);
                            Vector128.Store(vOut + vIn, pOut + j);
                        }
                    }

                    for (; j < length; j++)
                    {
                        pOut[j] += pB[j];
                    }
                }
            }
        }
    }

    internal bool IsPrepared(RadioControl control)
    {
        int mode = control.FftResolutionMode;
        if (mode < 0 || mode >= MAX_RESOLUTION_MODES) mode = 0;
        int batchSize = Math.Clamp(
            control.FftBatchCount <= 0 ? 10 : control.FftBatchCount,
            1,
            MAX_BATCH_COUNT);
        lock (_gpuLock)
        {
            if (_lastMode != mode) return false;
            if (_hamsPool[mode].Take(batchSize).Any(window => window is null))
                return false;
            if (control.IsGpuFftEnabled)
            {
                GpuFftRunner? runner = _gpuFfts[mode];
                return runner is { IsAvailable: true } &&
                    runner.MaxBatchSize == batchSize;
            }
            return _fftsPool[mode].Take(batchSize).All(fft => fft is not null);
        }
    }

    internal static int GetAggregatedDisplayWidth(
        int requestedWidth,
        int fftSize,
        int mainSpanHz,
        int fsHz)
    {
        if (fftSize <= 0) throw new ArgumentOutOfRangeException(nameof(fftSize));
        double fullBandwidth = fsHz > 0 ? fsHz : AppConstants.FULL_BW;
        double effectiveDisplayBandwidth = mainSpanHz > 0
            ? Math.Min(mainSpanHz, fullBandwidth)
            : Math.Min(AppConstants.DISPLAY_BW, fullBandwidth);
        double required = Math.Max(10, requestedWidth) * fullBandwidth /
                          Math.Max(1, effectiveDisplayBandwidth);
        int minimumWidth = Math.Clamp(
            (int)Math.Ceiling(Math.Min(required, fftSize)), 10, fftSize);

        int bucket = Math.Min(16, fftSize);
        while (bucket < minimumWidth && bucket <= fftSize / 2)
            bucket <<= 1;
        return Math.Clamp(Math.Max(bucket, minimumWidth), 10, fftSize);
    }

    private void AggregateNoiseFloorResults(
        int batchSize,
        int fftSize,
        int requestedWidth,
        ref float[] noiseFloorFftData,
        int baseMainSpanHz,
        int fsHz)
    {
        float fullBw = fsHz > 0 ? fsHz : AppConstants.FULL_BW;
        float baseDisplayBw = baseMainSpanHz > 0
            ? baseMainSpanHz
            : fullBw;
        int targetWidth = (int)Math.Ceiling(requestedWidth * fullBw / baseDisplayBw);
        targetWidth = Math.Clamp(targetWidth, 10, fftSize);

        if (noiseFloorFftData.Length != targetWidth)
        {
            noiseFloorFftData = new float[targetWidth];
        }

        float invBatchSize = 1.0f / batchSize;
        for (int i = 0; i < targetWidth; i++)
        {
            int startBin = (int)((long)i * fftSize / targetWidth);
            int endBin = (int)((long)(i + 1) * fftSize / targetWidth);
            if (endBin <= startBin) endBin = startBin + 1;
            if (endBin > fftSize) endBin = fftSize;

            float maxVal = float.MinValue;
            for (int j = startBin; j < endBin; j++)
            {
                float value = _fftOutputMovingAverageBuffer[j];
                if (value > maxVal) maxVal = value;
            }
            noiseFloorFftData[i] = maxVal * invBatchSize;
        }
    }
}
