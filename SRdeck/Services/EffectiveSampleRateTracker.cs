using System;

namespace SRdeck.Services;

public interface IEffectiveSampleRateTracker
{
    double SampleRateHz { get; }
    void AddSamples(uint sampleCount);
    void Reset();
}

public sealed class EffectiveSampleRateTracker : IEffectiveSampleRateTracker
{
    private const double MeasurementWindowSeconds = 0.5;
    private const double PreviousMeasurementWeight = 0.8;

    private readonly TimeProvider _timeProvider;
    private long _windowSamples;
    private long _windowStartTimestamp;
    private bool _hasActiveWindow;

    public EffectiveSampleRateTracker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public double SampleRateHz { get; private set; }

    public void AddSamples(uint sampleCount)
    {
        long nowTimestamp = _timeProvider.GetTimestamp();
        if (!_hasActiveWindow)
        {
            _windowStartTimestamp = nowTimestamp;
            _windowSamples = sampleCount;
            _hasActiveWindow = true;
            return;
        }

        _windowSamples += sampleCount;
        double elapsedSeconds = _timeProvider
            .GetElapsedTime(_windowStartTimestamp, nowTimestamp)
            .TotalSeconds;
        if (elapsedSeconds < MeasurementWindowSeconds)
        {
            return;
        }

        double measuredSampleRateHz = _windowSamples / elapsedSeconds;
        SampleRateHz = SampleRateHz <= 1.0
            ? measuredSampleRateHz
            : SampleRateHz * PreviousMeasurementWeight
                + measuredSampleRateHz * (1.0 - PreviousMeasurementWeight);

        _windowSamples = 0;
        _windowStartTimestamp = nowTimestamp;
    }

    public void Reset()
    {
        _windowSamples = 0;
        _windowStartTimestamp = 0;
        _hasActiveWindow = false;
        SampleRateHz = 0.0;
    }
}
