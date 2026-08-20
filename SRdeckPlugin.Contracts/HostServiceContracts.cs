namespace SRdeckPlugin.Contracts;

/// <summary>
/// Plugin settings serialized by the host. <see cref="SecretJsonPaths"/> is
/// classification metadata only; it does not encrypt <see cref="Json"/>.
/// </summary>
public sealed record PluginSettingsDocument(
    int SchemaVersion,
    string Json,
    IReadOnlyList<string>? SecretJsonPaths = null);

public interface IPluginSettingsStore
{
    string DataDirectory { get; }
    ValueTask<PluginSettingsDocument?> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(PluginSettingsDocument document, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(CancellationToken cancellationToken = default);
}

public readonly record struct TuningTarget(long FrequencyHz, int BandwidthHz);

public enum PluginGainPreference
{
    Unspecified,
    Automatic,
    Manual
}

public sealed record PluginTuningRequest(
    string ProfileId,
    string DisplayName,
    IReadOnlyList<TuningTarget> Targets,
    long? PreferredCenterFrequencyHz,
    int MinimumSampleRateHz,
    int? FrequencyStepHz,
    bool RequiresContinuousReception,
    bool AllowsScanning,
    PluginGainPreference GainPreference,
    bool PreservePreferredCenterFrequency = false);

public enum PluginTuningOutcome
{
    Accepted,
    Adjusted,
    Rejected,
    Deferred
}

public sealed record PluginTuningResult(
    PluginTuningOutcome Outcome,
    string Message,
    long CenterFrequencyHz,
    int SampleRateHz,
    long PassbandLowerFrequencyHz,
    long PassbandUpperFrequencyHz,
    long TargetFrequencyHz = 0);

public interface IPluginTuningService
{
    PluginTuningResult Current { get; }
    event EventHandler<PluginTuningResult>? AppliedConfigurationChanged;
    ValueTask<PluginTuningResult> RequestAsync(
        PluginTuningRequest request,
        CancellationToken cancellationToken = default);
}

public enum PcmSampleFormat
{
    Signed16LittleEndian
}

public sealed record PcmAudioFrame(
    string PluginId,
    Guid StreamId,
    long Sequence,
    int SampleRateHz,
    int Channels,
    PcmSampleFormat Format,
    ReadOnlyMemory<byte> Data,
    bool IsDiscontinuous,
    float? NormalizationGain = null);

public interface IPluginAudioSink
{
    bool TrySubmit(PcmAudioFrame frame);
    void Reset();
}

public interface IPluginNotificationService
{
    void PlayReceptionAlarm(TimeSpan delay = default);

    void PlayShortReceptionAlarm(TimeSpan delay = default)
    {
        PlayReceptionAlarm(delay);
    }
}
