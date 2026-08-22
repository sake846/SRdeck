using System;
using SRdeck.Models;

namespace SRdeck.Services;

public readonly record struct SignalInputDiagnosticsSnapshot(
    int GainReductionDb,
    IqSampleExtrema Extrema,
    float RxRssi,
    double EffectiveSampleRateHz);

public readonly record struct SdrStreamingDiagnosticsSnapshot(
    int QueuedSampleBlockCount,
    long CallbackCount,
    long DroppedCallbackCount,
    double LastCallbackAgeSeconds,
    int LastCallbackLengthBytes,
    long UnexpectedCallbackLengthCount);

public readonly record struct ProcessingCycleDiagnosticsSnapshot(
    int AudioBufferedBytes,
    int BufferWritePointer,
    int BufferReadPointer,
    int BufferSize,
    SdrStreamingDiagnosticsSnapshot? Streaming);

public interface IRadioDiagnosticsCollector
{
    void ApplySignalInput(
        ref RadioDiagnostics diagnostics,
        SignalInputDiagnosticsSnapshot snapshot);

    void ApplyProcessingCycle(
        ref RadioDiagnostics diagnostics,
        ProcessingCycleDiagnosticsSnapshot snapshot);
}

public sealed class RadioDiagnosticsCollector : IRadioDiagnosticsCollector
{
    public void ApplySignalInput(
        ref RadioDiagnostics diagnostics,
        SignalInputDiagnosticsSnapshot snapshot)
    {
        diagnostics.GainReductionDb = snapshot.GainReductionDb;
        diagnostics.BufferIMaxValue = snapshot.Extrema.MaxI;
        diagnostics.BufferIMinValue = snapshot.Extrema.MinI;
        diagnostics.BufferQMaxValue = snapshot.Extrema.MaxQ;
        diagnostics.BufferQMinValue = snapshot.Extrema.MinQ;
        diagnostics.RxRssi = snapshot.RxRssi;
        diagnostics.EffectiveSampleRateHz = snapshot.EffectiveSampleRateHz;
    }

    public void ApplyProcessingCycle(
        ref RadioDiagnostics diagnostics,
        ProcessingCycleDiagnosticsSnapshot snapshot)
    {
        diagnostics.BufferWPtr = snapshot.BufferWritePointer;
        diagnostics.BufferRPtr = snapshot.BufferReadPointer;
        int bufferSize = Math.Max(1, snapshot.BufferSize);
        diagnostics.BufferPtrDiff = (int)(
            ((long)snapshot.BufferWritePointer - snapshot.BufferReadPointer + bufferSize)
            % bufferSize);

        if (snapshot.Streaming is { } streaming)
        {
            diagnostics.SdrQueuedSampleBlockCount = streaming.QueuedSampleBlockCount;
            diagnostics.SdrCallbackCount = streaming.CallbackCount;
            diagnostics.SdrDroppedCallbackCount = streaming.DroppedCallbackCount;
            diagnostics.SdrLastCallbackAgeSeconds = streaming.LastCallbackAgeSeconds;
            diagnostics.SdrLastCallbackLengthBytes = streaming.LastCallbackLengthBytes;
            diagnostics.SdrUnexpectedCallbackLengthCount = streaming.UnexpectedCallbackLengthCount;
        }
    }
}
