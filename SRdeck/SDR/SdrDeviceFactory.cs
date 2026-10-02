using SRdeck.Configuration;
using SRdeck.Models;
#if ENABLE_RTLSDR
#endif

namespace SRdeck.SDR;

public static class SdrDeviceFactory
{
    public static bool TryOpenPreferred(out ISdrDevice? device)
        => TryOpenPreferred(SdrDeviceType.Auto, out device);

    public static bool TryOpenPreferred(SdrDeviceType deviceType, out ISdrDevice? device)
    {
        if (deviceType != SdrDeviceType.Auto)
        {
            ISdrDevice? candidate = CreateForProbe(deviceType);
            device = candidate is null ? null : TryOpen(candidate);
            return device is not null;
        }

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
#if ENABLE_RX888
        device = TryOpen(new Rx888Mk2Controller(suppressErrors: true));
        if (device != null)
        {
            return true;
        }
#endif
        return false;
    }

    public static bool TryOpen(SdrDeviceType deviceType, out ISdrDevice? device)
    {
        if (deviceType == SdrDeviceType.Auto) return TryOpenPreferred(out device);
        device = TryOpen(Create(deviceType));
        return device != null;
    }

    private static ISdrDevice? CreateForProbe(SdrDeviceType deviceType) =>
        deviceType switch
        {
#if ENABLE_RTLSDR
            SdrDeviceType.RtlSdr => new RtlSdrController(suppressErrors: true),
#endif
#if ENABLE_HACKRF
            SdrDeviceType.HackRf => new HackRfController(suppressErrors: true),
#endif
#if ENABLE_RX888
            SdrDeviceType.Rx888Mk2 => new Rx888Mk2Controller(suppressErrors: true),
#endif
            SdrDeviceType.SdrPlay => new SdrController(suppressErrors: true),
            _ => null
        };

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
#if ENABLE_HACKRF
                if (candidate is HackRfController hackRf) hackRf.SuppressErrors = false;
#endif
#if ENABLE_RX888
                if (candidate is Rx888Mk2Controller rx888) rx888.SuppressErrors = false;
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
#if ENABLE_HACKRF
            SdrDeviceType.HackRf => new HackRfController(),
#endif
#if ENABLE_RX888
            SdrDeviceType.Rx888Mk2 => new Rx888Mk2Controller(),
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
