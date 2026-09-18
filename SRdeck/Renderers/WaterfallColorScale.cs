using SRdeck.Models;

namespace SRdeck.Renderers;

/// <summary>
/// Shared intensity mapping used by the waterfall and its spectrum legend.
/// </summary>
internal static class WaterfallColorScale
{
    private const float FallbackNoiseFloorDb = -120.0f;

    internal static float ResolveNoiseFloor(RadioState radioState)
    {
        float noiseFloor = radioState.Min2FftPwr;
        return (!float.IsFinite(noiseFloor)
                || noiseFloor == AppConstants.MIN_RSSI_DB
                || noiseFloor == 0.0f)
            ? FallbackNoiseFloorDb
            : noiseFloor;
    }

    internal static int GetColorIndex(float physicalLevelDbm, float noiseFloorDb, int biasDb = 0)
    {
        return Math.Clamp((int)((physicalLevelDbm - noiseFloorDb) * 4.0f + biasDb), 0, 255);
    }
}
