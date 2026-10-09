using System.Diagnostics;
using SRdeck.DSP;

namespace SRdeck.Models.SDR;

public partial class FftProcessor
{
    private bool ProcessGpuSpectrum(
        RadioControl control, int requestedWidth, int mode, int fftSize, int fftSizeB,
        HanningWindow window, long submissionTag, out long completedTag, out bool inputAccepted,
        ref float[] spectrum, ref float[] waterfall, ref float[] waterfallAverage,
        ref float[] noise, out bool supported)
    {
        lock (_gpuLock)
        {
            var runner = GetGpuRunner(mode, 1, window);
            var request = CreateSpectrumRequest(control, requestedWidth, fftSize);
            runner.ConfigureCalibration(1, 0, request, allowMeasurement: false);
            _gpuInputOffsets[0] = 0;
            bool fresh = runner.ProcessSpectrumPacked(_packedRingI, _packedRingQ, _gpuInputOffsets,
                GetFftBias(fftSize), request, submissionTag, out completedTag, out inputAccepted,
                ref spectrum, ref noise, out FftPowerSummary? power);
            supported = runner.SupportsSpectrumAggregation;
            if (!supported) return false;
            LastGpuPrep = runner.LastTimePrep;
            LastGpuUpload = runner.LastTimeCopyFrom;
            LastGpuShader = runner.LastTimeShader;
            LastGpuDownload = runner.LastTimeCopyTo;
            LastGpuPost = runner.LastTimePost;
            LastGpuPack = runner.LastTimePack;
            LastGpuUploadNative = runner.LastTimeUploadNative;
            LastGpuDispatch = runner.LastTimeDispatch;
            LastGpuReadback = runner.LastTimeReadback;
            LastCpuPrep = LastCpuPost = 0;
            if (!fresh) return false;
            LastPowerSummary = power;
            var stopwatch = Stopwatch.StartNew();
            if (waterfall.Length != spectrum.Length) waterfall = new float[spectrum.Length];
            if (waterfallAverage.Length != spectrum.Length) waterfallAverage = new float[spectrum.Length];
            Array.Copy(spectrum, waterfall, spectrum.Length);
            for (int i = 0; i < spectrum.Length; i++) waterfallAverage[i] += spectrum[i];
            LastAggregate = stopwatch.Elapsed.TotalMilliseconds;
            LastCpuPost = LastAggregate;
            return true;
        }
    }

    private static GpuSpectrumRequest CreateSpectrumRequest(RadioControl control, int requestedWidth, int fftSize)
    {
        int fsHz = control.FsHz > 0 ? control.FsHz : (int)AppConstants.FULL_BW;
        var (start, end, center) = SpectrumStatisticsCalculator.GetPowerBins(fftSize, control, fsHz);
        return new GpuSpectrumRequest
        {
            SpectrumWidth = GetAggregatedDisplayWidth(requestedWidth, fftSize, control.MainSpanHz, fsHz),
            NoiseWidth = GetNoiseFloorWidth(requestedWidth, fftSize, control.BaseMainSpanHz, fsHz),
            StartBin = start, EndBin = end, CenterBin = center,
            SampleRateHz = fsHz, FrequencyOffsetHz = control.FreqOffsetHz, SpanHz = control.SpanHz
        };
    }
}
