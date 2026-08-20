using SRdeck.Configuration;
using SRdeck.Models;
#if ENABLE_RTLSDR
using System;
#endif

namespace SRdeck.SDR;

public static class SdrDeviceFactory
{
    public static bool TryOpenPreferred(out ISdrDevice? device)
    {
        device = TryOpen(new SdrController(suppressErrors: true));
        if (device != null)
        {
            return true;
        }

#if ENABLE_RTLSDR
        device = TryOpen(new RtlSdrController(suppressErrors: true));
        if (device != null)
        {
            return true;
        }
#endif
        return false;
    }

    private static ISdrDevice? TryOpen(ISdrDevice candidate)
    {
        try
        {
            if (candidate.Open())
            {
                if (candidate is SdrController sdrPlay) sdrPlay.SuppressErrors = false;
#if ENABLE_RTLSDR
                if (candidate is RtlSdrController rtlSdr) rtlSdr.SuppressErrors = false;
#endif
                return candidate;
            }
        }
        catch (Exception)
        {
            // A missing vendor API or an unavailable device means that the next
            // supported device family should be tried.
        }

        candidate.Dispose();
        return null;
    }

    public static ISdrDevice Create(SdrDeviceType deviceType)
    {
        return deviceType switch
        {
#if ENABLE_RTLSDR
            SdrDeviceType.RtlSdr => new RtlSdrController(),
#endif
            SdrDeviceType.SdrPlay => new SdrController(),
            SdrDeviceType.Auto => CreateAuto(),
            _ => CreateAuto()
        };
    }

    private static ISdrDevice CreateAuto()
    {
        // Auto detection always gives SDRplay priority. Actual availability is
        // determined by TryOpenPreferred when the user presses Detect.
        return new SdrController();
    }
}
