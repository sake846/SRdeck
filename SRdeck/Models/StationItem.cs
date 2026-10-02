using System.Text.Json.Serialization;

namespace SRdeck.Models;

public sealed class StationItem
{
    public long FrequencyHz { get; set; }
    public string Mode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Comment { get; set; } = "";

    [JsonIgnore]
    public string FrequencyLabel => $"{FrequencyHz / 1_000_000.0:0.000} MHz";
}
