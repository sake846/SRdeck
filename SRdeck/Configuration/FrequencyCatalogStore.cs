using System.Globalization;
using System.Text.Json;
using SRdeck.Models;

namespace SRdeck.Configuration;

internal sealed class FrequencyCatalogStore : ISettingsPersistenceNotifications
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly JsonSettingsFile<List<StationItem>> _stations;
    private readonly JsonSettingsFile<List<BandPlanItem>> _bandPlans;

    public FrequencyCatalogStore(string? stationsPath = null, string? bandPlansPath = null)
    {
        _stations = new(stationsPath ?? UserDataPaths.StationsPath,
            ReadOptions, WriteOptions, Report, NormalizeStations);
        _bandPlans = new(bandPlansPath ?? UserDataPaths.BandPlansPath,
            ReadOptions, WriteOptions, Report, NormalizeBandPlans);
    }

    public event Action<SettingsPersistenceIssue>? PersistenceIssue;

    public List<StationItem> LoadStations() => _stations.Load(createIfMissing: false);
    public List<BandPlanItem> LoadBandPlans() => _bandPlans.Load(createIfMissing: false);
    public void SaveStations(IEnumerable<StationItem> stations) => _stations.Save([.. stations]);
    public void SaveBandPlans(IEnumerable<BandPlanItem> bandPlans) => _bandPlans.Save([.. bandPlans]);

    private void Report(SettingsPersistenceIssue issue) => PersistenceIssue?.Invoke(issue);

    private static List<StationItem> NormalizeStations(List<StationItem> stations)
    {
        foreach (StationItem? station in stations)
        {
            if (station is null || station.FrequencyHz is < 1 or > int.MaxValue ||
                string.IsNullOrWhiteSpace(station.Name))
                throw new JsonException("A station must have a name and a frequency from 1 to 2,147,483,647 Hz.");
            station.Name = station.Name.Trim();
            station.Mode = station.Mode?.Trim() ?? "";
            station.Comment = station.Comment?.Trim() ?? "";
        }
        return stations;
    }

    private static List<BandPlanItem> NormalizeBandPlans(List<BandPlanItem> bandPlans)
    {
        foreach (BandPlanItem? band in bandPlans)
        {
            if (band is null || band.StartHz < 1 || band.EndHz > int.MaxValue ||
                band.StartHz >= band.EndHz || string.IsNullOrWhiteSpace(band.Label) ||
                band.DefaultStepHz is < 1 or > 1_000_000 || !IsColor(band.Color))
                throw new JsonException("A band must have a valid range, label, color, and tuning step.");
            band.Label = band.Label.Trim();
            band.Color = band.Color.ToUpperInvariant();
            band.Mode = band.Mode?.Trim() ?? "";
        }
        return bandPlans;
    }

    internal static bool IsColor(string? value) =>
        value is { Length: 7 or 9 } && value[0] == '#' &&
        uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
}
