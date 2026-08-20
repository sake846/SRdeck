using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace SRdeckPlugin.Sdk;

/// <summary>
/// Small append-only JSONL store for decoded plugin history.
/// </summary>
public static class PluginJsonLinesHistory
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, int> EntryCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastCompactions = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<T> Load<T>(string path, int maximumEntries,
        JsonSerializerOptions? options = null)
    {
        if (maximumEntries <= 0 || !File.Exists(path)) return [];

        lock (GateFor(path))
        {
            var entries = new Queue<T>(maximumEntries);
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    T? value = JsonSerializer.Deserialize<T>(line, options);
                    if (value is null) continue;
                    if (entries.Count == maximumEntries) entries.Dequeue();
                    entries.Enqueue(value);
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException)
                {
                    // Keep valid records even if a partial or manually edited line is present.
                }
            }
            return entries.ToArray();
        }
    }

    /// <summary>
    /// Reads every valid record currently persisted in a JSONL file.  The read is
    /// performed under the same per-file gate as append/compaction so callers get
    /// a consistent snapshot while reception continues.
    /// </summary>
    public static IReadOnlyList<T> LoadAll<T>(string path,
        JsonSerializerOptions? options = null)
    {
        if (!File.Exists(path)) return [];
        lock (GateFor(path)) return ReadAll<T>(path, options);
    }

    public static void Append<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        lock (GateFor(path))
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(path,
                JsonSerializer.Serialize(value, options) + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            EntryCounts.AddOrUpdate(path,
                static file => CountLines(file),
                static (_, count) => checked(count + 1));
        }
    }

    /// <summary>
    /// Appends a record and compacts the JSONL file when the configured retention
    /// policy is exceeded. Existing callers may continue to use <see cref="Append"/>
    /// when they intentionally want append-only behavior.
    /// </summary>
    public static void AppendAndRetain<T>(
        string path,
        T value,
        int maximumEntries,
        TimeSpan? maximumAge,
        DateTimeOffset now,
        Func<T, DateTimeOffset>? timestampSelector = null,
        JsonSerializerOptions? options = null,
        long maximumBytes = 0)
    {
        AppendBatchAndRetain(path, [value], maximumEntries, maximumAge, now,
            timestampSelector, options, maximumBytes);
    }

    /// <summary>
    /// Appends a batch and applies retention once for the whole batch.  This is
    /// used by the background writer so bursty reception does not perform one
    /// file open/flush/compaction cycle per decoded frame.
    /// </summary>
    public static void AppendBatchAndRetain<T>(
        string path,
        IReadOnlyCollection<T> values,
        int maximumEntries,
        TimeSpan? maximumAge,
        DateTimeOffset now,
        Func<T, DateTimeOffset>? timestampSelector = null,
        JsonSerializerOptions? options = null,
        long maximumBytes = 0)
    {
        if (values.Count == 0) return;

        lock (GateFor(path))
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string appendText = string.Join(Environment.NewLine,
                values.Select(value => JsonSerializer.Serialize(value, options))) + Environment.NewLine;
            File.AppendAllText(path, appendText,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            int count = EntryCounts.AddOrUpdate(path,
                static file => CountLines(file),
                (_, existing) => checked(existing + values.Count));
            bool compactionDue = !LastCompactions.TryGetValue(path, out DateTimeOffset lastCompaction) ||
                now - lastCompaction >= TimeSpan.FromSeconds(5);
            bool entryLimitExceeded = maximumEntries > 0 && count > maximumEntries && compactionDue;
            bool byteLimitExceeded = maximumBytes > 0 &&
                new FileInfo(path).Length > maximumBytes && compactionDue;
            bool ageLimitDue = maximumAge is not null && timestampSelector is not null &&
                compactionDue;
            if (!entryLimitExceeded && !byteLimitExceeded && !ageLimitDue) return;

            List<T> retained = ReadAll<T>(path, options);
            if (maximumAge is not null && timestampSelector is not null)
            {
                DateTimeOffset cutoff = now.ToUniversalTime() - maximumAge.Value;
                retained = retained.Where(item =>
                        timestampSelector(item).ToUniversalTime() >= cutoff)
                    .ToList();
            }
            if (maximumEntries > 0 && retained.Count > maximumEntries)
                retained = retained.Skip(retained.Count - maximumEntries).ToList();
            if (maximumBytes > 0)
            {
                while (retained.Count > 1 && SerializedByteCount(retained, options) > maximumBytes)
                    retained.RemoveAt(0);
            }

            RewriteUnlocked(path, retained, options);
            EntryCounts[path] = retained.Count;
            LastCompactions[path] = now;
        }
    }

    public static void Delete(string path)
    {
        lock (GateFor(path))
        {
            if (File.Exists(path)) File.Delete(path);
            EntryCounts.TryRemove(path, out _);
            LastCompactions.TryRemove(path, out _);
        }
    }

    public static void Rewrite<T>(string path, IEnumerable<T> values,
        JsonSerializerOptions? options = null)
    {
        lock (GateFor(path))
        {
            List<T> materialized = values.ToList();
            RewriteUnlocked(path, materialized, options);
            EntryCounts[path] = materialized.Count;
            LastCompactions[path] = DateTimeOffset.UtcNow;
        }
    }

    private static List<T> ReadAll<T>(string path, JsonSerializerOptions? options)
    {
        var values = new List<T>();
        if (!File.Exists(path)) return values;
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                T? value = JsonSerializer.Deserialize<T>(line, options);
                if (value is not null) values.Add(value);
            }
            catch (JsonException) { }
            catch (NotSupportedException) { }
        }
        return values;
    }

    private static void RewriteUnlocked<T>(string path, IReadOnlyList<T> values,
        JsonSerializerOptions? options)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(temporaryPath,
                values.Select(value => JsonSerializer.Serialize(value, options)),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static long SerializedByteCount<T>(IEnumerable<T> values, JsonSerializerOptions? options)
    {
        long bytes = 0;
        foreach (T value in values)
        {
            bytes += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, options));
            bytes += Environment.NewLine.Length;
        }
        return bytes;
    }

    private static int CountLines(string path)
    {
        if (!File.Exists(path)) return 0;
        int count = 0;
        foreach (string line in File.ReadLines(path))
            if (!string.IsNullOrWhiteSpace(line)) count++;
        return count;
    }

    private static object GateFor(string path) =>
        Gates.GetOrAdd(Path.GetFullPath(path), static _ => new object());
}
