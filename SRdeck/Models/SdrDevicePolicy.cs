using System;

namespace SRdeck.Models;

internal static class SdrDevicePolicy
{
    public static RadioControl ConstrainControl(
        RadioControl radioControl,
        SdrDeviceCapabilities capabilities,
        int fallbackSampleRateHz)
    {
        return radioControl;
    }

    public static long ResolveHardwareCenterFrequency(
        RadioControl radioControl,
        long logicalCenterFrequencyHz,
        SdrDeviceCapabilities capabilities)
    {
        return logicalCenterFrequencyHz;
    }

    public static int ResolveActiveInputCenterFrequency(
        int trackedCenterFrequencyHz,
        int configuredCenterFrequencyHz) =>
        trackedCenterFrequencyHz > 0
            ? trackedCenterFrequencyHz
            : configuredCenterFrequencyHz;

    public static bool UsesExpandedDemodulationBuffer(
        SdrDeviceCapabilities capabilities,
        int sampleRateHz) =>
        capabilities.UsesRtlDemodulationLayout && sampleRateHz == 2_000_000;
}
