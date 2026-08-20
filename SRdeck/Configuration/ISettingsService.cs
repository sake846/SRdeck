using SRdeck.Models.Configuration;

namespace SRdeck.Configuration;

public interface ISettingsService
{
    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);

    HardwareSettings LoadHardwareSettings(SdrDeviceType deviceType);
    void SaveHardwareSettings(HardwareSettings settings, SdrDeviceType deviceType);

    void BackupSettings();
    void BackupHardwareSettings();
}
