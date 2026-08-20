namespace SRdeck.Configuration;

public static class BuildFeatures
{
#if ENABLE_RTLSDR
    public const bool RtlSdr = true;
#else
    public const bool RtlSdr = false;
#endif

#if ENABLE_RX888
    public const bool Rx888 = true;
#else
    public const bool Rx888 = false;
#endif
}
