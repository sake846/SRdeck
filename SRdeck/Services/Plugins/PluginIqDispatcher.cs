using SRdeckPlugin.Contracts;
using SRdeck.DSP;

namespace SRdeck.Services.Plugins;

public readonly record struct PluginIqPublishRequest(
    IqSampleRingBuffer Buffer,
    int BlockStartPointer,
    int SampleCount,
    int SampleRateHz,
    long CenterFrequencyHz,
    long AbsoluteSampleEnd,
    SignalInputSource InputSource);

public readonly record struct PluginIqDispatchSnapshot(
    long SubmittedBlocks,
    long ProcessedBlocks,
    long DroppedBlocks,
    long DroppedSamples,
    int QueueDepth,
    int MaximumQueueDepth,
    int OutstandingLeases,
    double CurrentProcessingTimeMs,
    double CurrentBlockDurationMs,
    double AverageProcessingTimeMs,
    double MaximumProcessingTimeMs,
    long LastProcessedSequence,
    DateTimeOffset? LastSuccessfulProcessingUtc,
    string? LastError,
    IReadOnlyList<PluginProcessingStageSnapshot> ProcessingStages);

public interface IPluginIqDispatcher : IDisposable
{
    bool TryPublish(PluginIqPublishRequest request);
    bool WarmUpActiveChannels(int sampleRateHz, long centerFrequencyHz);
    void ResetStream();
    PluginIqDispatchSnapshot GetSnapshot(string pluginId);
    void SetWorkloadAccelerationPreferences(
        PluginChannelAccelerationPreference light,
        PluginChannelAccelerationPreference standard,
        PluginChannelAccelerationPreference heavy);
}

/// <summary>
/// Compatibility facade for IQ publication. Planning, processing, worker
/// execution and diagnostics live in internal implementation units.
/// </summary>
public sealed class PluginIqDispatcher : IPluginIqDispatcher
{
    private const int MinimumQueueCapacity = 1;
    private const int MaximumQueueCapacity = 32;

    private readonly ChannelProcessorRegistry _channelRegistry;
    private readonly Dictionary<string, IqDispatchWorker> _workers;
    private readonly IqDispatchPlanner _planner;
    private bool _disposed;

    internal int ChannelWarmUpExecutionCount => _planner.ChannelWarmUpExecutionCount;

    public static string NormalizationBackend =>
        System.Runtime.Intrinsics.X86.Sse2.IsSupported &&
        System.Runtime.Intrinsics.X86.Sse.IsSupported
            ? "cpu-simd-128"
            : "cpu-scalar";

    public PluginIqDispatcher(
        IReadOnlyList<IPluginModule> modules,
        IPluginManager pluginManager,
        TimeProvider timeProvider,
        IPluginMetricsRegistry? metricsRegistry = null)
        : this(modules, pluginManager, timeProvider, metricsRegistry, null)
    {
    }

    internal PluginIqDispatcher(
        IReadOnlyList<IPluginModule> modules,
        IPluginManager pluginManager,
        TimeProvider timeProvider,
        IPluginMetricsRegistry? metricsRegistry,
        IStandardChannelGpuBackend? gpuBackend)
    {
        _channelRegistry = new ChannelProcessorRegistry(gpuBackend);
        _workers = modules
            .Where(module => module is IIqBlockConsumer or IPluginChannelBlockConsumer)
            .ToDictionary(
                module => module.Descriptor.Id,
                module => new IqDispatchWorker(
                    module.Descriptor.Id,
                    module as IIqBlockConsumer,
                    module as IPluginChannelBlockConsumer,
                    module is IPluginProcessingDiagnosticsProvider processingDiagnostics
                        ? processingDiagnostics.ProcessingStage
                        : new PluginProcessingStageDefinition(
                            "方式固有処理",
                            PluginComputeDevice.Unknown,
                            "未申告",
                            "プラグインがCPU/GPU実行先を申告していません。"),
                    _channelRegistry,
                    metricsRegistry?.GetOrCreate(module.Descriptor.Id),
                    timeProvider,
                    (operation, exception) =>
                        pluginManager.ReportFault(module.Descriptor.Id, operation, exception),
                    Math.Clamp(
                        module is IPluginChannelBlockConsumer channelConsumer
                            ? channelConsumer.ChannelRequests
                                .Select(request => request.RequestedQueueCapacity)
                                .DefaultIfEmpty(module is IIqBlockConsumer raw
                                    ? raw.IqPreferences.RequestedQueueCapacity
                                    : PluginIqPreferences.Default.RequestedQueueCapacity)
                                .Max()
                            : ((IIqBlockConsumer)module).IqPreferences.RequestedQueueCapacity,
                        MinimumQueueCapacity,
                        MaximumQueueCapacity)),
                StringComparer.Ordinal);
        _planner = new IqDispatchPlanner(
            _workers,
            pluginManager,
            timeProvider,
            _channelRegistry);
    }

    public bool TryPublish(PluginIqPublishRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.SampleCount <= 0 || request.SampleRateHz <= 0 ||
            request.AbsoluteSampleEnd < request.SampleCount)
            return false;
        if (!_planner.TryCreatePlan(request, out IqDispatchPlan plan))
            return false;

        SharedIqBlockOwner shared = SharedIqBlockOwner.Create(
            request,
            plan.ActiveWorkers.Count,
            plan.BatchContextRequests,
            plan.BatchCpuParallelism);
        bool accepted = false;
        foreach (IqDispatchWorker worker in plan.ActiveWorkers)
        {
            PooledIqBlockLease lease = shared.CreateLease(plan.Metadata);
            if (worker.TryEnqueue(lease)) accepted = true;
            else lease.Dispose();
        }
        return accepted;
    }

    public bool WarmUpActiveChannels(int sampleRateHz, long centerFrequencyHz)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _planner.WarmUpActiveChannels(sampleRateHz, centerFrequencyHz);
    }

    public void ResetStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _planner.ResetStream();
        foreach (IqDispatchWorker worker in _workers.Values)
            worker.DropQueuedBlocks();
        _channelRegistry.Reset();
    }

    public PluginIqDispatchSnapshot GetSnapshot(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _workers.TryGetValue(pluginId, out IqDispatchWorker? worker)
            ? worker.Snapshot
            : default;
    }

    public void SetWorkloadAccelerationPreferences(
        PluginChannelAccelerationPreference light,
        PluginChannelAccelerationPreference standard,
        PluginChannelAccelerationPreference heavy) =>
        _planner.SetWorkloadAccelerationPreferences(light, standard, heavy);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IqDispatchWorker worker in _workers.Values)
            worker.Dispose();
    }
}
