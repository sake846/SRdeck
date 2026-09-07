using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SRdeck.Configuration;

internal static class SettingsFileLocks
{
    internal static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Serializes updates and replaces settings only after a complete, flushed write.</summary>
internal sealed class JsonSettingsFile<T> where T : class, new()
{
    private readonly string _path;
    private readonly object _gate;
    private readonly JsonSerializerOptions _readOptions;
    private readonly JsonSerializerOptions _writeOptions;
    private readonly Action<SettingsPersistenceIssue> _report;
    private readonly Func<T, T> _normalize;

    public JsonSettingsFile(string path, JsonSerializerOptions readOptions,
        JsonSerializerOptions writeOptions, Action<SettingsPersistenceIssue> report,
        Func<T, T>? normalize = null)
    {
        _path = Path.GetFullPath(path);
        _gate = SettingsFileLocks.Gates.GetOrAdd(_path, _ => new object());
        _readOptions = readOptions;
        _writeOptions = writeOptions;
        _report = report;
        _normalize = normalize ?? (value => value);
    }

    public T Load(bool createIfMissing = true)
    {
        lock (_gate)
        {
            try { return ReadOrRecover(createIfMissing, allowReadOnlyRecovery: true); }
            catch (Exception ex) when (IsPersistenceException(ex))
            {
                Report("読み込み", "設定を読み込めませんでした。今回は既定値を使用します。", true, ex);
                return new T();
            }
        }
    }

    public void Save(T value)
    {
        lock (_gate)
        {
            try
            {
                // Serialize before touching either the current file or its backup.
                byte[] contents = JsonSerializer.SerializeToUtf8Bytes(value, _writeOptions);
                if (File.Exists(_path)) _ = ReadOrRecover(createIfMissing: false);
                WriteAtomic(_path, contents, backupExisting: true);
            }
            catch (Exception ex) when (IsPersistenceException(ex))
            {
                Report("保存", "設定を保存できませんでした。変更内容は保存されていません。", true, ex);
            }
        }
    }

    public void Update(Action<T> update)
    {
        lock (_gate)
        {
            try
            {
                // Reading and replacing share a lock, including across service instances.
                T value = ReadOrRecover(createIfMissing: false);
                update(value);
                WriteAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(value, _writeOptions), backupExisting: true);
            }
            catch (Exception ex) when (IsPersistenceException(ex))
            {
                Report("保存", "設定を保存できませんでした。変更内容は保存されていません。", true, ex);
            }
        }
    }

    public void Backup()
    {
        lock (_gate)
        {
            try
            {
                T value = ReadOrRecover(createIfMissing: false);
                if (File.Exists(_path))
                    WriteAtomic(_path + ".bak", JsonSerializer.SerializeToUtf8Bytes(value, _writeOptions), backupExisting: false);
            }
            catch (Exception ex) when (IsPersistenceException(ex))
            {
                Report("バックアップ", "設定のバックアップを保存できませんでした。", true, ex);
            }
        }
    }

    private T ReadOrRecover(bool createIfMissing, bool allowReadOnlyRecovery = false)
    {
        bool corrupt = false;
        Exception? corruption = null;
        try { return Read(_path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (JsonException ex) { corrupt = true; corruption = ex; }

        T value;
        bool recoveredBackup = false;
        try { value = Read(_path + ".bak"); recoveredBackup = true; }
        catch (FileNotFoundException) { value = new T(); }
        catch (DirectoryNotFoundException) { value = new T(); }
        catch (JsonException) { value = new T(); }

        try
        {
            if (corrupt)
            {
                // Keep the invalid source for inspection; never copy it over a valid backup.
                string quarantine = _path + $".corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
                File.Move(_path, quarantine);
            }

            if (corrupt || recoveredBackup || createIfMissing)
                WriteAtomic(_path, JsonSerializer.SerializeToUtf8Bytes(value, _writeOptions), backupExisting: false);
        }
        catch (Exception ex) when (allowReadOnlyRecovery && IsPersistenceException(ex))
        {
            Report("復旧", recoveredBackup
                ? "バックアップを読み込みましたが、設定ファイルを復元できませんでした。"
                : "既定値を使用しますが、設定ファイルを保存できませんでした。", true, ex);
            return value;
        }

        if (corrupt || recoveredBackup)
            Report("復旧", recoveredBackup
                ? "設定ファイルをバックアップから復旧しました。"
                : "破損した設定ファイルを退避し、既定値で復旧しました。", false, corruption);

        return value;
    }

    private T Read(string path) => _normalize(
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), _readOptions)
        ?? throw new JsonException("The settings document must contain an object."));

    private static void WriteAtomic(string path, byte[] contents, bool backupExisting)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, backupExisting ? path + ".bak" : null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException ex) { Trace.TraceWarning($"Could not remove settings temporary file: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Trace.TraceWarning($"Could not remove settings temporary file: {ex.Message}"); }
        }
    }

    private void Report(string operation, string message, bool isError, Exception? exception)
    {
        var issue = new SettingsPersistenceIssue(_path, operation, message, isError, exception);
        Trace.TraceWarning($"{operation}: {_path}: {message} {exception?.Message}");
        try { _report(issue); }
        catch (Exception ex) { Trace.TraceWarning($"Settings notification failed: {ex.Message}"); }
    }

    private static bool IsPersistenceException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException;
}
