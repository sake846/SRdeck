using SRdeck.DSP;
using SRdeck.Models;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services;

public readonly record struct SignalBlockRuntimeSnapshot(
    double CurrentSystemDb,
    float SystemGainOffset,
    int PlaybackFallbackRfHz,
    int SdrCenterFrequencyHz,
    bool IsRfAgcEnabled,
    SdrDeviceCapabilities DeviceCapabilities,
    int MinimumGain,
    int MaximumGain);

public readonly record struct SignalBlockCompletionRequest(
    int BlockEndPointer,
    SignalBlockContext Context,
    SignalBlockRuntimeSnapshot Runtime);

public readonly record struct SignalBlockQueueSnapshot(
    int PendingBlocks, long DroppedBlocks, long DroppedSamples, long Generation);

public interface ISignalBlockCoordinator : IDisposable
{
    void Start();
    double Complete(SignalBlockCompletionRequest request);
    void Reset(Action? resetState = null);
    SignalBlockQueueSnapshot QueueSnapshot { get; }
    ReadOnlyMemory<short> CurrentSamplesI { get; }
    ReadOnlyMemory<short> CurrentSamplesQ { get; }
}

public interface ISignalBlockCoordinatorFactory
{
    ISignalBlockCoordinator Create(
        Action processSignalCycle,
        Action<int> synchronizePlaybackFrequency,
        Action<double> updateSystemDb,
        IAgcManager agcManager);
}

public sealed class SignalBlockCoordinatorFactory : ISignalBlockCoordinatorFactory
{
    private readonly ISignalBufferState _bufferState;
    private readonly ISignalInputMetrics _inputMetrics;
    private readonly ISignalProcessingWorkerFactory _processingWorkerFactory;

    public SignalBlockCoordinatorFactory(
        ISignalBufferState bufferState,
        ISignalInputMetrics inputMetrics,
        ISignalProcessingWorkerFactory processingWorkerFactory)
    {
        _bufferState = bufferState;
        _inputMetrics = inputMetrics;
        _processingWorkerFactory = processingWorkerFactory;
    }

    public ISignalBlockCoordinator Create(
        Action processSignalCycle,
        Action<int> synchronizePlaybackFrequency,
        Action<double> updateSystemDb,
        IAgcManager agcManager) =>
        new SignalBlockCoordinator(
            _bufferState,
            _inputMetrics,
            agcManager,
            _processingWorkerFactory,
            processSignalCycle,
            synchronizePlaybackFrequency,
            updateSystemDb);
}

internal sealed class SignalBlockCoordinator : ISignalBlockCoordinator
{
    internal const int MaximumPendingBlocks = 64;
    private readonly ISignalBufferState _bufferState;
    private readonly ISignalInputMetrics _inputMetrics;
    private readonly IAgcManager _agcManager;
    private readonly ISignalProcessingWorker _processingWorker;
    private readonly Action _processSignalCycle;
    private readonly Queue<CompletedBlock> _pendingBlocks = new();
    private readonly object _processingGate = new();
    private short[] _samplesI = [];
    private short[] _samplesQ = [];
    private int _currentSampleCount;
    private long _generation;
    private long _droppedBlocks;
    private long _droppedSamples;
    private bool _pendingDiscontinuity;
    private bool _disposed;
    private readonly Action<int> _synchronizePlaybackFrequency;
    private readonly Action<double> _updateSystemDb;
    public ReadOnlyMemory<short> CurrentSamplesI => _samplesI.AsMemory(0, _currentSampleCount);
    public ReadOnlyMemory<short> CurrentSamplesQ => _samplesQ.AsMemory(0, _currentSampleCount);

    private readonly record struct CompletedBlock(
        IqSampleRingBuffer Buffer, int Pointer, long AbsoluteSampleEnd,
        SignalBlockContext Context, long Generation)
    {
        public int SampleCount => Math.Max(1, Context.SampleRateHz / 10);
    }

    public SignalBlockQueueSnapshot QueueSnapshot
    {
        get
        {
            lock (_bufferState.SyncRoot)
                return new(_pendingBlocks.Count, _droppedBlocks, _droppedSamples, _generation);
        }
    }

    public SignalBlockCoordinator(
        ISignalBufferState bufferState,
        ISignalInputMetrics inputMetrics,
        IAgcManager agcManager,
        ISignalProcessingWorkerFactory processingWorkerFactory,
        Action processSignalCycle,
        Action<int> synchronizePlaybackFrequency,
        Action<double> updateSystemDb)
    {
        _bufferState = bufferState;
        _inputMetrics = inputMetrics;
        _agcManager = agcManager;
        _processSignalCycle = processSignalCycle;
        _processingWorker = processingWorkerFactory.Create(ProcessPendingBlocks);
        _synchronizePlaybackFrequency = synchronizePlaybackFrequency;
        _updateSystemDb = updateSystemDb;
    }

    public void Start() => _processingWorker.Start();

    public double Complete(SignalBlockCompletionRequest request)
    {
        lock (_bufferState.SyncRoot)
        {
            if (_disposed) return request.Runtime.CurrentSystemDb;
            return CompleteLocked(request);
        }
    }

    private double CompleteLocked(SignalBlockCompletionRequest request)
    {
        int samplesPerGrid = Math.Max(1, request.Context.SampleRateHz / 10);
        int historyIndex = _bufferState.GetGridIndex(request.BlockEndPointer, samplesPerGrid);
        SignalBlockRuntimeSnapshot runtime = request.Runtime;
        double systemDb = runtime.CurrentSystemDb;

        if (request.Context.Source == SignalInputSource.Playback)
        {
            _bufferState.GainHistory[historyIndex] = (float)request.Context.PlaybackSystemDb;
            _bufferState.FrequencyHistory[historyIndex] = request.Context.PlaybackRfHz > 0
                ? request.Context.PlaybackRfHz
                : runtime.PlaybackFallbackRfHz;
            systemDb = request.Context.PlaybackSystemDb + runtime.SystemGainOffset;
            _updateSystemDb(systemDb);
            _synchronizePlaybackFrequency(request.Context.PlaybackRfHz);
        }
        else
        {
            _bufferState.GainHistory[historyIndex] = (float)runtime.CurrentSystemDb;
            _bufferState.FrequencyHistory[historyIndex] = runtime.SdrCenterFrequencyHz;
            if (runtime.IsRfAgcEnabled)
            {
                _agcManager.EvaluateManualGain(
                    _inputMetrics.CurrentExtrema,
                    runtime.DeviceCapabilities,
                    runtime.MinimumGain,
                    runtime.MaximumGain);
            }
        }

        _inputMetrics.CompleteBlock();
        int completedBlockStartPointer = _bufferState.NextReadPointer;
        _bufferState.PrepareCompletedBlock(request.BlockEndPointer);
        int capacity = Math.Clamp(_bufferState.BufferSize / samplesPerGrid, 1, MaximumPendingBlocks);
        while (_pendingBlocks.Count >= capacity)
            RegisterDrop(_pendingBlocks.Dequeue());
        SignalBlockContext context = request.Context with
        {
            CenterFrequencyHz = request.Context.CenterFrequencyHz != 0
                ? request.Context.CenterFrequencyHz
                : request.Context.Source == SignalInputSource.Playback
                    ? _bufferState.FrequencyHistory[historyIndex]
                    : runtime.SdrCenterFrequencyHz
        };
        _pendingBlocks.Enqueue(new CompletedBlock(
            _bufferState.IqBuffer, completedBlockStartPointer,
            _bufferState.TotalSamplesReceived, context, _generation));
        _bufferState.CommitReadPointer();
        _processingWorker.Signal();
        return systemDb;
    }

    private void ProcessPendingBlocks()
    {
        while (true)
        {
            lock (_processingGate)
            {
                CompletedBlock completed;
                lock (_bufferState.SyncRoot)
                {
                    if (_disposed || !_pendingBlocks.TryDequeue(out completed)) return;
                    long start = completed.AbsoluteSampleEnd - completed.SampleCount;
                    long latestEnd = _bufferState.TotalSamplesReceived;
                    if (completed.Generation != _generation ||
                        !ReferenceEquals(completed.Buffer, _bufferState.IqBuffer) ||
                        start < 0 || latestEnd < completed.AbsoluteSampleEnd ||
                        latestEnd - start > completed.Buffer.Capacity)
                    {
                        RegisterDrop(completed);
                        continue;
                    }

                    if (_samplesI.Length < completed.SampleCount)
                    {
                        _samplesI = new short[completed.SampleCount];
                        _samplesQ = new short[completed.SampleCount];
                    }
                    completed.Buffer.CopyTo(completed.Pointer, _samplesI, _samplesQ, 0, completed.SampleCount);
                    _currentSampleCount = completed.SampleCount;
                    _bufferState.CurrentReadPointer = completed.Pointer;
                    _bufferState.CurrentReadAbsoluteSampleEnd = completed.AbsoluteSampleEnd;
                    _bufferState.CurrentReadContext = completed.Context with
                    {
                        Discontinuity = completed.Context.Discontinuity |
                            (_pendingDiscontinuity ? IqDiscontinuity.SamplesDropped : IqDiscontinuity.None)
                    };
                    _pendingDiscontinuity = false;
                }
                // Reusable copies stay valid for the entire cycle. Neither a
                // wrapping producer nor a restart can change the input now.
                try { _processSignalCycle(); }
                catch
                {
                    lock (_bufferState.SyncRoot)
                    {
                        RegisterDrop(completed);
                        if (_pendingBlocks.Count > 0) _processingWorker.Signal();
                    }
                    throw;
                }
            }
        }
    }

    private void RegisterDrop(CompletedBlock block)
    {
        _droppedBlocks++;
        _droppedSamples += block.SampleCount;
        _pendingDiscontinuity = true;
    }

    public void Reset(Action? resetState = null)
    {
        lock (_processingGate)
        lock (_bufferState.SyncRoot)
        {
            _generation++;
            while (_pendingBlocks.TryDequeue(out CompletedBlock block)) RegisterDrop(block);
            _bufferState.CurrentReadContext = default;
            _currentSampleCount = 0;
            _pendingDiscontinuity = true;
            resetState?.Invoke();
        }
    }

    public void Dispose()
    {
        lock (_processingGate)
        lock (_bufferState.SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            Reset();
        }
        _processingWorker.Dispose();
    }
}
