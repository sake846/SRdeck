using System.IO;
using System.Text.Json;

namespace SRdeck.Configuration;

public class JsonLastStateService : ILastStateService
{
    private readonly string _filePath = UserDataPaths.LastStatePath;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public LastState LoadLastState()
    {
        if (!File.Exists(_filePath))
        {
            return new();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<LastState>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public void SaveLastState(LastState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, SerializerOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Fail silently to avoid crashing the app
        }
    }

    public void BackupLastState()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                File.Copy(_filePath, _filePath + ".bak", true);
            }
            catch
            {
                // Fail silently
            }
        }
    }
}
