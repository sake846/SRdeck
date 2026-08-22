namespace SRdeck.Models;

public enum SdrDeviceKind
{
    SdrPlay,
    RtlSdr
}

public readonly record struct SdrDeviceCapabilities(SdrDeviceKind Kind)
{
    public bool IsRtlSdr => Kind == SdrDeviceKind.RtlSdr;
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
