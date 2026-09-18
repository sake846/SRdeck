
namespace SRdeck.Models;

internal static class SdrSampleRatePolicy
{
    private static readonly IReadOnlyList<int> SdrPlayRates =
        Array.AsReadOnly(new[] { 1_600_000, 2_000_000, 4_000_000, 6_000_000, 8_000_000, 10_000_000 });
    private static readonly IReadOnlyList<int> RtlSdrRates =
        Array.AsReadOnly(new[] { 2_000_000, 2_400_000 });

    public static IReadOnlyList<int> GetSupportedRates(bool isRtlDevice) =>
        isRtlDevice ? RtlSdrRates : SdrPlayRates;

    public static int Normalize(int sampleRateHz, bool isRtlDevice) =>
        GetSupportedRates(isRtlDevice).Contains(sampleRateHz) ? sampleRateHz : 2_000_000;

    public static int GetMainSpanHz(int sampleRateHz, bool isRtlDevice) =>
        isRtlDevice ? sampleRateHz : (int)(sampleRateHz * 0.875);
}
