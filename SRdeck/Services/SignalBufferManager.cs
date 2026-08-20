using System;
using SRdeck.DSP;
using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.Services;

public readonly record struct IqBufferCapacityResult(
    IqSampleRingBuffer Buffer,
    bool WasResized);

public interface ISignalBufferManager
{
    IqBufferCapacityResult EnsureIqBufferCapacity(
        IqSampleRingBuffer currentBuffer,
        int sampleRateHz);

    bool EnsureDemodulationCapacity(
        RadioState state,
        int sampleRateHz,
        SdrDeviceCapabilities deviceCapabilities);

    int GetMaxAvailableHistorySeconds(
        int bufferSize,
        int sampleRateHz);
}

public sealed class SignalBufferManager : ISignalBufferManager
{
    private const int AudioBufferSize = 12_800;

    public IqBufferCapacityResult EnsureIqBufferCapacity(
        IqSampleRingBuffer currentBuffer,
        int sampleRateHz)
    {
        int effectiveSampleRateHz = Math.Max(1, sampleRateHz);
        long requestedSamples = (long)effectiveSampleRateHz * AppConstants.IQ_RETENTION_SECONDS;
        if (requestedSamples > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"IQ retention requires {requestedSamples:N0} samples, which exceeds the supported ring size.");
        }

        int requiredCapacity = (int)Math.Max(1L, requestedSamples);
        return currentBuffer.Capacity == requiredCapacity
            ? new IqBufferCapacityResult(currentBuffer, false)
            : new IqBufferCapacityResult(new IqSampleRingBuffer(requiredCapacity), true);
    }

    public bool EnsureDemodulationCapacity(
        RadioState state,
        int sampleRateHz,
        SdrDeviceCapabilities deviceCapabilities)
    {
        int effectiveSampleRateHz = Math.Max(1, sampleRateHz);
        bool usesExpandedBuffer = SdrDevicePolicy.UsesExpandedDemodulationBuffer(
            deviceCapabilities,
            effectiveSampleRateHz);
        int requiredSamplesPerBlock = effectiveSampleRateHz / 10 * (usesExpandedBuffer ? 2 : 1);

        bool needsResize =
            state.BasebandIData.Length != requiredSamplesPerBlock ||
            state.BasebandQData.Length != requiredSamplesPerBlock;
        if (!needsResize)
        {
            return false;
        }

        state.BasebandIData = new int[requiredSamplesPerBlock];
        state.BasebandQData = new int[requiredSamplesPerBlock];
        return true;
    }

    public int GetMaxAvailableHistorySeconds(
        int bufferSize,
        int sampleRateHz)
    {
        int effectiveSampleRateHz = Math.Max(1, sampleRateHz);
        long retainedSeconds = bufferSize / (long)effectiveSampleRateHz;
        return (int)Math.Clamp(retainedSeconds - 1, 0, AppConstants.MAX_HISTORY_SEC);
    }
}
