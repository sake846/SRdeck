namespace SRdeck.Models.SDR;

internal static class SdrStreamLivenessPolicy
{
    public static bool ShouldReportStall(
        bool isStreaming,
        bool isStopping,
        bool alreadyReported,
        long lastCallbackTimestamp,
        long currentTimestamp,
        long timeoutTicks) =>
        isStreaming &&
        !isStopping &&
        !alreadyReported &&
        lastCallbackTimestamp > 0 &&
        currentTimestamp - lastCallbackTimestamp >= timeoutTicks;
}
