using System.Buffers;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Channels;
using SRdeckPlugin.Contracts;
using SRdeck.DSP;

namespace SRdeck.Services.Plugins;

/// <summary>
/// Owns one plugin's bounded IQ queue, lease lifecycle and asynchronous
/// consumption. The dispatcher only selects workers and hands them leases.
/// </summary>
internal sealed class IqDispatchWorker : IDisposable
{
    private readonly string _pluginId;
    private readonly IIqBlockConsumer? _rawConsumer;
    private readonly IPluginChannelBlockConsumer? _channelConsumer;
    private readonly ChannelProcessorRegistry _channelRegistry;
    private readonly IPluginMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string, Exception> _reportFault;
    private readonly Channel<PooledIqBlockLease> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private readonly PluginDiagnosticsCollector _diagnostics;
    private int _disposed;

    public int SnapshotChannelRequestCount =>
        _channelConsumer?.ChannelRequests.Count ?? 0;

    public IReadOnlyList<PluginChannelRequest> SnapshotChannelRequests() =>
        _channelConsumer?.ChannelRequests.ToArray() ?? [];

    public PluginIqDispatchSnapshot Snapshot => _diagnostics.Snapshot;

    public IqDispatchWorker(
        string pluginId,
        IIqBlockConsumer? rawConsumer,
        IPluginChannelBlockConsumer? channelConsumer,
        PluginProcessingStageDefinition pluginProcessingStage,
        ChannelProcessorRegistry channelRegistry,
        IPluginMetrics? metrics,
        TimeProvider timeProvider,
        Action<string, Exception> reportFault,
        int capacity)
    {
        _pluginId = pluginId;
        _rawConsumer = rawConsumer;
        _channelConsumer = channelConsumer;
        _channelRegistry = channelRegistry;
        _metrics = metrics ?? NullPluginMetrics.Instance;
        _timeProvider = timeProvider;
        _reportFault = reportFault;
        _diagnostics = new PluginDiagnosticsCollector(
            pluginProcessingStage,
            channelConsumer is not null);
        _channel = Channel.CreateBounded<PooledIqBlockLease>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessAsync);
    }

    public bool TryEnqueue(PooledIqBlockLease lease)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            _diagnostics.RegisterDisposedDrop(lease.Metadata.SampleCount);
            return false;
        }
        if (_diagnostics.TakePendingDiscontinuity())
            lease.AddDiscontinuity(IqDiscontinuity.SamplesDropped);
        _diagnostics.RegisterEnqueueStarted();
        if (!_channel.Writer.TryWrite(lease))
        {
            _diagnostics.RegisterEnqueueRejected(lease.Metadata.SampleCount);
            return false;
        }

        _diagnostics.RegisterEnqueueAccepted();
        return true;
    }

    public void DropQueuedBlocks()
    {
        while (_channel.Reader.TryRead(out PooledIqBlockLease? lease))
        {
            _diagnostics.RegisterQueuedDrop(lease.Metadata.SampleCount);
            lease.Dispose();
        }
        _diagnostics.MarkPendingDiscontinuity();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _channel.Writer.TryComplete();
        _cancellation.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        DropQueuedBlocks();
        _cancellation.Dispose();
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (PooledIqBlockLease lease in _channel.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                _diagnostics.RegisterDequeued();
                long started = Stopwatch.GetTimestamp();
                _diagnostics.RegisterProcessingStarted(
                    lease.Metadata.SampleCount,
                    lease.Metadata.SampleRateHz);
                try
                {
                    await ConsumeAsync(lease).ConfigureAwait(false);
                    _diagnostics.RegisterProcessed(
                        lease.Metadata.Sequence,
                        _timeProvider.GetUtcNow());
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _diagnostics.RegisterError($"{_pluginId}: {exception.Message}");
                    _reportFault("consume IQ", exception);
                    break;
                }
                finally
                {
                    _diagnostics.RegisterProcessingCompleted(
                        Stopwatch.GetTimestamp() - started);
                    lease.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        finally
        {
            DropQueuedBlocks();
        }
    }

    private async ValueTask ConsumeAsync(PooledIqBlockLease lease)
    {
        if (_channelConsumer is not null)
        {
            PluginChannelRequest[] requests = _channelConsumer.ChannelRequests.ToArray();
            var requestIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (PluginChannelRequest request in requests)
                if (!requestIds.Add(request.Id))
                    throw new InvalidOperationException(
                        $"Plugin '{_pluginId}' supplied duplicate channel request ID '{request.Id}'.");
            var channelBlocks = new IChannelIqBlockLease?[requests.Length];
            long channelizationStarted = Stopwatch.GetTimestamp();
            try
            {
                int maximumParallelism = Math.Min(requests.Length,
                    Math.Clamp(Environment.ProcessorCount - 2, 1, 4));
                PluginChannelRequest[] plannedRequests = _channelRegistry.PlanBatch(
                    requests, lease.BatchContextRequests, lease.Metadata,
                    lease.Samples.Length,
                    Math.Max(maximumParallelism, lease.BatchCpuParallelism));
                _cancellation.Token.ThrowIfCancellationRequested();
                IChannelIqBlockLease[] acquired = lease.AcquireChannels(
                    _channelRegistry, plannedRequests, out bool[] reused);
                for (int index = 0; index < acquired.Length; index++)
                {
                    IChannelIqBlockLease channelBlock = acquired[index];
                    channelBlocks[index] = channelBlock;
                    _diagnostics.UpdateChannelizationCounters(
                        lease.Samples.Length,
                        channelBlock.Samples.Length,
                        reused[index],
                        _metrics);
                }
                _diagnostics.UpdateChannelProcessingBackend(channelBlocks);
            }
            catch (Exception exception) when (IsStandardChannelUnavailable(exception))
            {
                foreach (IChannelIqBlockLease? channelBlock in channelBlocks) channelBlock?.Dispose();
                Array.Clear(channelBlocks);
                if (_rawConsumer is not null && requests.All(request => request.AllowRawIqFallback))
                {
                    lease.AddDiscontinuity(IqDiscontinuity.SamplesDropped);
                    _metrics.AddCounter(PluginProcessingStage.Channelization, "raw_fallbacks");
                }
                else
                {
                    _metrics.AddCounter(PluginProcessingStage.Channelization, "out_of_band_dropped");
                    return;
                }
            }
            catch
            {
                foreach (IChannelIqBlockLease? channelBlock in channelBlocks) channelBlock?.Dispose();
                throw;
            }
            finally
            {
                _diagnostics.RecordChannelizationTime(
                    Stopwatch.GetTimestamp() - channelizationStarted);
            }

            if (channelBlocks.Length > 0 && channelBlocks[0] is not null)
            {
                IChannelIqBlockLease[] completedBlocks = channelBlocks
                    .Select(channelBlock => channelBlock!).ToArray();
                try
                {
                    long pluginStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        await _channelConsumer.ConsumeChannelsAsync(
                            completedBlocks, _cancellation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _diagnostics.RecordPluginProcessingTime(
                            Stopwatch.GetTimestamp() - pluginStarted);
                    }
                }
                finally
                {
                    foreach (IChannelIqBlockLease channelBlock in completedBlocks) channelBlock.Dispose();
                }
                return;
            }
        }

        if (_rawConsumer is not null)
        {
            long pluginStarted = Stopwatch.GetTimestamp();
            try
            {
                await _rawConsumer.ConsumeAsync(lease, _cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _diagnostics.RecordPluginProcessingTime(
                    Stopwatch.GetTimestamp() - pluginStarted);
            }
            return;
        }

        throw new InvalidOperationException(
            $"Plugin '{_pluginId}' has no usable IQ consumption path.");
    }

    private static bool IsStandardChannelUnavailable(Exception exception) => exception switch
    {
        StandardChannelUnavailableException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 &&
            aggregate.InnerExceptions.All(IsStandardChannelUnavailable),
        _ => false
    };
}

internal sealed class SharedIqBlockOwner(
    IMemoryOwner<Complex32> owner,
    Memory<Complex32> samples,
    int referenceCount,
    IReadOnlyList<PluginChannelRequest> batchContextRequests,
    int batchCpuParallelism)
{
    private const float Int16NormalizationScale = 1.0f / 32768.0f;
    private IMemoryOwner<Complex32>? _owner = owner;
    private int _referenceCount = referenceCount;
    private readonly object _channelGate = new();
    private readonly Dictionary<ChannelProcessingKey, Lazy<StandardChannelProcessor.SharedChannelBlock>>
        _channels = [];

    public ReadOnlyMemory<Complex32> Samples => _owner is null
        ? ReadOnlyMemory<Complex32>.Empty
        : samples;
    public IReadOnlyList<PluginChannelRequest> BatchContextRequests { get; } =
        batchContextRequests;
    public int BatchCpuParallelism { get; } = batchCpuParallelism;

    public static SharedIqBlockOwner Create(
        PluginIqPublishRequest request,
        int referenceCount,
        IReadOnlyList<PluginChannelRequest> batchContextRequests,
        int batchCpuParallelism)
    {
        IMemoryOwner<Complex32> owner = MemoryPool<Complex32>.Shared.Rent(request.SampleCount);
        Memory<Complex32> samples = owner.Memory[..request.SampleCount];
        CopyNormalized(request.Buffer, request.BlockStartPointer, samples.Span);
        return new SharedIqBlockOwner(
            owner, samples, referenceCount, batchContextRequests, batchCpuParallelism);
    }

    public PooledIqBlockLease CreateLease(IqBlockMetadata metadata) =>
        new(metadata, this);

    public void Release()
    {
        if (Interlocked.Decrement(ref _referenceCount) == 0)
        {
            lock (_channelGate)
            {
                foreach (Lazy<StandardChannelProcessor.SharedChannelBlock> channel in _channels.Values)
                    if (channel.IsValueCreated) channel.Value.Dispose();
                _channels.Clear();
            }
            Interlocked.Exchange(ref _owner, null)?.Dispose();
        }
    }

    public IChannelIqBlockLease AcquireChannel(
        ChannelProcessorRegistry registry,
        PluginChannelRequest request,
        IqBlockMetadata metadata,
        out bool reused)
    {
        ChannelProcessingKey key = ChannelProcessingKey.From(request);
        Lazy<StandardChannelProcessor.SharedChannelBlock> channel;
        lock (_channelGate)
        {
            if (!_channels.TryGetValue(key, out channel!))
            {
                channel = new Lazy<StandardChannelProcessor.SharedChannelBlock>(
                    () => registry.Process(key, request, metadata, Samples.Span),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _channels.Add(key, channel);
                reused = false;
            }
            else reused = true;
        }
        return channel.Value.Acquire(request.Id);
    }

    public IChannelIqBlockLease[] AcquireChannels(
        ChannelProcessorRegistry registry,
        IReadOnlyList<PluginChannelRequest> requests,
        IqBlockMetadata metadata,
        out bool[] reused)
    {
        reused = new bool[requests.Count];
        var channels =
            new Lazy<StandardChannelProcessor.SharedChannelBlock>[requests.Count];
        lock (_channelGate)
        {
            var missing = new Dictionary<ChannelProcessingKey, PluginChannelRequest>();
            var newKeys = new HashSet<ChannelProcessingKey>();
            for (int index = 0; index < requests.Count; index++)
            {
                ChannelProcessingKey key = ChannelProcessingKey.From(requests[index]);
                if (_channels.TryGetValue(key, out Lazy<StandardChannelProcessor.SharedChannelBlock>? channel))
                {
                    channels[index] = channel;
                    reused[index] = true;
                }
                else if (!missing.TryAdd(key, requests[index]))
                {
                    reused[index] = true;
                }
                else
                {
                    newKeys.Add(key);
                }
            }

            if (missing.Count > 0)
            {
                var batch = new Lazy<IReadOnlyDictionary<
                    ChannelProcessingKey, StandardChannelProcessor.SharedChannelBlock>>(
                    () => registry.ProcessBatch(
                        missing.Values.ToArray(), metadata, Samples),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                foreach (ChannelProcessingKey key in newKeys)
                    _channels[key] = new Lazy<StandardChannelProcessor.SharedChannelBlock>(
                        () => batch.Value[key],
                        LazyThreadSafetyMode.ExecutionAndPublication);
            }
            for (int index = 0; index < requests.Count; index++)
                channels[index] = _channels[ChannelProcessingKey.From(requests[index])];
        }

        var leases = new IChannelIqBlockLease[requests.Count];
        try
        {
            for (int index = 0; index < requests.Count; index++)
                leases[index] = channels[index].Value.Acquire(requests[index].Id);
            return leases;
        }
        catch
        {
            foreach (IChannelIqBlockLease? lease in leases) lease?.Dispose();
            throw;
        }
    }

    private static unsafe void CopyNormalized(
        IqSampleRingBuffer source,
        int sourceOffset,
        Span<Complex32> destination)
    {
        int copied = 0;
        fixed (Complex32* destinationBase = destination)
        {
            while (copied < destination.Length)
            {
                IqSampleRingBuffer.ContiguousBlock block = source.GetContiguousBlock(
                    sourceOffset + copied, destination.Length - copied);
                fixed (short* sourceIBase = block.SamplesI)
                fixed (short* sourceQBase = block.SamplesQ)
                {
                    short* sourceI = sourceIBase + block.Offset;
                    short* sourceQ = sourceQBase + block.Offset;
                    Complex32* target = destinationBase + copied;
                    int index = 0;
                    if (Sse2.IsSupported && Sse.IsSupported)
                    {
                        Vector128<float> scale = Vector128.Create(Int16NormalizationScale);
                        float* targetFloats = (float*)target;
                        for (; index <= block.Length - 8; index += 8)
                        {
                            var (iLow, iHigh) = Vector128.Widen(Vector128.Load(sourceI + index));
                            var (qLow, qHigh) = Vector128.Widen(Vector128.Load(sourceQ + index));
                            Vector128<float> fiLow = Vector128.ConvertToSingle(iLow) * scale;
                            Vector128<float> fiHigh = Vector128.ConvertToSingle(iHigh) * scale;
                            Vector128<float> fqLow = Vector128.ConvertToSingle(qLow) * scale;
                            Vector128<float> fqHigh = Vector128.ConvertToSingle(qHigh) * scale;
                            Vector128.Store(Sse.UnpackLow(fiLow, fqLow), targetFloats + index * 2);
                            Vector128.Store(Sse.UnpackHigh(fiLow, fqLow), targetFloats + index * 2 + 4);
                            Vector128.Store(Sse.UnpackLow(fiHigh, fqHigh), targetFloats + index * 2 + 8);
                            Vector128.Store(Sse.UnpackHigh(fiHigh, fqHigh), targetFloats + index * 2 + 12);
                        }
                    }
                    for (; index < block.Length; index++)
                        target[index] = new Complex32(
                            sourceI[index] * Int16NormalizationScale,
                            sourceQ[index] * Int16NormalizationScale);
                }
                copied += block.Length;
            }
        }
    }
}

internal sealed class PooledIqBlockLease(
    IqBlockMetadata metadata,
    SharedIqBlockOwner shared) : IIqBlockLease
{
    private SharedIqBlockOwner? _shared = shared;

    public IqBlockMetadata Metadata { get; private set; } = metadata;
    public ReadOnlyMemory<Complex32> Samples => _shared?.Samples ?? ReadOnlyMemory<Complex32>.Empty;
    public IReadOnlyList<PluginChannelRequest> BatchContextRequests =>
        _shared?.BatchContextRequests ?? [];
    public int BatchCpuParallelism => _shared?.BatchCpuParallelism ?? 1;
    public IChannelIqBlockLease AcquireChannel(
        ChannelProcessorRegistry registry,
        PluginChannelRequest request,
        out bool reused) => (_shared ?? throw new ObjectDisposedException(nameof(PooledIqBlockLease)))
            .AcquireChannel(registry, request, Metadata, out reused);
    public IChannelIqBlockLease[] AcquireChannels(
        ChannelProcessorRegistry registry,
        IReadOnlyList<PluginChannelRequest> requests,
        out bool[] reused) =>
        (_shared ?? throw new ObjectDisposedException(nameof(PooledIqBlockLease)))
            .AcquireChannels(registry, requests, Metadata, out reused);

    public void AddDiscontinuity(IqDiscontinuity discontinuity) =>
        Metadata = Metadata with { Discontinuity = Metadata.Discontinuity | discontinuity };

    public void Dispose() => Interlocked.Exchange(ref _shared, null)?.Release();
}
