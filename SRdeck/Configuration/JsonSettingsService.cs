using System.IO;
using System.Text.Json;
using SRdeck.Models.Configuration;

namespace SRdeck.Configuration;

public class JsonSettingsService : ISettingsService
{
    private readonly string _filePath = UserDataPaths.AppSettingsPath;
    private readonly string _hwFilePath = UserDataPaths.HardwareSettingsPath;

    private static readonly JsonSerializerOptions DefaultWriteOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions DefaultReadOptions = new() 
    { 
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions HwReadOptions = new() 
    { 
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings LoadSettings()
    {
        if (!File.Exists(_filePath))
        {
            var defaultSettings = new AppSettings();
            SaveSettings(defaultSettings);
            return defaultSettings;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, DefaultReadOptions) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, DefaultWriteOptions);
            File.WriteAllText(_filePath, json);
        }
        catch 
        {
            // Settings save failure shouldn't crash the app
        }
    }

    private sealed class HardwareSettingsStore
    {
        public HardwareSettings SdrPlay { get; set; } = new();
        public HardwareSettings RtlSdr { get; set; } = new();
    }

    public HardwareSettings LoadHardwareSettings(SdrDeviceType deviceType)
    {
        if (!File.Exists(_hwFilePath))
        {
            var defaultStore = new HardwareSettingsStore();
            SaveHardwareStore(defaultStore);
            return ResolveHardwareSettings(defaultStore, deviceType);
        }

        try
        {
            var json = File.ReadAllText(_hwFilePath);

            var store = JsonSerializer.Deserialize<HardwareSettingsStore>(json, HwReadOptions);
            if (store != null)
            {
                return ResolveHardwareSettings(store, deviceType);
            }

            return new();
        }
        catch
        {
            return new();
        }
    }

    public void SaveHardwareSettings(HardwareSettings settings, SdrDeviceType deviceType)
    {
        try
        {
            HardwareSettingsStore store;
            if (File.Exists(_hwFilePath))
            {
                var json = File.ReadAllText(_hwFilePath);
                store = JsonSerializer.Deserialize<HardwareSettingsStore>(json, HwReadOptions) ?? new HardwareSettingsStore();
            }
            else
            {
                store = new HardwareSettingsStore();
            }

            switch (deviceType)
            {
                case SdrDeviceType.SdrPlay:
                    store.SdrPlay = settings;
                    break;
                case SdrDeviceType.RtlSdr:
                    store.RtlSdr = settings;
                    break;
                default:
                    store.SdrPlay = settings;
                    break;
            }

            SaveHardwareStore(store);
        }
        catch 
        {
            // Settings save failure shouldn't crash the app
        }
    }

    public void BackupSettings()
    {
        if (File.Exists(_filePath))
        {
            try { File.Copy(_filePath, _filePath + ".bak", true); } catch { }
        }
    }

    public void BackupHardwareSettings()
    {
        if (File.Exists(_hwFilePath))
        {
            try { File.Copy(_hwFilePath, _hwFilePath + ".bak", true); } catch { }
        }
    }

    private static HardwareSettings ResolveHardwareSettings(HardwareSettingsStore store, SdrDeviceType deviceType)
    {
        return deviceType switch
        {
            SdrDeviceType.SdrPlay => store.SdrPlay ?? new HardwareSettings(),
            SdrDeviceType.RtlSdr => store.RtlSdr ?? new HardwareSettings(),
            _ => store.SdrPlay ?? new HardwareSettings()
        };
    }

    private void SaveHardwareStore(HardwareSettingsStore store)
    {
        var json = JsonSerializer.Serialize(store, DefaultWriteOptions);
        File.WriteAllText(_hwFilePath, json);
    }
}
