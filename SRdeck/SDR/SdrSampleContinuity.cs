using SRdeck.Models;

namespace SRdeck.SDR;

/// <summary>Used on the producer thread before a callback can be dropped.</summary>
internal sealed class SdrSampleClock
{
    private long _sequence;
    private long _nextSample;
    private uint? _nextDeviceSample;

    public SdrSampleMetadata Capture(int count, uint? firstDeviceSample = null, bool reset = false)
    {
        bool discontinuity = reset;
        if (!reset && firstDeviceSample is uint first && _nextDeviceSample is uint expected && first != expected)
        {
            discontinuity = true;
            uint gap = unchecked(first - expected);
            // A normal uint wrap is contiguous. Backward jumps are resets,
            // not billions of lost samples.
            if (gap <= int.MaxValue) _nextSample += gap;
        }

        var metadata = new SdrSampleMetadata(++_sequence, _nextSample, discontinuity);
        _nextSample += Math.Max(0, count);
        _nextDeviceSample = firstDeviceSample is uint position
            ? unchecked(position + (uint)Math.Max(0, count)) : null;
        return metadata;
    }
}

/// <summary>
/// Owned by one dispatcher run. Attaches a loss to the first block after the
/// gap, never to older blocks still waiting in the queue.
/// </summary>
internal sealed class SdrSampleDeliveryTracker
{
    private long _nextSample;
    private long _sequence;
    private bool _failedDelivery;

    public SdrSampleMetadata Observe(SdrSampleMetadata captured, uint count)
    {
        long missing = Math.Max(0, captured.AbsoluteSampleStart - _nextSample);
        SdrSampleMetadata delivered = captured with
        {
            DroppedSamplesBefore = missing,
            IsDiscontinuous = captured.IsDiscontinuous || _failedDelivery || missing > 0 ||
                captured.AbsoluteSampleStart != _nextSample || captured.Sequence != _sequence + 1
        };
        _nextSample = captured.AbsoluteSampleStart + count;
        _sequence = captured.Sequence;
        _failedDelivery = false;
        return delivered;
    }

    public void MarkFailedDelivery() => _failedDelivery = true;
}
