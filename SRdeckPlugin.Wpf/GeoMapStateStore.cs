using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SRdeckPlugin.Wpf;

public sealed record GeoMapState(double Latitude, double Longitude, double Zoom);

public static class GeoMapStateStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, GeoMapState> MemoryCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly GeoMapState DefaultJapanState = new(36.2048, 138.2529, 5.0);

    public static string GetPluginDataDirectory(string mapId)
    {
        string safeMapId = NormalizeMapId(mapId);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string baseDir = string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(AppContext.BaseDirectory, "SRdeck", "plugins")
            : Path.Combine(appData, "SRdeck", "plugins");

        string fullBaseDir = Path.GetFullPath(baseDir);
        string candidate = Path.GetFullPath(Path.Combine(fullBaseDir, safeMapId));
        if (!candidate.StartsWith(fullBaseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Map ID resolves outside the plugin data directory.", nameof(mapId));
        }
        return candidate;
    }

    public static string GetStateFilePath(string mapId) =>
        Path.Combine(GetPluginDataDirectory(mapId), "settings.json");

    public static GeoMapState GetState(string mapId)
    {
        string key = NormalizeMapId(mapId);
        lock (Gate)
        {
            if (MemoryCache.TryGetValue(key, out GeoMapState? cached) && IsValidState(cached))
            {
                return cached;
            }

            GeoMapState state = LoadFromFile(key);
            MemoryCache[key] = state;
            return state;
        }
    }

    public static void SaveState(string mapId, GeoMapState state)
    {
        if (!IsValidState(state)) return;
        string key = NormalizeMapId(mapId);
        lock (Gate)
        {
            MemoryCache[key] = state;
            SaveToFile(key, state);
        }
    }

    public static bool IsValidState(GeoMapState state) =>
        state is not null &&
        double.IsFinite(state.Latitude) && state.Latitude is >= -90.0 and <= 90.0 &&
        double.IsFinite(state.Longitude) && state.Longitude is >= -180.0 and <= 180.0 &&
        double.IsFinite(state.Zoom) && state.Zoom is >= 1.0 and <= 20.0;

    private static string NormalizeMapId(string mapId)
    {
        string value = string.IsNullOrWhiteSpace(mapId) ? "default_map" : mapId.Trim();
        if (value.Length > 64 || value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
        {
            throw new ArgumentException("Map ID may contain only ASCII letters, digits, dot, underscore, and hyphen.", nameof(mapId));
        }
        return value;
    }

    private static GeoMapState LoadFromFile(string mapId)
    {
        try
        {
            string filePath = GetStateFilePath(mapId);
            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                JsonElement mapStateElement = default;
                bool found = false;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("Settings", out JsonElement settingsElement) &&
                        settingsElement.ValueKind == JsonValueKind.Object &&
                        settingsElement.TryGetProperty("MapState", out mapStateElement))
                    {
                        found = true;
                    }
                    else if (root.TryGetProperty("MapState", out mapStateElement))
                    {
                        found = true;
                    }
                }

                if (found && mapStateElement.ValueKind == JsonValueKind.Object)
                {
                    GeoMapState? loaded = JsonSerializer.Deserialize<GeoMapState>(mapStateElement.GetRawText());
                    if (loaded is not null && IsValidState(loaded))
                    {
                        return loaded;
                    }
                }
            }
        }
        catch
        {
            // Fall back to default Japan view on error
        }

        return DefaultJapanState;
    }

    private static void SaveToFile(string mapId, GeoMapState state)
    {
        try
        {
            string filePath = GetStateFilePath(mapId);
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            JsonNode? rootNode = null;
            if (File.Exists(filePath))
            {
                try
                {
                    string existingJson = File.ReadAllText(filePath);
                    rootNode = JsonNode.Parse(existingJson);
                }
                catch { }
            }

            if (rootNode is not JsonObject rootObj)
            {
                rootObj = new JsonObject
                {
                    ["SchemaVersion"] = 1,
                    ["Settings"] = new JsonObject()
                };
                rootNode = rootObj;
            }

            JsonObject settingsObj;
            if (rootObj.TryGetPropertyValue("Settings", out JsonNode? settingsNode) && settingsNode is JsonObject existingSettingsObj)
            {
                settingsObj = existingSettingsObj;
            }
            else
            {
                settingsObj = new JsonObject();
                rootObj["Settings"] = settingsObj;
            }

            settingsObj["MapState"] = JsonSerializer.SerializeToNode(state);

            string outputJson = rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            string tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, outputJson);
            File.Move(tempPath, filePath, true);
        }
        catch
        {
            // Fail silently to avoid interrupting UI interactions
        }
    }
}
