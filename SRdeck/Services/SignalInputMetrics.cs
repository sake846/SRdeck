using System;

namespace SRdeck.Services;

public interface ISignalInputMetrics
{
    IqSampleExtrema CurrentExtrema { get; }
    IqSampleExtrema LastCompletedExtrema { get; }
    double EffectiveSampleRateHz { get; }

    void ExtendExtrema(ReadOnlySpan<short> samplesI, ReadOnlySpan<short> samplesQ);
    void TrackSamples(uint sampleCount);
    void CompleteBlock();
    void ResetCurrentExtrema();
    void ResetSampleRate();
}

public sealed class SignalInputMetrics : ISignalInputMetrics
{
    private readonly IIqSampleExtremaCalculator _extremaCalculator;
    private readonly IEffectiveSampleRateTracker _sampleRateTracker;

    public SignalInputMetrics(
        IIqSampleExtremaCalculator extremaCalculator,
        IEffectiveSampleRateTracker sampleRateTracker)
    {
        _extremaCalculator = extremaCalculator;
        _sampleRateTracker = sampleRateTracker;
    }

    public IqSampleExtrema CurrentExtrema { get; private set; }
    public IqSampleExtrema LastCompletedExtrema { get; private set; }
    public double EffectiveSampleRateHz => _sampleRateTracker.SampleRateHz;

    public void ExtendExtrema(ReadOnlySpan<short> samplesI, ReadOnlySpan<short> samplesQ)
    {
        CurrentExtrema = _extremaCalculator.Extend(CurrentExtrema, samplesI, samplesQ);
    }

    public void TrackSamples(uint sampleCount) => _sampleRateTracker.AddSamples(sampleCount);

    public void CompleteBlock()
    {
        LastCompletedExtrema = CurrentExtrema;
        CurrentExtrema = default;
    }

    public void ResetCurrentExtrema() => CurrentExtrema = default;

    public void ResetSampleRate() => _sampleRateTracker.Reset();
}
