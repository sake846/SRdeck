using SRdeck.Configuration;

namespace SRdeck.ViewModels;

public partial class MainViewModel
{
    private static bool IsRtlSdrConfigured(SdrDeviceType deviceType)
    {
#if ENABLE_RTLSDR
        return deviceType == SdrDeviceType.RtlSdr;
#else
        return false;
#endif
    }

    private static bool IsRx888Configured(SdrDeviceType deviceType)
    {
#if ENABLE_RX888
        return deviceType == SdrDeviceType.Rx888Mk2;
#else
        return false;
#endif
    }

    private bool IsRx888DeviceController()
    {
#if ENABLE_RX888
        return _engine?.SdrDevice?.Capabilities.IsRx888 == true;
#else
        return false;
#endif
    }

    private bool IsRtlSdrDeviceController()
    {
#if ENABLE_RTLSDR
        return _engine?.SdrDevice?.Capabilities.IsRtlSdr == true;
#else
        return false;
#endif
    }

    private static bool IsHackRfConfigured(SdrDeviceType deviceType)
    {
#if ENABLE_HACKRF
        return deviceType == SdrDeviceType.HackRf;
#else
        return false;
#endif
    }

    private bool IsHackRfDeviceController()
    {
#if ENABLE_HACKRF
        return _engine?.SdrDevice?.Capabilities.IsHackRf == true;
#else
        return false;
#endif
    }


}
