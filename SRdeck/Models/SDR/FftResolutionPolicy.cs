namespace SRdeck.Models.SDR;

public static class FftResolutionPolicy
{
    private static readonly int[] FftSizes =
        [4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152, 4194304];

    public const int ModeCount = 11;

    public static int GetFftSize(int mode) => FftSizes[Math.Clamp(mode, 0, FftSizes.Length - 1)];

    public static int GetMaximumMode(int sampleRateHz, int batchCount, bool isGpuEnabled)
    {
        int safeSampleRateHz = Math.Max(1, sampleRateHz);
        int safeBatchCount = batchCount <= 0 ? 10 : Math.Clamp(batchCount, 1, 32);
        double maximumFftPoints = 0.3 * safeSampleRateHz / safeBatchCount;
        int maximumMode = 0;
        for (int index = 0; index < FftSizes.Length; index++)
        {
            if (FftSizes[index] <= maximumFftPoints)
                maximumMode = index;
        }

        // CPU FFTs above 32K can monopolize the processing worker even when
        // the sample-rate window would otherwise permit them.
        return isGpuEnabled ? maximumMode : Math.Min(maximumMode, 3);
    }

    public static int ClampMode(int mode, int sampleRateHz, int batchCount, bool isGpuEnabled)
    {
        int safeMode = Math.Clamp(mode, 0, FftSizes.Length - 1);
        return Math.Min(safeMode, GetMaximumMode(sampleRateHz, batchCount, isGpuEnabled));
    }
}
