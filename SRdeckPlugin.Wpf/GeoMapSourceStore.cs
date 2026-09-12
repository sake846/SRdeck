using System.IO;
using System.Text.Json;

namespace SRdeckPlugin.Wpf;

public enum GeoMapSourceMode
{
    Auto,
    Online,
    Offline
}

public sealed record GeoMapSourceOptions(
    GeoMapSourceMode Mode = GeoMapSourceMode.Auto,
    string? MbTilesPath = null,
    // Kept in the persisted schema for compatibility with older settings files.
    // Auto mode now selects the source from the current connectivity state.
    bool AllowOnlineFallback = true);

public static class GeoMapSourceStore
{
    private static readonly object Gate = new();
    private static GeoMapSourceOptions? cached;

    public static event EventHandler? Changed;

    public static string GetSettingsFilePath(string? localAppData = null)
    {
        string root = localAppData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "SRdeck", "maps", "settings.json");
    }

    public static GeoMapSourceOptions GetOptions(string? localAppData = null)
    {
        lock (Gate)
        {
            if (localAppData is null && cached is not null) return cached;
            GeoMapSourceOptions loaded = Load(GetSettingsFilePath(localAppData));
            if (localAppData is null) cached = loaded;
            return loaded;
        }
    }

    public static bool TrySaveOptions(GeoMapSourceOptions options, string? localAppData = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsValid(options)) return false;
        string path = GetSettingsFilePath(localAppData);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(options, JsonOptions));
            File.Move(temporaryPath, path, true);
            lock (Gate)
            {
                if (localAppData is null) cached = options;
            }
            if (localAppData is null) Changed?.Invoke(null, EventArgs.Empty);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsValid(GeoMapSourceOptions options) =>
        Enum.IsDefined(options.Mode) &&
        (string.IsNullOrWhiteSpace(options.MbTilesPath) || Path.IsPathFullyQualified(options.MbTilesPath));

    public static bool HasUsableOfflineMap(GeoMapSourceOptions options) =>
        !string.IsNullOrWhiteSpace(options.MbTilesPath) && File.Exists(options.MbTilesPath);

    public static bool ShouldUseOfflineMap(GeoMapSourceOptions options, bool isOnline)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Mode == GeoMapSourceMode.Offline ||
            options.Mode == GeoMapSourceMode.Auto && !isOnline;
    }

    private static GeoMapSourceOptions Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            GeoMapSourceOptions? options = JsonSerializer.Deserialize<GeoMapSourceOptions>(
                File.ReadAllText(path), JsonOptions);
            return options is not null && IsValid(options) ? options : new();
        }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
        catch (JsonException) { return new(); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
