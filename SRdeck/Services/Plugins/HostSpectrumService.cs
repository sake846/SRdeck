using SRdeck.Models;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

internal sealed class HostSpectrumService(Func<IRadioRenderContext> renderContextFactory) : IPluginSpectrumService
{
    private readonly object gate = new();
    private SnapshotKey? cachedKey;
    private PluginSpectrumSnapshot? cached;

    public PluginSpectrumSnapshot? GetSnapshot(PluginSpectrumRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        lock (gate)
        {
            // The engine also owns the plugin dispatcher, so resolve it after construction.
            IRadioRenderContext engine = renderContextFactory();
            if (!engine.HasValidMainFftData) return null;
            using MainFftFrameLease lease = engine.AcquireMainFftFrame();
            MainFftFrame frame = lease.Frame;
            if (frame.FrameId <= 0 || frame.SampleRateHz <= 0 || frame.SpectrumData.Length == 0) return null;
            RadioControl control = engine.Control;
            double inputLower = frame.CenterFrequencyHz - frame.SampleRateHz / 2.0;
            double inputUpper = inputLower + frame.SampleRateHz;
            double lowerHz = inputLower, upperHz = inputUpper;
            if (request.Region == PluginSpectrumRegion.DisplayBand)
            {
                int span = control.MainSpanHz > 0 ? control.MainSpanHz : frame.SampleRateHz;
                lowerHz = control.CenterFreqHz - span / 2.0;
                upperHz = lowerHz + span;
            }
            else if (request.Region == PluginSpectrumRegion.FrequencyRange)
            {
                lowerHz = request.LowerFrequencyHz!.Value;
                upperHz = request.UpperFrequencyHz!.Value;
            }
            float systemDb = float.IsFinite(control.SystemDb) ? control.SystemDb : 0;
            float rfCalibration = engine.RfCalibrationOffset;
            if (!float.IsFinite(rfCalibration)) return null;
            var key = new SnapshotKey(frame.FrameId, frame.SampleRateHz, frame.CenterFrequencyHz,
                lowerHz, upperHz, request.BinCount, request.Aggregation, systemDb, rfCalibration);
            if (cachedKey == key) return cached;

            var levels = new float[request.BinCount];
            double sourceBinsPerHz = frame.SpectrumData.Length / (double)frame.SampleRateHz;
            double outputBinWidth = (upperHz - lowerHz) / request.BinCount;
            for (int column = 0; column < levels.Length; column++)
            {
                double lower = Math.Max(inputLower, lowerHz + column * outputBinWidth);
                double upper = Math.Min(inputUpper, lowerHz + (column + 1) * outputBinWidth);
                if (upper <= lower) { levels[column] = float.NaN; continue; }
                int start = (int)Math.Clamp(Math.Floor((lower - inputLower) * sourceBinsPerHz), 0, frame.SpectrumData.Length - 1);
                int end = (int)Math.Clamp(Math.Ceiling((upper - inputLower) * sourceBinsPerHz), start + 1, frame.SpectrumData.Length);
                float peak = float.NaN;
                int count = 0;
                for (int bin = start; bin < end; bin++)
                    if (float.IsFinite(frame.SpectrumData[bin]))
                    {
                        peak = !float.IsFinite(peak) ? frame.SpectrumData[bin] : Math.Max(peak, frame.SpectrumData[bin]);
                        count++;
                    }
                double value = peak;
                if (request.Aggregation == PluginSpectrumAggregation.MeanPower && count > 0)
                {
                    double sum = 0;
                    for (int bin = start; bin < end; bin++)
                        if (float.IsFinite(frame.SpectrumData[bin]))
                            sum += Math.Pow(10, (frame.SpectrumData[bin] - peak) / 10.0);
                    value += 10 * Math.Log10(sum / count);
                }
                levels[column] = (float)value - systemDb + rfCalibration;
            }
            cachedKey = key;
            cached = new(frame.FrameId, frame.TimestampUtc, frame.SampleRateHz, frame.CenterFrequencyHz,
                lowerHz, upperHz, frame.SpectrumData.Length, request.Aggregation, levels)
            { CalibrationOffsetDb = rfCalibration - systemDb };
            return cached;
        }
    }

    private readonly record struct SnapshotKey(long FrameId, int SampleRateHz, long InputCenterHz,
        double LowerHz, double UpperHz, int BinCount, PluginSpectrumAggregation Aggregation, float SystemDb, float RfCalibration);
}
