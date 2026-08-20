using System;

namespace SRdeck.Models.SDR;

/// <summary>
/// Describes the optional controls exposed by each SDRplay hardware family.
/// The SDRplay API identifies hardware by model, while the UI needs a stable,
/// model-oriented description of the controls it may show.
/// </summary>
public readonly record struct SdrPlayDeviceFeatures(
    bool SupportsBiasT,
    int AntennaCount,
    bool SupportsAmPort,
    bool SupportsExternalReferenceOutput,
    bool SupportsHdr)
{
    public bool SupportsAntennaSelection => AntennaCount > 1;
}

public static class SdrPlayDeviceFeaturePolicy
{
    public static SdrPlayDeviceFeatures GetFeatures(string? modelName)
    {
        if (string.Equals(modelName, "RSPdx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelName, "RSPdxR2", StringComparison.OrdinalIgnoreCase))
        {
            return new(SupportsBiasT: true, AntennaCount: 3, SupportsAmPort: false,
                SupportsExternalReferenceOutput: false, SupportsHdr: true);
        }

        if (string.Equals(modelName, "RSP2", StringComparison.OrdinalIgnoreCase))
        {
            return new(SupportsBiasT: true, AntennaCount: 2, SupportsAmPort: true,
                SupportsExternalReferenceOutput: true, SupportsHdr: false);
        }

        if (string.Equals(modelName, "RSPduo", StringComparison.OrdinalIgnoreCase))
        {
            return new(SupportsBiasT: true, AntennaCount: 0, SupportsAmPort: true,
                SupportsExternalReferenceOutput: true, SupportsHdr: false);
        }

        if (string.Equals(modelName, "RSP1A", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modelName, "RSP1B", StringComparison.OrdinalIgnoreCase))
        {
            return new(SupportsBiasT: true, AntennaCount: 0, SupportsAmPort: false,
                SupportsExternalReferenceOutput: false, SupportsHdr: false);
        }

        return default;
    }
}
