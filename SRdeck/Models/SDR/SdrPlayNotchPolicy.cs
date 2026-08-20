using System;

namespace SRdeck.Models.SDR;

/// <summary>
/// Values used by the SDRplay broadcast-notch UI.  The RF notch is a combined
/// MW/FM filter; DAB is a separate hardware filter where the device supports it.
/// </summary>
public enum SdrPlayNotchFilterMode
{
    Off = 0,
    MwFm = 1,
    Dab = 2,
    MwFmAndDab = 3
}

public static class SdrPlayNotchPolicy
{
    public static bool SupportsBroadcastNotch(string? modelName) =>
        string.Equals(modelName, "RSP1A", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelName, "RSP1B", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelName, "RSP2", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelName, "RSPduo", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelName, "RSPdx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelName, "RSPdxR2", StringComparison.OrdinalIgnoreCase);

    public static bool SupportsDabNotch(string? modelName) =>
        string.Equals(modelName, "RSP1A", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelName, "RSP1B", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelName, "RSPduo", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelName, "RSPdx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(modelName, "RSPdxR2", StringComparison.OrdinalIgnoreCase);

    public static SdrPlayNotchFilterMode Normalize(string? modelName, int value)
    {
        SdrPlayNotchFilterMode mode = Enum.IsDefined((SdrPlayNotchFilterMode)value)
            ? (SdrPlayNotchFilterMode)value
            : SdrPlayNotchFilterMode.Off;

        bool broadcast = SupportsBroadcastNotch(modelName);
        bool dab = SupportsDabNotch(modelName);
        return mode switch
        {
            SdrPlayNotchFilterMode.MwFm when !broadcast => SdrPlayNotchFilterMode.Off,
            SdrPlayNotchFilterMode.Dab when !dab => SdrPlayNotchFilterMode.Off,
            SdrPlayNotchFilterMode.MwFm => SdrPlayNotchFilterMode.MwFm,
            SdrPlayNotchFilterMode.Dab => SdrPlayNotchFilterMode.Dab,
            SdrPlayNotchFilterMode.MwFmAndDab when broadcast && dab => mode,
            SdrPlayNotchFilterMode.MwFmAndDab when broadcast => SdrPlayNotchFilterMode.MwFm,
            _ => SdrPlayNotchFilterMode.Off
        };
    }
}
