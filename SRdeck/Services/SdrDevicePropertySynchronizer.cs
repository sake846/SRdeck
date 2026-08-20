using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.Services;

public readonly record struct SdrDevicePropertyValues(
    int SampleRateHz,
    long InputCenterFrequencyHz,
    int GainReductionDb,
    float PpmAdjustment,
    float BiasPpm);

public interface ISdrDevicePropertySynchronizer
{
    long Synchronize(ISdrDevice device, RadioControl control, SdrDevicePropertyValues values);
}

public sealed class SdrDevicePropertySynchronizer : ISdrDevicePropertySynchronizer
{
    public long Synchronize(ISdrDevice device, RadioControl control, SdrDevicePropertyValues values)
    {
        long centerFrequencyHz = SdrDevicePolicy.ResolveHardwareCenterFrequency(
            control,
            values.InputCenterFrequencyHz,
            device.Capabilities);

        device.FsHz = values.SampleRateHz;
        device.CenterFreqHz = centerFrequencyHz;
        device.RfGainDb = values.GainReductionDb;
        // Application AGC is implemented on the host. Hardware AGC must remain disabled.
        device.RfAgcEnabled = false;
        device.PpmAdjustment = values.PpmAdjustment;
        device.BiasPpm = values.BiasPpm;

        return centerFrequencyHz;
    }
}
