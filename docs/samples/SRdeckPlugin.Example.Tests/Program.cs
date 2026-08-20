using SRdeckPlugin.Contracts;
using SRdeckPlugin.Example;

var plugin = new ExamplePlugin();
Assert(plugin.Descriptor.Id == "example.decoder", "stable descriptor ID");
Assert(plugin.Descriptor.Capabilities ==
       (PluginCapabilities.IqConsumer | PluginCapabilities.Headless),
    "capabilities match implemented contracts");

var host = new TestHostContext(plugin.Descriptor.Id);
await plugin.InitializeAsync(host, CancellationToken.None);
Assert(plugin.State == PluginLifecycleState.Initialized, "initialize transition");
await plugin.ActivateAsync(CancellationToken.None);
await plugin.StartStreamAsync(CancellationToken.None);
Assert(plugin.State == PluginLifecycleState.Streaming, "start transition");

var metadata = new IqBlockMetadata(
    StreamId: Guid.NewGuid(),
    Generation: 1,
    Sequence: 0,
    AbsoluteSampleStart: 0,
    MonotonicTimestamp: 0,
    UtcTimestamp: DateTimeOffset.UnixEpoch,
    SampleRateHz: 48_000,
    CenterFrequencyHz: 100_000_000,
    SampleCount: 4,
    InputSource: IqInputSource.Playback,
    Discontinuity: IqDiscontinuity.StreamStarted);
using (var lease = new TestIqBlockLease(metadata, new Complex32[4]))
    await plugin.ConsumeAsync(lease, CancellationToken.None);
Assert(plugin.ProcessedSamples == 4, "IQ samples consumed");

await plugin.StopStreamAsync(CancellationToken.None);
await plugin.StopStreamAsync(CancellationToken.None);
Assert(plugin.State == PluginLifecycleState.Active, "idempotent stop");
await plugin.DeactivateAsync(CancellationToken.None);
await plugin.DeactivateAsync(CancellationToken.None);
Assert(plugin.State == PluginLifecycleState.Initialized, "idempotent deactivate");
await plugin.DisposeAsync();
await plugin.DisposeAsync();
Assert(plugin.State == PluginLifecycleState.Disposed, "idempotent dispose");

Console.WriteLine("PASS  minimal plugin conformance example");
return;

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"Assertion failed: {name}");
}

sealed class TestHostContext(string pluginId) : IPluginHostContext
{
    public string PluginId { get; } = pluginId;
    public TimeProvider TimeProvider { get; } = TimeProvider.System;
    public IPluginLogger Logger { get; } = new TestLogger();
    public IPluginSettingsStore Settings { get; } = new TestSettingsStore();
    public IPluginTuningService Tuning { get; } = new TestTuningService();
    public IPluginAudioSink Audio { get; } = new TestAudioSink();
    public IPluginDispatcher Dispatcher { get; } = new InlineDispatcher();
    public IPluginNotificationService Notifications { get; } = new TestNotifications();
}

sealed class TestLogger : IPluginLogger
{
    public void Log(PluginLogLevel level, string eventId, string message, Exception? exception = null) { }
}

sealed class TestSettingsStore : IPluginSettingsStore
{
    public string DataDirectory { get; } = Path.GetTempPath();
    public ValueTask<PluginSettingsDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<PluginSettingsDocument?>(null);
    public ValueTask SaveAsync(PluginSettingsDocument document, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
    public ValueTask DeleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

sealed class TestTuningService : IPluginTuningService
{
    public PluginTuningResult Current { get; } = new(
        PluginTuningOutcome.Accepted, "test", 100_000_000, 48_000, 99_976_000, 100_024_000,
        100_000_000);
    public event EventHandler<PluginTuningResult>? AppliedConfigurationChanged
    {
        add { }
        remove { }
    }
    public ValueTask<PluginTuningResult> RequestAsync(
        PluginTuningRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Current);
}

sealed class TestAudioSink : IPluginAudioSink
{
    public bool TrySubmit(PcmAudioFrame frame) => true;
    public void Reset() { }
}

sealed class InlineDispatcher : IPluginDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
}

sealed class TestNotifications : IPluginNotificationService
{
    public void PlayReceptionAlarm(TimeSpan delay = default) { }
}

sealed class TestIqBlockLease(IqBlockMetadata metadata, Complex32[] samples) : IIqBlockLease
{
    public IqBlockMetadata Metadata { get; } = metadata;
    public ReadOnlyMemory<Complex32> Samples { get; } = samples;
    public void Dispose() { }
}
