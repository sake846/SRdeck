using System.Text.Json.Serialization;

namespace SRdeck.Models;

public sealed class BandPlanItem
{
    public long StartHz { get; set; }
    public long EndHz { get; set; }
    public string Label { get; set; } = "";
    public string Color { get; set; } = "#3C708CA0";
    public int DefaultStepHz { get; set; } = 1_000;
    public string Mode { get; set; } = "";

    [JsonIgnore]
    public string StartFrequencyLabel => $"{StartHz / 1_000_000.0:0.000} MHz";

    [JsonIgnore]
    public string EndFrequencyLabel => $"{EndHz / 1_000_000.0:0.000} MHz";

    [JsonIgnore]
    public string RangeLabel =>
        $"{StartHz / 1_000_000.0:0.000000} - {EndHz / 1_000_000.0:0.000000} MHz";
}
