using System;

namespace SRdeck.Models.SDR;

public readonly record struct SdrPlayGainSetting(
    int LnaState,
    int GainReductionDb,
    int AttenuationFromMaximumGainDb);

/// <summary>
/// Maps the normalized sensitivity control to SDRplay's LNA and baseband gain
/// reduction controls. LNA reductions are from the SDRplay API 3.15 gain tables.
/// The application currently uses the normal 50-ohm/default input paths and does
/// not enable RSPdx HDR mode, so the corresponding rows are selected here.
/// </summary>
public static class SdrPlayGainPolicy
{
    private const int NominalGainReductionDb = 50;

    private static readonly int[] Rsp1_0_420 = [0, 24, 19, 43];
    private static readonly int[] Rsp1_420_1000 = [0, 7, 19, 26];
    private static readonly int[] Rsp1_1000_2000 = [0, 5, 19, 24];

    private static readonly int[] Rsp1A_0_60 = [0, 6, 12, 18, 37, 42, 61];
    private static readonly int[] Rsp1A_60_420 = [0, 6, 12, 18, 20, 26, 32, 38, 57, 62];
    private static readonly int[] Rsp1A_420_1000 = [0, 7, 13, 19, 20, 27, 33, 39, 45, 64];
    private static readonly int[] Rsp1A_1000_2000 = [0, 6, 12, 20, 26, 32, 38, 43, 62];

    private static readonly int[] Rsp2_0_420 = [0, 10, 15, 21, 24, 34, 39, 45, 64];
    private static readonly int[] Rsp2_420_1000 = [0, 7, 10, 17, 22, 41];
    private static readonly int[] Rsp2_1000_2000 = [0, 5, 21, 15, 15, 34];

    private static readonly int[] RspDx_0_12 = [0, 3, 6, 9, 12, 15, 24, 27, 30, 33, 36, 39, 42, 45, 48, 51, 54, 57, 60];
    private static readonly int[] RspDx_12_50 = [0, 3, 6, 9, 12, 15, 18, 24, 27, 30, 33, 36, 39, 42, 45, 48, 51, 54, 57, 60];
    private static readonly int[] RspDx_50_60 = [0, 3, 6, 9, 12, 20, 23, 26, 29, 32, 35, 38, 44, 47, 50, 53, 56, 59, 62, 65, 68, 71, 74, 77, 80];
    private static readonly int[] RspDx_60_250 = [0, 3, 6, 9, 12, 15, 24, 27, 30, 33, 36, 39, 42, 45, 48, 51, 54, 57, 60, 63, 66, 69, 72, 75, 78, 81, 84];
    private static readonly int[] RspDx_250_420 = [0, 3, 6, 9, 12, 15, 18, 24, 27, 30, 33, 36, 39, 42, 45, 48, 51, 54, 57, 60, 63, 66, 69, 72, 75, 78, 81, 84];
    private static readonly int[] RspDx_420_1000 = [0, 7, 10, 13, 16, 19, 22, 25, 31, 34, 37, 40, 43, 46, 49, 52, 55, 58, 61, 64, 67];
    private static readonly int[] RspDx_1000_2000 = [0, 5, 8, 11, 14, 17, 20, 32, 35, 38, 41, 44, 47, 50, 53, 56, 59, 62, 65];

    public static int GetLnaStateCount(string? modelName, long frequencyHz) =>
        GetLnaReductions(modelName, frequencyHz).Length;

    public static int ClampLnaState(string? modelName, long frequencyHz, int lnaState)
    {
        int maxState = Math.Max(0, GetLnaStateCount(modelName, frequencyHz) - 1);
        return Math.Clamp(lnaState, 0, maxState);
    }

    public static int GetLnaReductionDb(string? modelName, long frequencyHz, int lnaState)
    {
        int[] reductions = GetLnaReductions(modelName, frequencyHz);
        return reductions[ClampLnaState(modelName, frequencyHz, lnaState)];
    }

    public static int GetAttenuationFromMaximumGainDb(
        string? modelName,
        long frequencyHz,
        int lnaState,
        int gainReductionDb,
        int minimumGainReductionDb)
    {
        int basebandReduction = Math.Max(0, gainReductionDb - Math.Max(0, minimumGainReductionDb));
        return GetLnaReductionDb(modelName, frequencyHz, lnaState) + basebandReduction;
    }

    public static SdrPlayGainSetting FromSensitivity(
        int sensitivity,
        string? modelName,
        long frequencyHz,
        int minimumGainReductionDb,
        int maximumGainReductionDb)
    {
        int maxGr = Math.Max(0, maximumGainReductionDb);
        int minGr = Math.Clamp(minimumGainReductionDb, 0, maxGr);
        int clampedSensitivity = Math.Clamp(sensitivity, 0, 100);
        int[] lnaReductions = GetLnaReductions(modelName, frequencyHz);
        int maximumLnaReduction = GetMaximum(lnaReductions);
        int maximumAttenuation = maximumLnaReduction + maxGr - minGr;
        int targetAttenuation = (int)Math.Round(
            (100 - clampedSensitivity) / 100.0 * maximumAttenuation);

        int bestState = 0;
        int bestGr = minGr;
        int bestAttenuation = 0;
        int bestError = int.MaxValue;
        int bestNominalDistance = int.MaxValue;
        int nominalGr = Math.Clamp(NominalGainReductionDb, minGr, maxGr);

        for (int state = 0; state < lnaReductions.Length; state++)
        {
            int gr = Math.Clamp(
                minGr + targetAttenuation - lnaReductions[state],
                minGr,
                maxGr);
            int attenuation = lnaReductions[state] + gr - minGr;
            int error = Math.Abs(attenuation - targetAttenuation);
            int nominalDistance = Math.Abs(gr - nominalGr);

            if (error < bestError ||
                (error == bestError && nominalDistance < bestNominalDistance))
            {
                bestState = state;
                bestGr = gr;
                bestAttenuation = attenuation;
                bestError = error;
                bestNominalDistance = nominalDistance;
            }
        }

        return new SdrPlayGainSetting(bestState, bestGr, bestAttenuation);
    }

    public static int ToSensitivity(
        int lnaState,
        int gainReductionDb,
        string? modelName,
        long frequencyHz,
        int minimumGainReductionDb,
        int maximumGainReductionDb)
    {
        int maxGr = Math.Max(0, maximumGainReductionDb);
        int minGr = Math.Clamp(minimumGainReductionDb, 0, maxGr);
        int[] lnaReductions = GetLnaReductions(modelName, frequencyHz);
        int maximumLnaReduction = GetMaximum(lnaReductions);
        int maximumAttenuation = maximumLnaReduction + maxGr - minGr;
        if (maximumAttenuation <= 0) return 50;

        int currentLnaReduction = GetLnaReductionDb(modelName, frequencyHz, lnaState);
        int currentGr = Math.Clamp(gainReductionDb, minGr, maxGr);
        int currentAttenuation = currentLnaReduction + currentGr - minGr;

        double ratio = Math.Clamp((double)currentAttenuation / maximumAttenuation, 0.0, 1.0);
        int sensitivity = (int)Math.Round(100.0 * (1.0 - ratio));
        return Math.Clamp(sensitivity, 0, 100);
    }

    private static int[] GetLnaReductions(string? modelName, long frequencyHz)
    {
        long hz = Math.Max(0, frequencyHz);
        string model = modelName ?? string.Empty;

        if (model.StartsWith("RSPdx", StringComparison.OrdinalIgnoreCase))
        {
            if (hz < 12_000_000) return RspDx_0_12;
            if (hz < 50_000_000) return RspDx_12_50;
            if (hz < 60_000_000) return RspDx_50_60;
            if (hz < 250_000_000) return RspDx_60_250;
            if (hz < 420_000_000) return RspDx_250_420;
            if (hz < 1_000_000_000) return RspDx_420_1000;
            return RspDx_1000_2000;
        }

        if (model.Equals("RSP2", StringComparison.OrdinalIgnoreCase))
        {
            if (hz < 420_000_000) return Rsp2_0_420;
            if (hz < 1_000_000_000) return Rsp2_420_1000;
            return Rsp2_1000_2000;
        }

        if (model.Equals("RSPduo", StringComparison.OrdinalIgnoreCase))
        {
            return GetRsp1AFamilyReductions(hz, splitLowBandAt50Mhz: false);
        }

        if (model.Equals("RSP1B", StringComparison.OrdinalIgnoreCase))
        {
            return GetRsp1AFamilyReductions(hz, splitLowBandAt50Mhz: true);
        }

        if (model.Equals("RSP1A", StringComparison.OrdinalIgnoreCase))
        {
            return GetRsp1AFamilyReductions(hz, splitLowBandAt50Mhz: false);
        }

        if (hz < 420_000_000) return Rsp1_0_420;
        if (hz < 1_000_000_000) return Rsp1_420_1000;
        return Rsp1_1000_2000;
    }

    private static int[] GetRsp1AFamilyReductions(long hz, bool splitLowBandAt50Mhz)
    {
        long lowBandLimit = splitLowBandAt50Mhz ? 50_000_000 : 60_000_000;
        if (hz < lowBandLimit) return Rsp1A_0_60;
        if (hz < 420_000_000) return Rsp1A_60_420;
        if (hz < 1_000_000_000) return Rsp1A_420_1000;
        return Rsp1A_1000_2000;
    }

    private static int GetMaximum(int[] values)
    {
        int maximum = values[0];
        for (int index = 1; index < values.Length; index++)
        {
            maximum = Math.Max(maximum, values[index]);
        }
        return maximum;
    }
}
