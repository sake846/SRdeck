using System.Diagnostics;
using SRdeckPlugin.Contracts;
using SRdeck.Services;

namespace SRdeck.Services.Plugins;

public sealed record PluginRuntimeInfo(
    PluginDescriptor Descriptor,
    PluginLifecycleState State,
    bool IsCompatible,
    string? LastError,
    string? SelectedProfileId = null);

public sealed record PluginOperationResult(bool Succeeded, string? Error)
{
    public static PluginOperationResult Success { get; } = new(true, null);
    public static PluginOperationResult Failure(string error) => new(false, error);
}

public sealed record PluginManagerOptions(TimeSpan LifecycleOperationTimeout)
{
    public static PluginManagerOptions Default { get; } = new(TimeSpan.FromSeconds(5));
}

public sealed class PluginRuntimeChangedEventArgs(PluginRuntimeInfo runtime) : EventArgs
{
    public PluginRuntimeInfo Runtime { get; } = runtime;
}

public interface IPluginHostContextFactory
{
    IPluginHostContext Create(string pluginId);
}

public interface IPluginManager : IAsyncDisposable
{
    IReadOnlyList<PluginRuntimeInfo> Plugins { get; }
    string? ActivePluginId { get; }
    IReadOnlyList<string> ActivePluginIds { get; }
    IReadOnlyList<string> StreamingPluginIds { get; }
    bool IsActivePluginStreaming { get; }
    event EventHandler<PluginRuntimeChangedEventArgs>? RuntimeChanged;

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask<PluginOperationResult> ActivateAsync(string pluginId, CancellationToken cancellationToken = default);
    ValueTask<PluginOperationResult> ActivateAdditionalAsync(string pluginId, CancellationToken cancellationToken = default);
    ValueTask<PluginOperationResult> SelectProfileAsync(
        string pluginId,
        string profileId,
        CancellationToken cancellationToken = default);
    ValueTask<PluginOperationResult> StartStreamAsync(CancellationToken cancellationToken = default);
    ValueTask<PluginOperationResult> StartStreamAsync(string pluginId, CancellationToken cancellationToken = default);
    ValueTask<PluginOperationResult> StopStreamAsync(CancellationToken cancellationToken = default);
    ValueTask<PluginOperationResult> StopStreamAsync(string pluginId, CancellationToken cancellationToken = default);
    ValueTask<PluginOperationResult> DeactivateAsync(string pluginId, CancellationToken cancellationToken = default);
    ValueTask ShutdownAsync(CancellationToken cancellationToken = default);
    bool TryGetActiveCapability<TCapability>(out TCapability? capability) where TCapability : class;
    bool IsPluginActive(string pluginId);
    bool IsPluginStreaming(string pluginId);
    void ReportFault(string pluginId, string operation, Exception exception);
}

public sealed class PluginManager : IPluginManager
{
    private readonly PluginRuntimeRegistry _runtimeRegistry;
    private readonly PluginLifecycleCoordinator _lifecycleCoordinator;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private bool _isDisposed;

    public PluginManager(
        IEnumerable<IPluginModule> modules,
        IPluginHostContextFactory contextFactory,
        PluginManagerOptions? options = null)
    {
        PluginManagerOptions resolvedOptions = options ?? PluginManagerOptions.Default;
        if (resolvedOptions.LifecycleOperationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "The lifecycle operation timeout must be positive.");

        _runtimeRegistry = new PluginRuntimeRegistry(modules, contextFactory);
        _lifecycleCoordinator = new PluginLifecycleCoordinator(_runtimeRegistry, resolvedOptions);
        _runtimeRegistry.RuntimeChanged += HandleRuntimeChanged;
    }

    public IReadOnlyList<PluginRuntimeInfo> Plugins => _runtimeRegistry.Plugins;

    public string? ActivePluginId => _runtimeRegistry.ActivePluginId;

    public IReadOnlyList<string> ActivePluginIds => _runtimeRegistry.ActivePluginIds;

    public IReadOnlyList<string> StreamingPluginIds => _runtimeRegistry.StreamingPluginIds;

    public bool IsActivePluginStreaming => _runtimeRegistry.IsActivePluginStreaming;

    public event EventHandler<PluginRuntimeChangedEventArgs>? RuntimeChanged;

    public bool IsPluginActive(string pluginId) => _runtimeRegistry.IsPluginActive(pluginId);

    public bool IsPluginStreaming(string pluginId) => _runtimeRegistry.IsPluginStreaming(pluginId);

    public void ReportFault(string pluginId, string operation, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        if (_isDisposed) return;

        _lifecycleGate.Wait();
        try
        {
            _lifecycleCoordinator.ReportFault(pluginId, operation, exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool TryGetActiveCapability<TCapability>(out TCapability? capability) where TCapability : class =>
        _runtimeRegistry.TryGetActiveCapability(out capability);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycleCoordinator.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginOperationResult> ActivateAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _lifecycleCoordinator.ActivateAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginOperationResult> ActivateAdditionalAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _lifecycleCoordinator.ActivateAdditionalAsync(pluginId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginOperationResult> SelectProfileAsync(
        string pluginId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _lifecycleCoordinator.SelectProfileAsync(pluginId, profileId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginOperationResult> StartStreamAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _lifecycleCoordinator.StartStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginOperationResult> StartStreamAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _lifecycleCoordinator.StartStreamAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginOperationResult> StopStreamAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _lifecycleCoordinator.StopStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginOperationResult> StopStreamAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _lifecycleCoordinator.StopStreamAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<PluginOperationResult> DeactivateAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _lifecycleCoordinator.DeactivateAsync(pluginId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed) return;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _lifecycleCoordinator.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        _isDisposed = true;
        _runtimeRegistry.RuntimeChanged -= HandleRuntimeChanged;
        _lifecycleGate.Dispose();
    }

    private void HandleRuntimeChanged(object? sender, PluginRuntimeChangedEventArgs eventArgs)
    {
        EventHandler<PluginRuntimeChangedEventArgs>? handlers = RuntimeChanged;
        if (handlers is null) return;
        _runtimeRegistry.TryGetEntry(eventArgs.Runtime.Descriptor.Id, out PluginRuntimeEntry? entry);
        foreach (EventHandler<PluginRuntimeChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                entry?.Context.Logger.Log(
                    PluginLogLevel.Warning,
                    "plugin.runtime-notification.failed",
                    $"A runtime-state subscriber failed for plugin '{eventArgs.Runtime.Descriptor.Id}'.",
                    exception);
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_isDisposed, this);
}

public sealed class PluginHostContextFactory(
    TimeProvider timeProvider,
    IPluginSettingsStoreFactory settingsStoreFactory,
    IPluginTuningServiceFactory tuningServiceFactory,
    IPluginAudioSinkFactory audioSinkFactory,
    IPluginMetricsRegistry metricsRegistry,
    Func<IPluginIqDispatcher> iqDispatcherFactory,
    IPluginNotificationService notificationService,
    IPluginDispatcher dispatcher,
    IRadioStateStore radioStateStore) : IPluginHostContextFactory
{
    public IPluginHostContext Create(string pluginId) =>
        new PluginHostContext(
            pluginId,
            timeProvider,
            new TracePluginLogger(pluginId),
            settingsStoreFactory.Create(pluginId),
            tuningServiceFactory.Create(pluginId),
            audioSinkFactory.Create(pluginId),
            metricsRegistry.GetOrCreate(pluginId),
            new HostPluginRuntimeDiagnostics(pluginId, iqDispatcherFactory),
            dispatcher,
            notificationService,
            new RadioStatePluginTelemetry(radioStateStore));
}

internal sealed class RadioStatePluginTelemetry(IRadioStateStore radioStateStore) : IPluginReceiverTelemetry
{
    public float SignalLevelDbm
    {
        get
        {
            // AveRxPwr is the calibrated sum of the FFT-bin powers across the
            // tuned receive bandwidth. AveFftPwr is only the center-bin value,
            // so it varies with FFT resolution and reports a peak-like level.
            float value = radioStateStore.PublishedState.AveRxPwr;
            return float.IsFinite(value) ? value : -150f;
        }
    }

    public float NoiseFloorDbm
    {
        get
        {
            float value = radioStateStore.PublishedState.Min2FftPwr;
            return float.IsFinite(value) ? value : -150f;
        }
    }

    public float CalibrationOffsetDb
    {
        get
        {
            float value = radioStateStore.PublishedState.RfCalibrationDelta;
            return float.IsFinite(value) && value != 0 ? value : -80f;
        }
    }
}

internal sealed class HostPluginRuntimeDiagnostics(
    string pluginId,
    Func<IPluginIqDispatcher> dispatcherFactory) : IPluginRuntimeDiagnostics
{
    public PluginRuntimeDiagnosticsSnapshot GetSnapshot()
    {
        PluginIqDispatchSnapshot value = dispatcherFactory().GetSnapshot(pluginId);
        return new(
            value.SubmittedBlocks,
            value.ProcessedBlocks,
            value.DroppedBlocks,
            value.DroppedSamples,
            value.QueueDepth,
            value.MaximumQueueDepth,
            value.OutstandingLeases,
            value.CurrentProcessingTimeMs,
            value.CurrentBlockDurationMs,
            value.AverageProcessingTimeMs,
            value.MaximumProcessingTimeMs,
            value.LastProcessedSequence,
            value.LastSuccessfulProcessingUtc,
            value.LastError,
            value.ProcessingStages);
    }
}

internal sealed record PluginHostContext(
    string PluginId,
    TimeProvider TimeProvider,
    IPluginLogger Logger,
    IPluginSettingsStore Settings,
    IPluginTuningService Tuning,
    IPluginAudioSink Audio,
    IPluginMetrics Metrics,
    IPluginRuntimeDiagnostics RuntimeDiagnostics,
    IPluginDispatcher Dispatcher,
    IPluginNotificationService Notifications,
    IPluginReceiverTelemetry? ReceiverTelemetry = null) : IPluginHostContext;

internal sealed class TracePluginLogger(string pluginId) : IPluginLogger
{
    public void Log(PluginLogLevel level, string eventId, string message, Exception? exception = null)
    {
        string details = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
        Trace.WriteLine($"[{level}] [{pluginId}] [{eventId}] {message}{details}");
    }
}
