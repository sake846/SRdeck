using System.Diagnostics;
using SRdeck.Models;

namespace SRdeck.SDR;

public partial class SdrController
{
    private int _isTimingCaptureEnabled;
    private long _callbackElapsedTicks;
    private long _callbackTimingCount;
    private long _sampleDeliveryElapsedTicks;
    private long _sampleDeliveryCount;
    private long _queueWaitElapsedTicks;
    private long _queueWaitCount;
    private long _queueWaitMaxTicks;

    public void StartTimingCapture()
    {
        Interlocked.Exchange(ref _callbackElapsedTicks, 0);
        Interlocked.Exchange(ref _callbackTimingCount, 0);
        Interlocked.Exchange(ref _sampleDeliveryElapsedTicks, 0);
        Interlocked.Exchange(ref _sampleDeliveryCount, 0);
        Interlocked.Exchange(ref _queueWaitElapsedTicks, 0);
        Interlocked.Exchange(ref _queueWaitCount, 0);
        Interlocked.Exchange(ref _queueWaitMaxTicks, 0);
        Volatile.Write(ref _isTimingCaptureEnabled, 1);
    }

    public SdrStreamTimingSnapshot TakeTimingSnapshot() => new(
        Interlocked.Exchange(ref _callbackElapsedTicks, 0),
        Interlocked.Exchange(ref _callbackTimingCount, 0),
        Interlocked.Exchange(ref _sampleDeliveryElapsedTicks, 0),
        Interlocked.Exchange(ref _sampleDeliveryCount, 0),
        Interlocked.Exchange(ref _queueWaitElapsedTicks, 0),
        Interlocked.Exchange(ref _queueWaitCount, 0),
        Interlocked.Exchange(ref _queueWaitMaxTicks, 0));

    public void StopTimingCapture() => Volatile.Write(ref _isTimingCaptureEnabled, 0);

    private bool IsTimingCaptureEnabled => Volatile.Read(ref _isTimingCaptureEnabled) != 0;

    private void RecordCallbackElapsed(long elapsedTicks)
    {
        Interlocked.Add(ref _callbackElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref _callbackTimingCount);
    }

    private void RecordSampleDeliveryElapsed(long elapsedTicks)
    {
        Interlocked.Add(ref _sampleDeliveryElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref _sampleDeliveryCount);
    }

    private void RecordQueueWait(long elapsedTicks)
    {
        Interlocked.Add(ref _queueWaitElapsedTicks, elapsedTicks);
        Interlocked.Increment(ref _queueWaitCount);

        long currentMax = Interlocked.Read(ref _queueWaitMaxTicks);
        while (elapsedTicks > currentMax)
        {
            long previous = Interlocked.CompareExchange(ref _queueWaitMaxTicks, elapsedTicks, currentMax);
            if (previous == currentMax) break;
            currentMax = previous;
        }
    }
}
