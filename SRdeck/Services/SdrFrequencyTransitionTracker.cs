namespace SRdeck.Services;

public interface ISdrFrequencyTransitionTracker
{
    int ActiveCenterFrequencyHz { get; }
    void TrackRequestedFrequency(long centerFrequencyHz, long switchDelaySamples);
    void Advance(long sampleCount);
}

public sealed class SdrFrequencyTransitionTracker : ISdrFrequencyTransitionTracker
{
    private int _pendingCenterFrequencyHz;
    private long _remainingDelaySamples;
    private long _lastRequestedCenterFrequencyHz;

    public int ActiveCenterFrequencyHz { get; private set; }

    public void TrackRequestedFrequency(long centerFrequencyHz, long switchDelaySamples)
    {
        if (_lastRequestedCenterFrequencyHz != 0 && centerFrequencyHz != _lastRequestedCenterFrequencyHz)
        {
            _pendingCenterFrequencyHz = (int)centerFrequencyHz;
            _remainingDelaySamples = switchDelaySamples;
            if (_remainingDelaySamples <= 0)
            {
                _remainingDelaySamples = 0;
                ActiveCenterFrequencyHz = _pendingCenterFrequencyHz;
            }
        }

        _lastRequestedCenterFrequencyHz = centerFrequencyHz;
        if (ActiveCenterFrequencyHz == 0)
        {
            ActiveCenterFrequencyHz = (int)centerFrequencyHz;
        }
    }

    public void Advance(long sampleCount)
    {
        if (_remainingDelaySamples <= 0)
        {
            return;
        }

        _remainingDelaySamples -= sampleCount;
        if (_remainingDelaySamples <= 0)
        {
            _remainingDelaySamples = 0;
            ActiveCenterFrequencyHz = _pendingCenterFrequencyHz;
        }
    }
}
