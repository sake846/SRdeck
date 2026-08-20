using System.Diagnostics;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

/// <summary>
/// Collects the per-plugin counters and processing-stage state exposed by the
/// dispatcher snapshot. It deliberately contains no queue or consumer logic.
/// </summary>
internal sealed class PluginDiagnosticsCollector(
    PluginProcessingStageDefinition pluginProcessingStage,
    bool hasChannelConsumer)
{
    private readonly PluginProcessingStageDefinition _pluginProcessingStage = pluginProcessingStage;
    private readonly bool _hasChannelConsumer = hasChannelConsumer;
    private long _submittedBlocks;
    private long _processedBlocks;
    private long _droppedBlocks;
    private long _droppedSamples;
    private long _processingTicks;
    private long _currentProcessingTicks;
    private long _currentBlockDurationTicks;
    private long _maximumProcessingTicks;
    private long _lastProcessedSequence = -1;
    private int _queueDepth;
    private int _maximumQueueDepth;
    private int _outstandingLeases;
    private int _pendingDiscontinuity;
    private long _lastSuccessfulUtcTicks;
    private string? _lastError;
    private long _channelizationTicks;
    private long _currentChannelizationTicks;
    private long _channelizationCount;
    private long _pluginProcessingTicks;
    private long _currentPluginProcessingTicks;
    private long _pluginProcessingCount;
    private int _channelComputeDevice;
    private string _channelBackend = "自動選択（入力待ち）";
    private string? _channelDetail;

    public PluginIqDispatchSnapshot Snapshot
    {
        get
        {
            long processed = Interlocked.Read(ref _processedBlocks);
            long utcTicks = Interlocked.Read(ref _lastSuccessfulUtcTicks);
            return new PluginIqDispatchSnapshot(
                Interlocked.Read(ref _submittedBlocks),
                processed,
                Interlocked.Read(ref _droppedBlocks),
                Interlocked.Read(ref _droppedSamples),
                Volatile.Read(ref _queueDepth),
                Volatile.Read(ref _maximumQueueDepth),
                Volatile.Read(ref _outstandingLeases),
                TicksToMilliseconds(Interlocked.Read(ref _currentProcessingTicks)),
                TicksToMilliseconds(Interlocked.Read(ref _currentBlockDurationTicks)),
                processed == 0 ? 0 : TicksToMilliseconds(Interlocked.Read(ref _processingTicks)) / processed,
                TicksToMilliseconds(Interlocked.Read(ref _maximumProcessingTicks)),
                Interlocked.Read(ref _lastProcessedSequence),
                utcTicks == 0 ? null : new DateTimeOffset(utcTicks, TimeSpan.Zero),
                Volatile.Read(ref _lastError),
                BuildProcessingStages());
        }
    }

    public bool TakePendingDiscontinuity() =>
        Interlocked.Exchange(ref _pendingDiscontinuity, 0) != 0;

    public void MarkPendingDiscontinuity() => Volatile.Write(ref _pendingDiscontinuity, 1);

    public void RegisterEnqueueStarted()
    {
        Interlocked.Increment(ref _outstandingLeases);
        int depth = Interlocked.Increment(ref _queueDepth);
        UpdateMaximum(ref _maximumQueueDepth, depth);
    }

    public void RegisterEnqueueAccepted()
    {
        Interlocked.Increment(ref _submittedBlocks);
    }

    public void RegisterEnqueueRejected(int sampleCount)
    {
        Interlocked.Decrement(ref _queueDepth);
        Interlocked.Decrement(ref _outstandingLeases);
        Volatile.Write(ref _pendingDiscontinuity, 1);
        RegisterDrop(sampleCount);
    }

    public void RegisterDisposedDrop(int sampleCount) => RegisterDrop(sampleCount);

    public void RegisterDequeued() => Interlocked.Decrement(ref _queueDepth);

    public void RegisterQueuedDrop(int sampleCount)
    {
        Interlocked.Decrement(ref _queueDepth);
        Interlocked.Decrement(ref _outstandingLeases);
        RegisterDrop(sampleCount);
    }

    public void RegisterProcessingStarted(int sampleCount, int sampleRateHz)
    {
        long blockDurationTicks = checked((long)Math.Round(
            sampleCount * (double)Stopwatch.Frequency / sampleRateHz));
        Interlocked.Exchange(ref _currentBlockDurationTicks, blockDurationTicks);
    }

    public void RegisterProcessed(long sequence, DateTimeOffset processedUtc)
    {
        Interlocked.Increment(ref _processedBlocks);
        Interlocked.Exchange(ref _lastProcessedSequence, sequence);
        Interlocked.Exchange(ref _lastSuccessfulUtcTicks, processedUtc.UtcTicks);
        Volatile.Write(ref _lastError, null);
    }

    public void RegisterError(string error) => Volatile.Write(ref _lastError, error);

    public void RegisterProcessingCompleted(long elapsedTicks)
    {
        Interlocked.Exchange(ref _currentProcessingTicks, elapsedTicks);
        Interlocked.Add(ref _processingTicks, elapsedTicks);
        UpdateMaximum(ref _maximumProcessingTicks, elapsedTicks);
        Interlocked.Decrement(ref _outstandingLeases);
    }

    public void RegisterDrop(int sampleCount)
    {
        Interlocked.Increment(ref _droppedBlocks);
        Interlocked.Add(ref _droppedSamples, sampleCount);
    }

    public void RecordChannelizationTime(long elapsedTicks)
    {
        RecordStageTime(
            ref _currentChannelizationTicks,
            ref _channelizationTicks,
            ref _channelizationCount,
            elapsedTicks);
    }

    public void RecordPluginProcessingTime(long elapsedTicks)
    {
        RecordStageTime(
            ref _currentPluginProcessingTicks,
            ref _pluginProcessingTicks,
            ref _pluginProcessingCount,
            elapsedTicks);
    }

    public void UpdateChannelizationCounters(
        int inputSampleCount,
        int outputSampleCount,
        bool reused,
        IPluginMetrics metrics)
    {
        metrics.AddCounter(
            PluginProcessingStage.Channelization,
            reused ? "shared_hits" : "computed_blocks");
        metrics.AddCounter(
            PluginProcessingStage.Channelization,
            "input_samples",
            inputSampleCount,
            "samples");
        metrics.AddCounter(
            PluginProcessingStage.Channelization,
            "output_samples",
            outputSampleCount,
            "samples");
    }

    public void UpdateChannelProcessingBackend(
        IReadOnlyList<IChannelIqBlockLease?> blocks)
    {
        string[] backends = blocks
            .Where(block => block is not null)
            .Select(block => block!.Metadata.Configuration.ProcessingBackend)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        PluginComputeDevice[] devices = backends
            .Select(ComputeDeviceFromBackend)
            .Distinct()
            .ToArray();
        PluginComputeDevice device = devices.Length switch
        {
            0 => PluginComputeDevice.Unknown,
            1 => devices[0],
            _ => PluginComputeDevice.Mixed
        };
        Volatile.Write(ref _channelComputeDevice, (int)device);
        Volatile.Write(ref _channelBackend,
            backends.Length == 0 ? "不明" : string.Join(" + ", backends));
        Volatile.Write(ref _channelDetail, string.Join(" / ", blocks
            .Where(block => block is not null)
            .Select(block =>
                $"{block!.Metadata.Configuration.RequestId}: " +
                block.Metadata.Configuration.ProcessingBackend)));
    }

    private IReadOnlyList<PluginProcessingStageSnapshot> BuildProcessingStages()
    {
        var stages = new List<PluginProcessingStageSnapshot>(2);
        if (_hasChannelConsumer)
        {
            long channelCount = Interlocked.Read(ref _channelizationCount);
            PluginComputeDevice device =
                (PluginComputeDevice)Volatile.Read(ref _channelComputeDevice);
            stages.Add(new PluginProcessingStageSnapshot(
                "チャンネル抽出・レート変換",
                device,
                Volatile.Read(ref _channelBackend),
                TicksToMilliseconds(Interlocked.Read(ref _currentChannelizationTicks)),
                TicksToMilliseconds(Interlocked.Read(ref _channelizationTicks)) /
                    Math.Max(channelCount, 1),
                channelCount,
                Volatile.Read(ref _channelDetail)));
        }

        long pluginCount = Interlocked.Read(ref _pluginProcessingCount);
        stages.Add(new PluginProcessingStageSnapshot(
            _pluginProcessingStage.Operation,
            _pluginProcessingStage.Device,
            _pluginProcessingStage.Backend,
            TicksToMilliseconds(Interlocked.Read(ref _currentPluginProcessingTicks)),
            pluginCount == 0
                ? 0
                : TicksToMilliseconds(Interlocked.Read(ref _pluginProcessingTicks)) / pluginCount,
            pluginCount,
            _pluginProcessingStage.Detail));
        return stages;
    }

    private static PluginComputeDevice ComputeDeviceFromBackend(string backend)
    {
        if (backend.StartsWith("gpu", StringComparison.OrdinalIgnoreCase))
            return PluginComputeDevice.Gpu;
        if (backend.StartsWith("cpu", StringComparison.OrdinalIgnoreCase))
            return PluginComputeDevice.Cpu;
        return PluginComputeDevice.Unknown;
    }

    private static void RecordStageTime(
        ref long currentTicks,
        ref long totalTicks,
        ref long count,
        long elapsedTicks)
    {
        Interlocked.Exchange(ref currentTicks, elapsedTicks);
        Interlocked.Add(ref totalTicks, elapsedTicks);
        Interlocked.Increment(ref count);
    }

    private static double TicksToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static void UpdateMaximum(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
