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



    private bool IsRtlSdrDeviceController()
    {
#if ENABLE_RTLSDR
        return _engine?.SdrDevice?.Capabilities.IsRtlSdr == true;
#else
        return false;
#endif
    }


}
