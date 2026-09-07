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

}
