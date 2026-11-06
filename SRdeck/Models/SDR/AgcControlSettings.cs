using SRdeck.Models;
using SRdeck.Models.Configuration;

namespace SRdeck.Models.SDR;

public sealed record AgcControlSettings(
    double AttackSeconds,
    double ReleaseSeconds,
    double HoldSeconds,
    int UpperThreshold,
    int LowerThreshold)
{
    public static AgcControlSettings Default { get; } =
        AgcSensitivityProfile.CreateDefaults().Single(profile => profile.Mode == AgcReleaseMode.Slow).ControlSettings;

    public static AgcControlSettings From(AgcSensitivityProfile profile)
    {
        int upper = ToAmplitude(profile.UpperThresholdDbfs);
        int lower = Math.Min(ToAmplitude(profile.LowerThresholdDbfs), upper - 1);
        return new(
            NormalizeSeconds(profile.AttackSeconds),
            NormalizeSeconds(profile.ReleaseSeconds),
            NormalizeSeconds(profile.HoldSeconds),
            upper,
            Math.Max(1, lower));
    }

    private static int ToAmplitude(double dbfs) =>
        Math.Clamp((int)Math.Round(32768 * Math.Pow(10, Math.Clamp(dbfs, -80, 0) / 20)), 2, short.MaxValue);

    private static double NormalizeSeconds(double seconds) =>
        double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 3600) : 0;
}
