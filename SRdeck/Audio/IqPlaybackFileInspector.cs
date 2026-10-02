using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace SRdeck.Audio;

internal sealed record IqPlaybackFileInfo(
    int SampleRateHz,
    TimeSpan Duration,
    int? CenterFrequencyHz,
    string? CenterFrequencySource);

internal static class IqPlaybackFileInspector
{
    private const int GainRecordSize = sizeof(long) + sizeof(float) + sizeof(int);

    public static IqPlaybackFileInfo Inspect(string filePath)
    {
        using var reader = new PcmWaveFileReader(filePath);
        if (reader.WaveFormat is not { Channels: 2, BitsPerSample: 16 })
            throw new InvalidDataException(
                $"IQ WAV は 16 bit PCM・2チャンネル（左=I、右=Q）のみ再生できます。" +
                $" 選択ファイルは {reader.WaveFormat.BitsPerSample} bit・{reader.WaveFormat.Channels}チャンネルです。");

        (int? centerFrequencyHz, string? source) = ReadCenterFrequency(filePath);
        return new(reader.WaveFormat.SampleRate, reader.TotalTime, centerFrequencyHz, source);
    }

    public static int? TryReadCenterFrequencyHz(string filePath) => ReadCenterFrequency(filePath).FrequencyHz;

    private static (int? FrequencyHz, string? Source) ReadCenterFrequency(string filePath)
    {
        string gainPath = Path.ChangeExtension(filePath, ".gain");
        if (File.Exists(gainPath))
        {
            try
            {
                byte[] firstRecord = new byte[GainRecordSize];
                using FileStream stream = File.OpenRead(gainPath);
                if (stream.Length >= GainRecordSize && stream.Read(firstRecord) == GainRecordSize)
                {
                    int frequencyHz = BinaryPrimitives.ReadInt32LittleEndian(firstRecord.AsSpan(12));
                    if (frequencyHz > 0) return (frequencyHz, Path.GetFileName(gainPath));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (string sidecarPath in SidecarCandidates(filePath))
        {
            if (!File.Exists(sidecarPath)) continue;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
                if (!ReferencesSelectedWav(document.RootElement, Path.GetFileName(filePath))) continue;
                long? frequencyHz = ReadJsonCenterFrequency(document.RootElement, Path.GetFileName(filePath));
                if (frequencyHz is > 0 and <= int.MaxValue)
                    return ((int)frequencyHz.Value, Path.GetFileName(sidecarPath));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return (null, null);
    }

    private static IEnumerable<string> SidecarCandidates(string filePath)
    {
        string directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(filePath);
        yield return Path.ChangeExtension(filePath, ".json");
        yield return Path.Combine(directory, stem + "-diagnostics.json");
        yield return Path.Combine(directory, stem + "-metadata.json");

        foreach (string suffix in new[] { "-raw-iq", "-channel-iq" })
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(directory, stem[..^suffix.Length] + "-diagnostics.json");
        }
    }

    private static bool ReferencesSelectedWav(JsonElement root, string fileName)
    {
        string[] fileProperties = ["RawIqFile", "ChannelIqFile", "WavFile"];
        bool hasFileReference = false;
        foreach (string propertyName in fileProperties)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement property) ||
                property.ValueKind != JsonValueKind.String) continue;
            hasFileReference = true;
            if (string.Equals(property.GetString(), fileName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return !hasFileReference;
    }

    private static long? ReadJsonCenterFrequency(JsonElement root, string fileName)
    {
        bool channelIq = IsChannelIq(root, fileName);
        if (!channelIq && TryReadNestedInt64(root, "InputMetadata", "CenterFrequencyHz", out long inputCenter))
            return inputCenter;

        if (TryReadNestedInt64(root, "ChannelConfiguration", "ChannelCenterFrequencyHz", out long channelCenter))
            return channelCenter;
        foreach (string propertyName in new[] { "CenterFrequencyHz", "FrequencyHz", "ChannelCenterFrequencyHz", "DialFrequencyHz" })
            if (TryReadInt64(root, propertyName, out long value)) return value;
        foreach (string objectName in new[] { "SelectedChannel", "SelectedBand", "Channel" })
            foreach (string propertyName in new[] { "CenterFrequencyHz", "FrequencyHz", "ChannelCenterFrequencyHz", "DialFrequencyHz" })
                if (TryReadNestedInt64(root, objectName, propertyName, out long value)) return value;

        return TryReadNestedInt64(root, "InputMetadata", "CenterFrequencyHz", out inputCenter)
            ? inputCenter
            : null;
    }

    private static bool IsChannelIq(JsonElement root, string fileName)
    {
        if (root.TryGetProperty("ChannelIqFile", out JsonElement channelFile) &&
            channelFile.ValueKind == JsonValueKind.String &&
            string.Equals(channelFile.GetString(), fileName, StringComparison.OrdinalIgnoreCase)) return true;

        if (ContainsChannelText(root, "CaptureInput") || ContainsChannelText(root, "CaptureSignal") ||
            ContainsChannelText(root, "CaptureMode")) return true;
        if (root.TryGetProperty("ChannelConfiguration", out JsonElement configuration) &&
            configuration.ValueKind == JsonValueKind.Object) return true;

        return TryReadInt64(root, "SampleRateHz", out long captureRate) &&
               TryReadNestedInt64(root, "InputMetadata", "SampleRateHz", out long inputRate) &&
               captureRate != inputRate;
    }

    private static bool ContainsChannelText(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String &&
        property.GetString()?.Contains("channel", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryReadNestedInt64(
        JsonElement root,
        string objectName,
        string propertyName,
        out long value)
    {
        value = 0;
        return root.TryGetProperty(objectName, out JsonElement child) &&
               child.ValueKind == JsonValueKind.Object &&
               TryReadInt64(child, propertyName, out value);
    }

    private static bool TryReadInt64(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out JsonElement property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }
}
