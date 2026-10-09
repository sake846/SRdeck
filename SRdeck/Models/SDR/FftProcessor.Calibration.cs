using SRdeck.DSP;

namespace SRdeck.Models.SDR;

public partial class FftProcessor
{
    private GpuFftRunner GetGpuRunner(int mode, int batch, HanningWindow window)
    {
        var runner = _gpuFfts[mode];
        if (runner is null || runner.MaxBatchSize != batch)
        {
            runner?.Dispose();
            runner = _gpuFfts[mode] = new GpuFftRunner(_fftSizes[mode], _fftSizeBs[mode], batch, window.hData);
        }
        return runner;
    }

    // WarmUp is called before reception. Normal frames only look up saved profiles.
    internal void CalibrateGpuBeforeStart(RadioControl control, int requestedWidth)
    {
        if (!control.IsGpuFftEnabled) return;
        int mode = control.FftResolutionMode;
        if (mode < 0 || mode >= MAX_RESOLUTION_MODES) mode = 0;
        int batch = Math.Clamp(control.FftBatchCount <= 0 ? 10 : control.FftBatchCount, 1, MAX_BATCH_COUNT);
        int step = batch == 1 ? 0 : control.FsHz / 10 / batch;
        GpuSpectrumRequest? request = batch == 1 ? CreateSpectrumRequest(control, requestedWidth, _fftSizes[mode]) : null;
        lock (_gpuLock)
        {
            ClearPoolsExcept(mode);
            var window = _hamsPool[mode][0] ??= new HanningWindow(_fftSizes[mode]);
            bool reused = _gpuFfts[mode] is { } previous && previous.MaxBatchSize == batch;
            var runner = GetGpuRunner(mode, batch, window);
            var report = runner.ConfigureCalibration(batch, step, request, allowMeasurement: false);
            if (report.Source == FftCalibrationSource.Pending)
            {
                // A stopped session may retain tagged readbacks. Measure on a fresh context.
                if (reused)
                {
                    runner.Dispose();
                    _gpuFfts[mode] = null;
                    runner = GetGpuRunner(mode, batch, window);
                }
                report = runner.ConfigureCalibration(batch, step, request, allowMeasurement: true);
            }
            Console.WriteLine(report.Entry is { } entry
                ? $"FFT calibration ({report.Source}): {_fftSizes[mode]:N0} x {batch}, profile {entry.Profile}, " +
                  $"median {entry.MedianMs:F2} ms, p95 {entry.P95Ms:F2} ms, CPU {entry.CpuMedianMs:F2} ms, GPU {entry.GpuMedianMs:F2} ms"
                : $"FFT calibration: {report.Source}; using the reference path");
        }
    }
}
