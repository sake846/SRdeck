namespace SRdeck.Services;

public interface ISdrFrequencyTransitionTracker
{
    int ActiveCenterFrequencyHz { get; }
    void TrackRequestedFrequency(long centerFrequencyHz, long switchDelaySamples);
    void Advance(long sampleCount);
}

public sealed class SdrFrequencyTransitionTracker : ISdrFrequencyTransitionTracker
{
    private readonly object _sync = new();
    private int _activeCenterFrequencyHz;
    private int _pendingCenterFrequencyHz;
    private long _remainingDelaySamples;
    private long _lastRequestedCenterFrequencyHz;

    public int ActiveCenterFrequencyHz
    {
        get
        {
            lock (_sync) return _activeCenterFrequencyHz;
        }
    }

    public void TrackRequestedFrequency(long centerFrequencyHz, long switchDelaySamples)
    {
        lock (_sync)
        {
            if (_lastRequestedCenterFrequencyHz != 0 && centerFrequencyHz != _lastRequestedCenterFrequencyHz)
            {
                _pendingCenterFrequencyHz = (int)centerFrequencyHz;
                _remainingDelaySamples = switchDelaySamples;
                if (_remainingDelaySamples <= 0)
                {
                    _remainingDelaySamples = 0;
                    _activeCenterFrequencyHz = _pendingCenterFrequencyHz;
                }
            }

            _lastRequestedCenterFrequencyHz = centerFrequencyHz;
            if (_activeCenterFrequencyHz == 0)
            {
                _activeCenterFrequencyHz = (int)centerFrequencyHz;
            }
        }
    }

    public void Advance(long sampleCount)
    {
        lock (_sync)
        {
            if (_remainingDelaySamples <= 0)
            {
                return;
            }

            _remainingDelaySamples -= sampleCount;
            if (_remainingDelaySamples <= 0)
            {
                _remainingDelaySamples = 0;
                _activeCenterFrequencyHz = _pendingCenterFrequencyHz;
            }
        }
    }
}
