using System.Collections.Concurrent;
using SRdeckPlugin.Contracts;
using SRdeck.DSP;

namespace SRdeck.Services.Plugins;

internal readonly record struct ChannelProcessingKey(
    long CenterFrequencyHz,
    int BandwidthHz,
    int OutputSampleRateHz,
    int MaximumIntermediateSampleRateHz,
    int MinimumIntermediateSampleRateHz,
    int FirTaps,
    int CicStages,
    int CoarseOutputMinimumSampleRateHz,
    int CoarseOutputMaximumSampleRateHz,
    int MaximumFineDecimationFactor,
    PluginChannelAccelerationPreference AccelerationPreference)
{
    public static ChannelProcessingKey From(PluginChannelRequest request) => new(
        request.CenterFrequencyHz,
        request.BandwidthHz,
        request.OutputSampleRateHz,
        request.MaximumIntermediateSampleRateHz,
        request.MinimumIntermediateSampleRateHz,
        request.FirTaps,
        request.CicStages,
        request.CoarseOutputMinimumSampleRateHz,
        request.CoarseOutputMaximumSampleRateHz,
        request.MaximumFineDecimationFactor,
        request.AccelerationPreference);
}

/// <summary>
/// Host-only extension point for a complete stateful GPU channel transform.
/// A backend must preserve block order, discontinuities, group delay and sample
/// mapping; FFT-only or display-only GPU services do not satisfy this contract.
/// </summary>
internal interface IStandardChannelGpuBackend
{
    bool IsAvailable { get; }
    bool Supports(PluginChannelRequest request, IqBlockMetadata metadata, int inputSampleCount, PluginChannelAccelerationPreference? preferenceOverride = null);
    bool ShouldUseGpuForAutomaticBatch(
        IReadOnlyList<PluginChannelRequest> requests,
        IqBlockMetadata metadata,
        int inputSampleCount,
        int cpuParallelism);
    StandardChannelProcessor.SharedChannelBlock Process(
        PluginChannelRequest request,
        IqBlockMetadata metadata,
        ReadOnlySpan<Complex32> samples);
    IReadOnlyList<StandardChannelProcessor.SharedChannelBlock> ProcessBatch(
        IReadOnlyList<PluginChannelRequest> requests,
        IqBlockMetadata metadata,
        ReadOnlyMemory<Complex32> samples);
    void Reset();
}

/// <summary>
/// Owns reusable stateful standard-channel processors and the CPU/GPU selection
/// policy used by IQ dispatch workers.
/// </summary>
internal class ChannelProcessorRegistry(IStandardChannelGpuBackend? gpuBackend = null)
{
    private readonly ReaderWriterLockSlim lifecycleLock = new();
    private readonly ConcurrentDictionary<ChannelProcessingKey, StandardChannelProcessor> processors = [];
    public PluginChannelAccelerationPreference LightAccelerationPreference { get; set; } = PluginChannelAccelerationPreference.Auto;
    public PluginChannelAccelerationPreference StandardAccelerationPreference { get; set; } = PluginChannelAccelerationPreference.Auto;
    public PluginChannelAccelerationPreference HeavyAccelerationPreference { get; set; } = PluginChannelAccelerationPreference.Auto;
    internal int ProcessorCount => processors.Count;

    public PluginChannelRequest[] PlanBatch(
        IReadOnlyList<PluginChannelRequest> requests,
        IReadOnlyList<PluginChannelRequest> batchContextRequests,
        IqBlockMetadata metadata,
        int inputSampleCount,
        int cpuParallelism)
    {
        var planned = new PluginChannelRequest[requests.Count];
        for (int index = 0; index < requests.Count; index++)
        {
            PluginChannelRequest request = requests[index];
            PluginChannelAccelerationPreference preference = ResolvePreference(
                request, metadata.SampleRateHz, inputSampleCount);
            planned[index] = request with { AccelerationPreference = preference };
        }

        PluginChannelRequest[] context = batchContextRequests
            .Concat(requests)
            .Select(request =>
            {
                PluginChannelAccelerationPreference preference = ResolvePreference(
                    request, metadata.SampleRateHz, inputSampleCount);
                return request with { AccelerationPreference = preference };
            })
            .DistinctBy(ChannelProcessingKey.From)
            .ToArray();
        PluginChannelRequest[] automatic = context.Where(request =>
            request.AccelerationPreference == PluginChannelAccelerationPreference.Auto).ToArray();
        bool hasExplicitGpu = context.Any(request =>
            request.AccelerationPreference is PluginChannelAccelerationPreference.GpuPreferred or
                PluginChannelAccelerationPreference.GpuRequired);
        bool useAutomaticGpu = !hasExplicitGpu &&
            gpuBackend is { IsAvailable: true } &&
            gpuBackend.ShouldUseGpuForAutomaticBatch(
                automatic, metadata, inputSampleCount, cpuParallelism);
        if (!useAutomaticGpu)
        {
            for (int index = 0; index < planned.Length; index++)
                if (planned[index].AccelerationPreference == PluginChannelAccelerationPreference.Auto)
                    planned[index] = planned[index] with
                    {
                        AccelerationPreference = PluginChannelAccelerationPreference.Cpu
                    };
        }
        return planned;
    }

    public StandardChannelProcessor.SharedChannelBlock Process(
        ChannelProcessingKey key,
        PluginChannelRequest request,
        IqBlockMetadata metadata,
        ReadOnlySpan<Complex32> samples)
    {
        lifecycleLock.EnterReadLock();
        try
        {
            PluginChannelAccelerationPreference preference = ResolvePreference(
                request, metadata.SampleRateHz, samples.Length);

            if (preference != PluginChannelAccelerationPreference.Cpu &&
                gpuBackend is { IsAvailable: true } &&
                gpuBackend.Supports(request, metadata, samples.Length, preference))
            {
                try
                {
                    // The native backend owns a state table and a single D3D context.
                    // Keep GPU submissions serialized while allowing independent CPU
                    // channels to execute concurrently.
                    lock (gpuBackend)
                        return gpuBackend.Process(request, metadata, samples);
                }
                catch (StandardChannelUnavailableException) when (
                    preference != PluginChannelAccelerationPreference.GpuRequired)
                {
                    // A device removal or native failure disables the backend. The same
                    // block is immediately retried through the stateful CPU processor.
                    metadata = metadata with
                    {
                        Discontinuity = metadata.Discontinuity | IqDiscontinuity.SamplesDropped
                    };
                }
            }
            StandardChannelProcessor processor = processors.GetOrAdd(
                key, _ => new StandardChannelProcessor(request));
            lock (processor)
                return processor.ProcessShared(metadata, samples);
        }
        finally
        {
            lifecycleLock.ExitReadLock();
        }
    }

    public IReadOnlyDictionary<ChannelProcessingKey, StandardChannelProcessor.SharedChannelBlock>
        ProcessBatch(
            IReadOnlyList<PluginChannelRequest> requests,
            IqBlockMetadata metadata,
            ReadOnlyMemory<Complex32> samples)
    {
        lifecycleLock.EnterReadLock();
        var results =
            new ConcurrentDictionary<ChannelProcessingKey, StandardChannelProcessor.SharedChannelBlock>();
        try
        {
            var gpuRequests = new List<PluginChannelRequest>(requests.Count);
            var cpuRequests = new List<PluginChannelRequest>(requests.Count);
            foreach (PluginChannelRequest request in requests)
            {
                PluginChannelAccelerationPreference preference = ResolvePreference(
                    request, metadata.SampleRateHz, samples.Length);
                if (preference != PluginChannelAccelerationPreference.Cpu &&
                    gpuBackend is { IsAvailable: true } &&
                    gpuBackend.Supports(request, metadata, samples.Length, preference))
                {
                    gpuRequests.Add(request);
                }
                else
                {
                    cpuRequests.Add(request);
                }
            }

            if (gpuRequests.Count > 0)
            {
                try
                {
                    IReadOnlyList<StandardChannelProcessor.SharedChannelBlock> gpuBlocks;
                    lock (gpuBackend!)
                    {
                        gpuBlocks = gpuRequests.Count == 1
                            ? [gpuBackend.Process(gpuRequests[0], metadata, samples.Span)]
                            : gpuBackend.ProcessBatch(gpuRequests, metadata, samples);
                    }
                    if (gpuBlocks.Count != gpuRequests.Count)
                    {
                        foreach (StandardChannelProcessor.SharedChannelBlock block in gpuBlocks)
                            block.Dispose();
                        throw new StandardChannelUnavailableException(
                            $"GPU batch returned {gpuBlocks.Count} blocks for " +
                            $"{gpuRequests.Count} requests.");
                    }
                    for (int index = 0; index < gpuRequests.Count; index++)
                        results[ChannelProcessingKey.From(gpuRequests[index])] = gpuBlocks[index];
                }
                catch (StandardChannelUnavailableException) when (
                    gpuRequests.All(request =>
                        request.AccelerationPreference !=
                        PluginChannelAccelerationPreference.GpuRequired))
                {
                    metadata = metadata with
                    {
                        Discontinuity =
                            metadata.Discontinuity | IqDiscontinuity.SamplesDropped
                    };
                    cpuRequests.AddRange(gpuRequests);
                }
            }

            if (cpuRequests.Count > 0)
            {
                Parallel.ForEach(cpuRequests, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(
                        cpuRequests.Count, Math.Clamp(Environment.ProcessorCount - 2, 1, 4))
                }, request =>
                {
                    ChannelProcessingKey key = ChannelProcessingKey.From(request);
                    StandardChannelProcessor processor = processors.GetOrAdd(
                        key, _ => new StandardChannelProcessor(request));
                    lock (processor)
                        results[key] = processor.ProcessShared(metadata, samples.Span);
                });
            }
            return results;
        }
        catch
        {
            foreach (StandardChannelProcessor.SharedChannelBlock block in results.Values)
                block.Dispose();
            throw;
        }
        finally
        {
            lifecycleLock.ExitReadLock();
        }
    }

    private PluginChannelAccelerationPreference ResolvePreference(
        PluginChannelRequest request,
        int inputSampleRateHz,
        int inputSampleCount)
    {
        // An plugin can explicitly reserve CPU processing when it must not contend
        // with the main FFT for the GPU. Workload preferences select acceleration
        // only for plugins that opted into automatic selection.
        if (request.AccelerationPreference != PluginChannelAccelerationPreference.Auto)
            return request.AccelerationPreference;

        GpuChannelWorkloadClass workloadClass = GpuChannelWorkloadClassifier.Classify(
            request, inputSampleRateHz, inputSampleCount);
        PluginChannelAccelerationPreference tierPreference = workloadClass switch
        {
            GpuChannelWorkloadClass.Light => LightAccelerationPreference,
            GpuChannelWorkloadClass.Heavy => HeavyAccelerationPreference,
            _ => StandardAccelerationPreference
        };
        return tierPreference;
    }

    public void Reset()
    {
        lifecycleLock.EnterWriteLock();
        try
        {
            // Keep configured CPU processors and native GPU resources alive across
            // stop/start. The next block carries StreamStarted and resets only the
            // stream history while reusing filter coefficients and GPU allocations.
            gpuBackend?.Reset();
        }
        finally
        {
            lifecycleLock.ExitWriteLock();
        }
    }
}

// Preserve the existing internal test and host injection seam while the
// implementation is named after its responsibility.
internal sealed class SharedChannelProcessorRegistry(IStandardChannelGpuBackend? gpuBackend = null)
    : ChannelProcessorRegistry(gpuBackend);
