using System.Numerics;

namespace SRdeck.Models;

internal readonly record struct SpectrumStatisticsOptions(
    int FallbackSampleRateHz,
    int RequestedSpectrumWidth,
    float RfCalibrationOffset);

internal static class SpectrumStatisticsCalculator
{
    public static void Update(
        ref RadioState radioState,
        float[]? spectrum,
        float[]? noiseFloorSpectrum,
        RadioControl control,
        SpectrumStatisticsOptions options)
    {
        if (spectrum == null || spectrum.Length == 0) return;

        radioState.MaxDb = CalculateMaxPower(spectrum);
        // RX888 shifts the display FFT when the main view is panned while zoomed and
        // fills the uncovered edge with an artificial low value. NoiseFloorFftData is
        // captured before that shift, so those display-only bins cannot lower SmMinPwr.
        float[] noiseSpectrum = noiseFloorSpectrum is { Length: > 0 } ? noiseFloorSpectrum : spectrum;
        radioState.MinFftPwr = CalculateNoiseFloor(
            noiseSpectrum,
            control,
            options,
            out radioState.MinFftScanMinHz,
            out radioState.MinFftScanMaxHz) - (float)control.SystemDb + options.RfCalibrationOffset;
        SyncRssi(ref radioState, spectrum, control, options);
    }

    private static float CalculateMaxPower(float[] spectrum)
    {
        float maxPower = AppConstants.MIN_RSSI_DB;
        int index = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int vectorSize = Vector<float>.Count;
            Vector<float> maxVector = new(maxPower);
            for (; index <= spectrum.Length - vectorSize; index += vectorSize)
            {
                var tempVector = new Vector<float>(spectrum, index);
                maxVector = Vector.Max(maxVector, tempVector);
            }
            for (int vectorElementIndex = 0; vectorElementIndex < vectorSize; vectorElementIndex++)
            {
                if (maxVector[vectorElementIndex] > maxPower) maxPower = maxVector[vectorElementIndex];
            }
        }
        for (; index < spectrum.Length; index++)
        {
            if (maxPower < spectrum[index]) maxPower = spectrum[index];
        }
        return maxPower;
    }

    private static float CalculateNoiseFloor(
        float[] spectrum,
        RadioControl control,
        SpectrumStatisticsOptions options,
        out long scanMinHz,
        out long scanMaxHz)
    {
        int dataLength = spectrum.Length;
        int sampleRateHz = control.FsHz > 0 ? control.FsHz : options.FallbackSampleRateHz;
        if (sampleRateHz <= 0) sampleRateHz = (int)AppConstants.FULL_BW;

        // Keep the detection window equal to the least-zoomed main view. MainSpanHz
        // changes on every wheel zoom, while BaseMainSpanHz remains the selected SPN.
        int scanSpanHz = control.BaseMainSpanHz > 0
            ? Math.Min(control.BaseMainSpanHz, sampleRateHz)
            : sampleRateHz;
        int displaySpanHz = control.BaseMainSpanHz > 0
            ? control.BaseMainSpanHz
            : sampleRateHz;
        double marginRatio = AppConstants.FFT_SCAN_MARGIN_PERCENT / 100.0;
        double baseStartHz = (sampleRateHz - scanSpanHz) / 2.0;
        double scanStartHz = baseStartHz + scanSpanHz * marginRatio;
        double scanEndHz = baseStartHz + scanSpanHz * (1.0 - marginRatio);
        int noiseScanStart = (int)Math.Floor(scanStartHz * dataLength / sampleRateHz);
        int noiseScanEnd = (int)Math.Ceiling(scanEndHz * dataLength / sampleRateHz);



        noiseScanStart = Math.Clamp(noiseScanStart, 0, Math.Max(0, dataLength - 1));
        noiseScanEnd = Math.Clamp(noiseScanEnd, noiseScanStart + 1, dataLength);

        // Match the bin density of the least-zoomed display. This remains fixed while
        // MainSpanHz changes and uses the same max aggregation as the displayed FFT.
        int baseViewBinCount = (int)Math.Ceiling((double)Math.Max(10, options.RequestedSpectrumWidth) * sampleRateHz / displaySpanHz);
        int statisticsBins = Math.Min(dataLength, Math.Max(10, baseViewBinCount));
        int statisticsStart = Math.Clamp(
            (int)Math.Ceiling((double)noiseScanStart * statisticsBins / dataLength),
            0,
            statisticsBins - 1);
        int statisticsEnd = Math.Clamp(
            (int)Math.Floor((double)noiseScanEnd * statisticsBins / dataLength),
            statisticsStart + 1,
            statisticsBins);

        // Report the exact FFT-bin edges that the loop below will inspect, after all
        // device-band and aggregation alignment adjustments have been applied.
        int actualSourceStart = (int)((long)statisticsStart * dataLength / statisticsBins);
        int actualSourceEnd = (int)((long)statisticsEnd * dataLength / statisticsBins);
        double fftBandStartHz = control.CenterFreqHz - sampleRateHz / 2.0;
        scanMinHz = (long)Math.Round(fftBandStartHz + (double)actualSourceStart * sampleRateHz / dataLength);
        scanMaxHz = (long)Math.Round(fftBandStartHz + (double)actualSourceEnd * sampleRateHz / dataLength);

        float minPower = 0f;
        for (int index = statisticsStart; index < statisticsEnd; index++)
        {
            int sourceStart = (int)((long)index * dataLength / statisticsBins);
            int sourceEnd = (int)((long)(index + 1) * dataLength / statisticsBins);
            if (sourceEnd <= sourceStart) sourceEnd = sourceStart + 1;

            float groupMax = float.MinValue;
            for (int innerIndex = sourceStart; innerIndex < sourceEnd; innerIndex++)
            {
                if (float.IsFinite(spectrum[innerIndex]) && spectrum[innerIndex] > groupMax) groupMax = spectrum[innerIndex];
            }
            if (groupMax != float.MinValue && groupMax < minPower) minPower = groupMax;
        }
        return minPower;
    }

    private static void SyncRssi(
        ref RadioState radioState,
        float[] spectrum,
        RadioControl control,
        SpectrumStatisticsOptions options)
    {
        bool initializeNoiseFloor = !float.IsFinite(radioState.Min2FftPwr)
            || radioState.Min2FftPwr == AppConstants.MIN_RSSI_DB;
        int dataLength = spectrum.Length;
        int sampleRateHz = control.FsHz > 0 ? control.FsHz : options.FallbackSampleRateHz;
        if (sampleRateHz <= 0) sampleRateHz = (int)AppConstants.FULL_BW;

        double binWidthHz = Math.Max(1.0, (double)sampleRateHz / dataLength);
        int centerBin = dataLength / 2;
        int tunedBinIndex = centerBin + (int)(control.FreqOffsetHz / binWidthHz);
        int halfSpan = (int)((control.SpanHz / binWidthHz) / 2);
        double linearSum = 0;
        int binCount = 0;
        const double Ln10 = 2.302585092994046;
        for (int binIndex = tunedBinIndex - halfSpan; binIndex <= tunedBinIndex + halfSpan; binIndex++)
        {
            if (binIndex >= 0 && binIndex < dataLength)
            {
                linearSum += Math.Exp(spectrum[binIndex] * 0.1 * Ln10);
                binCount++;
            }
        }
        if (binCount > 0)
        {
            float powerDbFs = 10.0f * MathF.Log10((float)linearSum);
            radioState.RfCalibrationDelta = -(float)control.SystemDb + options.RfCalibrationOffset;
            radioState.AveRxPwr = powerDbFs + radioState.RfCalibrationDelta;
            float centerValDb = (tunedBinIndex >= 0 && tunedBinIndex < dataLength) ? spectrum[tunedBinIndex] : AppConstants.MIN_RSSI_DB;
            radioState.AveFftPwr = centerValDb - (float)control.SystemDb + options.RfCalibrationOffset;
            radioState.AveDb = powerDbFs - 10.0f * MathF.Log10((float)binCount);
            radioState.Ave2Db = radioState.AveDb * AppConstants.RSSI_EMA_ALPHA + radioState.Ave2Db * (1.0f - AppConstants.RSSI_EMA_ALPHA);
            radioState.Min2FftPwr = initializeNoiseFloor
                ? radioState.MinFftPwr
                : radioState.MinFftPwr * AppConstants.RSSI_EMA_ALPHA + radioState.Min2FftPwr * (1.0f - AppConstants.RSSI_EMA_ALPHA);
        }
        else
        {
            // Keep smoothing alive even when current tuned bin is temporarily out of the visible FFT range.
            radioState.Min2FftPwr = initializeNoiseFloor
                ? radioState.MinFftPwr
                : radioState.MinFftPwr * AppConstants.RSSI_EMA_ALPHA + radioState.Min2FftPwr * (1.0f - AppConstants.RSSI_EMA_ALPHA);
            radioState.AveDb = radioState.MinFftPwr;
            radioState.Ave2Db = radioState.AveDb * AppConstants.RSSI_EMA_ALPHA + radioState.Ave2Db * (1.0f - AppConstants.RSSI_EMA_ALPHA);
        }
    }
}
