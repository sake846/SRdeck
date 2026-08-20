using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using SRdeckPlugin.Contracts;
using SRdeck.DSP;

namespace SRdeck.Services.Plugins;

internal readonly record struct IqDispatchPlan(
    IReadOnlyList<IqDispatchWorker> ActiveWorkers,
    IReadOnlyList<PluginChannelRequest> BatchContextRequests,
    int BatchCpuParallelism,
    IqBlockMetadata Metadata);

/// <summary>
/// Builds the immutable per-block dispatch plan and owns stream continuity
/// metadata. Channel processing itself remains in ChannelProcessorRegistry.
/// </summary>
internal sealed class IqDispatchPlanner(
    IReadOnlyDictionary<string, IqDispatchWorker> workers,
    IPluginManager pluginManager,
    TimeProvider timeProvider,
    ChannelProcessorRegistry channelRegistry)
{
    private readonly IReadOnlyDictionary<string, IqDispatchWorker> _workers = workers;
    private readonly IPluginManager _pluginManager = pluginManager;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ChannelProcessorRegistry _channelRegistry = channelRegistry;
    private readonly ConcurrentDictionary<string, ChannelWarmupState> _channelWarmups =
        new(StringComparer.Ordinal);
    private readonly object _streamGate = new();
    private Guid _streamId = Guid.NewGuid();
    private long _generation = 1;
    private long _nextSequence;
    private int _lastSampleRateHz;
    private long _lastCenterFrequencyHz;

    internal int ChannelWarmUpExecutionCount { get; private set; }

    public bool TryCreatePlan(PluginIqPublishRequest request, out IqDispatchPlan plan)
    {
        IqDispatchWorker[] activeWorkers = _pluginManager.StreamingPluginIds
            .Select(pluginId => _workers.TryGetValue(pluginId, out IqDispatchWorker? worker)
                ? worker
                : null)
            .Where(worker => worker is not null)
            .Cast<IqDispatchWorker>()
            .ToArray();
        if (activeWorkers.Length == 0)
        {
            plan = default;
            return false;
        }

        PluginChannelRequest[] batchContextRequests = activeWorkers
            .SelectMany(worker => worker.SnapshotChannelRequests())
            .DistinctBy(ChannelProcessingKey.From)
            .ToArray();
        int batchCpuParallelism = Math.Min(
            batchContextRequests.Length,
            Math.Max(1, Math.Min(
                Math.Max(Environment.ProcessorCount - 2, 1),
                activeWorkers.Sum(worker => Math.Min(
                    worker.SnapshotChannelRequestCount,
                    Math.Clamp(Environment.ProcessorCount - 2, 1, 4))))));

        IqBlockMetadata metadata;
        lock (_streamGate)
        {
            IqDiscontinuity discontinuity = IqDiscontinuity.None;
            if (Interlocked.Exchange(ref _streamStarted, 0) != 0)
                discontinuity |= IqDiscontinuity.StreamStarted;
            if (_lastSampleRateHz != 0 && _lastSampleRateHz != request.SampleRateHz)
                discontinuity |= IqDiscontinuity.SampleRateChanged;
            if (_lastCenterFrequencyHz != 0 && _lastCenterFrequencyHz != request.CenterFrequencyHz)
                discontinuity |= IqDiscontinuity.TuningChanged;

            if ((discontinuity & (IqDiscontinuity.SampleRateChanged | IqDiscontinuity.TuningChanged)) != 0)
            {
                _generation++;
                _nextSequence = 0;
            }

            _lastSampleRateHz = request.SampleRateHz;
            _lastCenterFrequencyHz = request.CenterFrequencyHz;
            long sequence = ++_nextSequence;
            metadata = new IqBlockMetadata(
                _streamId,
                _generation,
                sequence,
                request.AbsoluteSampleEnd - request.SampleCount,
                Stopwatch.GetTimestamp(),
                _timeProvider.GetUtcNow(),
                request.SampleRateHz,
                request.CenterFrequencyHz,
                request.SampleCount,
                request.InputSource == SignalInputSource.Sdr ? IqInputSource.Sdr : IqInputSource.Playback,
                discontinuity);
        }

        plan = new IqDispatchPlan(
            activeWorkers,
            batchContextRequests,
            batchCpuParallelism,
            metadata);
        return true;
    }

    public void SetWorkloadAccelerationPreferences(
        PluginChannelAccelerationPreference light,
        PluginChannelAccelerationPreference standard,
        PluginChannelAccelerationPreference heavy)
    {
        _channelRegistry.LightAccelerationPreference = light;
        _channelRegistry.StandardAccelerationPreference = standard;
        _channelRegistry.HeavyAccelerationPreference = heavy;
        _channelWarmups.Clear();
    }

    public bool WarmUpActiveChannels(int sampleRateHz, long centerFrequencyHz)
    {
        if (sampleRateHz <= 0) return false;
        string? pluginId = _pluginManager.ActivePluginId;
        if (pluginId is null || !_workers.TryGetValue(pluginId, out IqDispatchWorker? worker))
            return false;

        PluginChannelRequest[] requests = worker.SnapshotChannelRequests().ToArray();
        if (requests.Length == 0) return false;
        var requestedState = new ChannelWarmupState(
            sampleRateHz, centerFrequencyHz, requests);
        if (_channelWarmups.TryGetValue(pluginId, out ChannelWarmupState? existingState) &&
            existingState.Matches(requestedState))
        {
            return true;
        }

        int sampleCount = Math.Clamp(sampleRateHz / 10, 4_096, 1_000_000);
        Complex32[] samples = ArrayPool<Complex32>.Shared.Rent(sampleCount);
        try
        {
            ChannelWarmUpExecutionCount++;
            samples.AsSpan(0, sampleCount).Clear();
            var metadata = new IqBlockMetadata(
                Guid.NewGuid(),
                0,
                0,
                0,
                Stopwatch.GetTimestamp(),
                _timeProvider.GetUtcNow(),
                sampleRateHz,
                centerFrequencyHz,
                sampleCount,
                IqInputSource.Playback,
                IqDiscontinuity.StreamStarted);
            int maximumParallelism = Math.Min(
                requests.Length, Math.Clamp(Environment.ProcessorCount - 2, 1, 4));
            PluginChannelRequest[] planned = _channelRegistry.PlanBatch(
                requests, requests, metadata, sampleCount, maximumParallelism);
            IReadOnlyDictionary<ChannelProcessingKey, StandardChannelProcessor.SharedChannelBlock>
                blocks = _channelRegistry.ProcessBatch(
                    planned, metadata, samples.AsMemory(0, sampleCount));
            foreach (StandardChannelProcessor.SharedChannelBlock block in blocks.Values)
                block.Dispose();

            // Leave coefficients and GPU allocations cached, but make the first
            // real IQ block start with clean phase/history state.
            _channelRegistry.Reset();
            _channelWarmups[pluginId] = requestedState;
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[PluginIqDispatcher] Channel warm-up skipped: {exception.Message}");
            return false;
        }
        finally
        {
            ArrayPool<Complex32>.Shared.Return(samples);
        }
    }

    public void ResetStream()
    {
        lock (_streamGate)
        {
            _streamId = Guid.NewGuid();
            _generation++;
            _nextSequence = 0;
            _lastSampleRateHz = 0;
            _lastCenterFrequencyHz = 0;
            Volatile.Write(ref _streamStarted, 1);
        }
    }

    private int _streamStarted = 1;

    private sealed record ChannelWarmupState(
        int SampleRateHz,
        long CenterFrequencyHz,
        PluginChannelRequest[] Requests)
    {
        public bool Matches(ChannelWarmupState other) =>
            SampleRateHz == other.SampleRateHz &&
            CenterFrequencyHz == other.CenterFrequencyHz &&
            Requests.AsSpan().SequenceEqual(other.Requests);
    }
}
