using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Models;
using SRdeck.Models.SDR;
using System.Text.Json.Serialization;

namespace SRdeck.Models.Configuration;

public partial class AgcSensitivityProfile : ObservableObject
{
    private const double MinimumThresholdDbfs = -80;
    private const double ThresholdSeparationDb = 0.1;
    public static IReadOnlyList<AgcReleaseMode> EditableModes { get; } =
        [AgcReleaseMode.Fast, AgcReleaseMode.Medium, AgcReleaseMode.Slow];

    private AgcReleaseMode _mode = AgcReleaseMode.Slow;
    private double _attackSeconds;
    private double _releaseSeconds = AppConstants.AGC_RELEASE_SLOW_SECONDS;
    private double _holdSeconds = AppConstants.AGC_RELEASE_SLOW_SECONDS * 10;
    private double _upperThresholdDbfs = ToDbfs(AppConstants.AGC_UPPER_THRESHOLD);
    private double _lowerThresholdDbfs = ToDbfs(AppConstants.AGC_LOWER_THRESHOLD);

    public AgcReleaseMode Mode
    {
        get => _mode;
        set => SetProperty(ref _mode, Enum.IsDefined(value) ? value : AgcReleaseMode.Slow);
    }

    public double AttackSeconds
    {
        get => _attackSeconds;
        set => SetProperty(ref _attackSeconds, NormalizeSeconds(value));
    }

    public double ReleaseSeconds
    {
        get => _releaseSeconds;
        set => SetProperty(ref _releaseSeconds, NormalizeSeconds(value));
    }

    public double HoldSeconds
    {
        get => _holdSeconds;
        set => SetProperty(ref _holdSeconds, NormalizeSeconds(value));
    }

    public double UpperThresholdDbfs
    {
        get => _upperThresholdDbfs;
        set
        {
            double normalized = NormalizeDbfs(value, ToDbfs(AppConstants.AGC_UPPER_THRESHOLD), MinimumThresholdDbfs + ThresholdSeparationDb);
            if (normalized <= LowerThresholdDbfs)
                LowerThresholdDbfs = normalized - ThresholdSeparationDb;
            SetProperty(ref _upperThresholdDbfs, normalized);
        }
    }

    public double LowerThresholdDbfs
    {
        get => _lowerThresholdDbfs;
        set
        {
            double normalized = NormalizeDbfs(value, ToDbfs(AppConstants.AGC_LOWER_THRESHOLD), MinimumThresholdDbfs);
            SetProperty(ref _lowerThresholdDbfs, Math.Min(normalized, UpperThresholdDbfs - ThresholdSeparationDb));
        }
    }

    [JsonIgnore]
    public AgcControlSettings ControlSettings => AgcControlSettings.From(this);

    public void ResetToDefaults()
    {
        AttackSeconds = 0;
        ReleaseSeconds = Mode switch
        {
            AgcReleaseMode.Fast => AppConstants.AGC_RELEASE_FAST_SECONDS,
            AgcReleaseMode.Medium => AppConstants.AGC_RELEASE_MEDIUM_SECONDS,
            _ => AppConstants.AGC_RELEASE_SLOW_SECONDS
        };
        HoldSeconds = ReleaseSeconds * 10;
        UpperThresholdDbfs = ToDbfs(AppConstants.AGC_UPPER_THRESHOLD);
        LowerThresholdDbfs = ToDbfs(AppConstants.AGC_LOWER_THRESHOLD);
    }

    public static List<AgcSensitivityProfile> CreateDefaults() =>
        EditableModes.Select(CreateDefault).ToList();

    public static AgcSensitivityProfile CreateDefault(AgcReleaseMode mode)
    {
        var profile = new AgcSensitivityProfile { Mode = mode };
        profile.ResetToDefaults();
        return profile;
    }

    private static double NormalizeSeconds(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 3600) : 0;

    private static double NormalizeDbfs(double value, double fallback, double minimum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, 0) : fallback;

    private static double ToDbfs(int amplitude) => 20 * Math.Log10(amplitude / 32768.0);
}
