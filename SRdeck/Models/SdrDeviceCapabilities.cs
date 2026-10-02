namespace SRdeck.Models;

public enum SdrDeviceKind
{
    SdrPlay,
    RtlSdr,
    HackRf,
    Rx888
}

public readonly record struct SdrDeviceCapabilities(SdrDeviceKind Kind)
{
    public bool IsRtlSdr => Kind == SdrDeviceKind.RtlSdr;
    public bool IsHackRf => Kind == SdrDeviceKind.HackRf;
    public bool IsRx888 => Kind == SdrDeviceKind.Rx888;
    public bool UsesRx888FrequencyModel => IsRx888;
    public bool UsesRtlDemodulationLayout => IsRtlSdr;
}

public interface ISdrStreamingDiagnostics
{
    int QueuedSampleBlockCount { get; }
    long CallbackCount { get; }
    long DroppedCallbackCount { get; }
    double LastCallbackAgeSeconds { get; }
    int LastCallbackLengthBytes => 0;
    long UnexpectedCallbackLengthCount => 0;
}

public readonly record struct SdrStreamTimingSnapshot(
    long CallbackElapsedTicks,
    long CallbackCount,
    long SampleDeliveryElapsedTicks,
    long SampleDeliveryCount,
    long QueueWaitElapsedTicks,
    long QueueWaitCount,
    long QueueWaitMaxTicks);

/// <summary>Optional, capture-scoped timings for SDR callback and sample delivery work.</summary>
public interface ISdrStreamTimingDiagnostics
{
    void StartTimingCapture();
    SdrStreamTimingSnapshot TakeTimingSnapshot();
    void StopTimingCapture();
}
