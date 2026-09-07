using System;
using System.Text.Json;

namespace SRdeck.Configuration;

public class JsonLastStateService : ILastStateService, ISettingsPersistenceNotifications
{
    private readonly JsonSettingsFile<LastState> _file;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public event Action<SettingsPersistenceIssue>? PersistenceIssue;

    public JsonLastStateService() : this(UserDataPaths.LastStatePath) { }

    public JsonLastStateService(string path) =>
        _file = new(path, SerializerOptions, SerializerOptions, issue => PersistenceIssue?.Invoke(issue));

    public LastState LoadLastState() => _file.Load(createIfMissing: false);
    public void SaveLastState(LastState state) => _file.Save(state);
    public void BackupLastState() => _file.Backup();
}
