using System.Text.Json;
using SRdeck.Models.Configuration;

namespace SRdeck.Configuration;

public class JsonSettingsService : ISettingsService, ISettingsPersistenceNotifications
{
    private readonly JsonSettingsFile<AppSettings> _settings;
    private readonly JsonSettingsFile<HardwareSettingsStore> _hardware;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static readonly JsonSerializerOptions HardwareReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    public event Action<SettingsPersistenceIssue>? PersistenceIssue;

    public JsonSettingsService() : this(UserDataPaths.AppSettingsPath, UserDataPaths.HardwareSettingsPath) { }

    public JsonSettingsService(string settingsPath, string hardwarePath)
    {
        _settings = new(settingsPath, ReadOptions, WriteOptions, Report, Normalize);
        _hardware = new(hardwarePath, HardwareReadOptions, WriteOptions, Report);
    }

    public AppSettings LoadSettings() => _settings.Load();
    public void SaveSettings(AppSettings settings) => _settings.Save(settings);
    public void BackupSettings() => _settings.Backup();
    public void BackupHardwareSettings() => _hardware.Backup();

    public HardwareSettings LoadHardwareSettings(SdrDeviceType deviceType)
    {
        HardwareSettingsStore store = _hardware.Load();
        return deviceType switch
        {
            SdrDeviceType.RtlSdr => store.RtlSdr ?? new HardwareSettings(),
            SdrDeviceType.HackRf => store.HackRf ?? new HardwareSettings(),
            SdrDeviceType.Rx888Mk2 => store.Rx888 ?? new HardwareSettings(),
            _ => store.SdrPlay ?? new HardwareSettings()
        };
    }

    public void SaveHardwareSettings(HardwareSettings settings, SdrDeviceType deviceType) =>
        _hardware.Update(store =>
        {
            if (deviceType == SdrDeviceType.RtlSdr) store.RtlSdr = settings;
            else if (deviceType == SdrDeviceType.HackRf) store.HackRf = settings;
            else if (deviceType == SdrDeviceType.Rx888Mk2) store.Rx888 = settings;
            else store.SdrPlay = settings;
        });

    private void Report(SettingsPersistenceIssue issue) => PersistenceIssue?.Invoke(issue);

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Display ??= new();
        settings.Power ??= new();
        settings.Plugins ??= new();
        settings.SignalProcessing ??= new();
        settings.Demodulation ??= new();
        return settings;
    }

    private sealed class HardwareSettingsStore
    {
        public HardwareSettings SdrPlay { get; set; } = new();
        public HardwareSettings RtlSdr { get; set; } = new();
        public HardwareSettings HackRf { get; set; } = new();
        public HardwareSettings Rx888 { get; set; } = new();
    }
}
