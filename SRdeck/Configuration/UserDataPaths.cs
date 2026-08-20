using System;
using System.IO;

namespace SRdeck.Configuration;

public static class UserDataPaths
{
    private const string AppFolderName = "SRdeck";

    public static string UserDataDirectory
    {
        get
        {
            var appDataRootPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(appDataRootPath))
            {
                appDataRootPath = AppContext.BaseDirectory;
            }

            var appDataDirectoryPath = Path.Combine(appDataRootPath, AppFolderName);
            Directory.CreateDirectory(appDataDirectoryPath);
            return appDataDirectoryPath;
        }
    }

    public static string AppSettingsPath => Path.Combine(UserDataDirectory, "appsettings.json");
    public static string HardwareSettingsPath => Path.Combine(UserDataDirectory, "hardware.json");
    public static string LastStatePath => Path.Combine(UserDataDirectory, "last_state.json");
    public static string StationsPath => Path.Combine(UserDataDirectory, "stations.json");
    public static string BandPlansPath => Path.Combine(UserDataDirectory, "bandplans.json");
    public static string PluginsDirectory => Path.Combine(UserDataDirectory, "plugins");
}
