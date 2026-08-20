using System.IO;
using System.Text.Json;
using SRdeckPlugin.Contracts;
using SRdeck.Configuration;

namespace SRdeck.Services.Plugins;

public interface IPluginSettingsStoreFactory
{
    IPluginSettingsStore Create(string pluginId);
}

public sealed class JsonPluginSettingsStoreFactory : IPluginSettingsStoreFactory
{
    private readonly string _pluginsDirectory;

    public JsonPluginSettingsStoreFactory() : this(UserDataPaths.PluginsDirectory) { }

    public JsonPluginSettingsStoreFactory(string pluginsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        _pluginsDirectory = Path.GetFullPath(pluginsDirectory);
    }

    public IPluginSettingsStore Create(string pluginId) =>
        new JsonPluginSettingsStore(_pluginsDirectory, pluginId);
}

internal sealed class JsonPluginSettingsStore : IPluginSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonPluginSettingsStore(string pluginsDirectory, string pluginId)
    {
        ValidatePluginId(pluginId);
        DataDirectory = Path.Combine(pluginsDirectory, pluginId);
        _settingsPath = Path.Combine(DataDirectory, "settings.json");
    }

    public string DataDirectory { get; }

    public async ValueTask<PluginSettingsDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath)) return null;
            await using FileStream stream = new(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            PersistedSettings? persisted = await JsonSerializer.DeserializeAsync<PersistedSettings>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (persisted is null || persisted.SchemaVersion < 0 || persisted.Settings.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException($"Invalid plugin settings file '{_settingsPath}'.");
            return new PluginSettingsDocument(
                persisted.SchemaVersion,
                persisted.Settings.GetRawText(),
                persisted.SecretJsonPaths);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        PluginSettingsDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion < 0) throw new ArgumentOutOfRangeException(nameof(document));
        if (document.SecretJsonPaths?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ArgumentException("Secret JSON paths must not be empty.", nameof(document));
        using JsonDocument parsed = JsonDocument.Parse(document.Json);
        JsonElement settings = parsed.RootElement.Clone();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporaryPath = _settingsPath + ".tmp";
        try
        {
            Directory.CreateDirectory(DataDirectory);
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new PersistedSettings(
                        document.SchemaVersion,
                        settings,
                        document.SecretJsonPaths?.Distinct(StringComparer.Ordinal).ToArray()),
                    WriteOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            // A cancelled/failed write must never leave a stale temporary file
            // that can be mistaken for a recoverable settings snapshot later.
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original serialization/I/O/cancellation error.
            }
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidatePluginId(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (pluginId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            pluginId.Contains(Path.DirectorySeparatorChar) ||
            pluginId.Contains(Path.AltDirectorySeparatorChar) ||
            pluginId is "." or "..")
        {
            throw new ArgumentException("The plugin ID is not safe for use as a data directory.", nameof(pluginId));
        }
    }

    private sealed record PersistedSettings(
        int SchemaVersion,
        JsonElement Settings,
        IReadOnlyList<string>? SecretJsonPaths = null);
}
