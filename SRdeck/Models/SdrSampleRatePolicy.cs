
namespace SRdeck.Models;

internal static class SdrSampleRatePolicy
{
    private static readonly IReadOnlyList<int> SdrPlayRates =
        Array.AsReadOnly(new[] { 1_600_000, 2_000_000, 4_000_000, 6_000_000, 8_000_000, 10_000_000 });
    private static readonly IReadOnlyList<int> RtlSdrRates =
        Array.AsReadOnly(new[] { 2_000_000, 2_400_000 });
    private static readonly IReadOnlyList<int> HackRfRates =
        Array.AsReadOnly(new[] { 8_000_000, 10_000_000, 16_000_000, 20_000_000 });
    private static readonly IReadOnlyList<int> Rx888Rates =
        Array.AsReadOnly(new[] { 8_000_000, 32_000_000, 64_000_000 });

    public static IReadOnlyList<int> GetSupportedRates(SdrDeviceKind kind) => kind switch
    {
        SdrDeviceKind.RtlSdr => RtlSdrRates,
        SdrDeviceKind.HackRf => HackRfRates,
        SdrDeviceKind.Rx888 => Rx888Rates,
        _ => SdrPlayRates
    };

    public static IReadOnlyList<int> GetSupportedRates(bool isRtlDevice, bool isRx888Device = false) =>
        GetSupportedRates(isRx888Device
            ? SdrDeviceKind.Rx888
            : isRtlDevice ? SdrDeviceKind.RtlSdr : SdrDeviceKind.SdrPlay);

    public static int Normalize(int sampleRateHz, SdrDeviceKind kind)
    {
        IReadOnlyList<int> rates = GetSupportedRates(kind);
        int fallback = kind switch
        {
            SdrDeviceKind.RtlSdr => 2_000_000,
            SdrDeviceKind.HackRf => 8_000_000,
            SdrDeviceKind.Rx888 => 8_000_000,
            _ => 2_000_000
        };
        return rates.Contains(sampleRateHz) ? sampleRateHz : fallback;
    }

    public static int Normalize(int sampleRateHz, bool isRtlDevice, bool isRx888Device = false) =>
        Normalize(sampleRateHz, isRx888Device
            ? SdrDeviceKind.Rx888
            : isRtlDevice ? SdrDeviceKind.RtlSdr : SdrDeviceKind.SdrPlay);

    public static int GetMainSpanHz(int sampleRateHz, bool isRtlDevice, bool isRx888Device = false) =>
        isRtlDevice || isRx888Device ? sampleRateHz : (int)(sampleRateHz * 0.875);
}
