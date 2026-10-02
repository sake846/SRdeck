
namespace SRdeck.Models;

internal static class SdrDevicePolicy
{
    public static RadioControl ConstrainControl(
        RadioControl radioControl,
        SdrDeviceCapabilities capabilities,
        int fallbackSampleRateHz)
    {
        if (!capabilities.UsesRx888FrequencyModel) return radioControl;

        int sampleRateHz = radioControl.FsHz > 0 ? radioControl.FsHz : fallbackSampleRateHz;
        if (sampleRateHz < 32_000_000) return radioControl;

        radioControl.CenterFreqHz = radioControl.MainSpanHz >= sampleRateHz
            ? sampleRateHz / 2
            : Math.Clamp(radioControl.CenterFreqHz, 0, sampleRateHz);
        radioControl.FreqOffsetHz = radioControl.TunedFreqHz - radioControl.CenterFreqHz;
        radioControl.ApplyPrimaryReceiverTuning();
        return radioControl;
    }

    public static long ResolveHardwareCenterFrequency(
        RadioControl radioControl,
        long logicalCenterFrequencyHz,
        SdrDeviceCapabilities capabilities)
    {
        return capabilities.UsesRx888FrequencyModel && radioControl.FsHz >= 32_000_000
            ? radioControl.FsHz / 2
            : logicalCenterFrequencyHz;
    }

    public static int ResolveActiveInputCenterFrequency(
        int trackedCenterFrequencyHz,
        int configuredCenterFrequencyHz) =>
        trackedCenterFrequencyHz > 0
            ? trackedCenterFrequencyHz
            : configuredCenterFrequencyHz;

}
