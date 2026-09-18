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

    public static string GetPluginDataDirectory(string mapId) =>
        GetPluginDataDirectory(mapId, null);

    public static string GetPluginDataDirectory(string mapId, string? pluginsDirectory)
    {
        string safeMapId = NormalizeMapId(mapId);
        string fullBaseDir = ResolvePluginsDirectory(pluginsDirectory);
        string candidate = Path.GetFullPath(Path.Combine(fullBaseDir, safeMapId));
        string relative = Path.GetRelativePath(fullBaseDir, candidate);
        if (relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ArgumentException("Map ID resolves outside the plugin data directory.", nameof(mapId));
        }
        return candidate;
    }

    public static string GetStateFilePath(string mapId) =>
        GetStateFilePath(mapId, null);

    public static string GetStateFilePath(string mapId, string? pluginsDirectory) =>
        Path.Combine(GetPluginDataDirectory(mapId, pluginsDirectory), "settings.json");

    public static GeoMapState GetState(string mapId) =>
        GetState(mapId, null);

    public static GeoMapState GetState(string mapId, string? pluginsDirectory)
    {
        string key = NormalizeMapId(mapId);
        string dataDirectory = GetPluginDataDirectory(key, pluginsDirectory);
        string cacheKey = CreateCacheKey(dataDirectory, key);
        lock (Gate)
        {
            if (MemoryCache.TryGetValue(cacheKey, out GeoMapState? cached) && IsValidState(cached))
            {
                return cached;
            }

            GeoMapState state = LoadFromFile(Path.Combine(dataDirectory, "settings.json"));
            MemoryCache[cacheKey] = state;
            return state;
        }
    }

    public static GeoMapState ReloadState(string mapId) =>
        ReloadState(mapId, null);

    public static GeoMapState ReloadState(string mapId, string? pluginsDirectory)
    {
        string key = NormalizeMapId(mapId);
        string dataDirectory = GetPluginDataDirectory(key, pluginsDirectory);
        string cacheKey = CreateCacheKey(dataDirectory, key);
        lock (Gate)
        {
            GeoMapState state = LoadFromFile(Path.Combine(dataDirectory, "settings.json"));
            MemoryCache[cacheKey] = state;
            return state;
        }
    }

    public static void SaveState(string mapId, GeoMapState state)
    {
        _ = TrySaveState(mapId, state);
    }

    public static bool TrySaveState(string mapId, GeoMapState state) =>
        TrySaveState(mapId, state, null);

    public static bool TrySaveState(string mapId, GeoMapState state, string? pluginsDirectory)
    {
        if (!IsValidState(state)) return false;
        string key = NormalizeMapId(mapId);
        string dataDirectory = GetPluginDataDirectory(key, pluginsDirectory);
        string cacheKey = CreateCacheKey(dataDirectory, key);
        lock (Gate)
        {
            if (!SaveToFile(Path.Combine(dataDirectory, "settings.json"), state))
                return false;

            MemoryCache[cacheKey] = state;
            return true;
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

    private static GeoMapState LoadFromFile(string filePath)
    {
        try
        {
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
                    GeoMapState? loaded = mapStateElement.Deserialize<GeoMapState>();
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

    private static bool SaveToFile(string filePath, GeoMapState state)
    {
        string? tempPath = null;
        try
        {
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
            tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, outputJson);
            File.Move(tempPath, filePath, true);
            return true;
        }
        catch
        {
            // The UI-facing SaveState method intentionally preserves the prior
            // best-effort behavior. Tests and other callers can use TrySaveState
            // to observe whether the file was actually persisted.
            return false;
        }
        finally
        {
            try
            {
                if (tempPath is not null && File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Preserve the original save result.
            }
        }
    }

    private static string ResolvePluginsDirectory(string? pluginsDirectory)
    {
        if (!string.IsNullOrWhiteSpace(pluginsDirectory))
            return Path.GetFullPath(pluginsDirectory);

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string baseDir = string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(AppContext.BaseDirectory, "SRdeck", "plugins")
            : Path.Combine(appData, "SRdeck", "plugins");
        return Path.GetFullPath(baseDir);
    }

    private static string CreateCacheKey(string dataDirectory, string mapId) =>
        $"{dataDirectory}\0{mapId}";
}
