using System;
using System.Collections.Concurrent;
using SRdeck.Models;

namespace SRdeck.Services;

public readonly record struct SignalBlockCompletionRequest(
    int BlockEndPointer,
    SignalBlockContext Context,
    int SampleRateHz,
    double CurrentSystemDb,
    float SystemGainOffset,
    int PlaybackFallbackRfHz,
    int SdrCenterFrequencyHz,
    bool IsRfAgcEnabled,
    SdrDeviceCapabilities DeviceCapabilities,
    int MinimumGain,
    int MaximumGain);

public interface ISignalBlockCoordinator : IDisposable
{
    void Start();
    double Complete(SignalBlockCompletionRequest request);
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
    private readonly ISignalBufferState _bufferState;
    private readonly ISignalInputMetrics _inputMetrics;
    private readonly IAgcManager _agcManager;
    private readonly ISignalProcessingWorker _processingWorker;
    private readonly Action _processSignalCycle;
    private readonly ConcurrentQueue<(int Pointer, long AbsoluteSampleEnd)> _pendingReadPointers = new();
    private readonly Action<int> _synchronizePlaybackFrequency;
    private readonly Action<double> _updateSystemDb;

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
        _processingWorker = processingWorkerFactory.Create(ProcessNextCompletedBlock);
        _synchronizePlaybackFrequency = synchronizePlaybackFrequency;
        _updateSystemDb = updateSystemDb;
    }

    public void Start() => _processingWorker.Start();

    public double Complete(SignalBlockCompletionRequest request)
    {
        int samplesPerGrid = Math.Max(1, request.SampleRateHz / 10);
        int historyIndex = _bufferState.GetGridIndex(request.BlockEndPointer, samplesPerGrid);
        double systemDb = request.CurrentSystemDb;

        if (request.Context.Source == SignalInputSource.Playback)
        {
            _bufferState.GainHistory[historyIndex] = (float)request.Context.PlaybackSystemDb;
            _bufferState.FrequencyHistory[historyIndex] = request.Context.PlaybackRfHz > 0
                ? request.Context.PlaybackRfHz
                : request.PlaybackFallbackRfHz;
            systemDb = request.Context.PlaybackSystemDb + request.SystemGainOffset;
            _updateSystemDb(systemDb);
            _synchronizePlaybackFrequency(request.Context.PlaybackRfHz);
        }
        else
        {
            _bufferState.GainHistory[historyIndex] = (float)request.CurrentSystemDb;
            _bufferState.FrequencyHistory[historyIndex] = request.SdrCenterFrequencyHz;
            if (request.IsRfAgcEnabled)
            {
                _agcManager.EvaluateManualGain(
                    _inputMetrics.CurrentExtrema,
                    request.DeviceCapabilities,
                    request.MinimumGain,
                    request.MaximumGain);
            }
        }

        _inputMetrics.CompleteBlock();
        int completedBlockStartPointer = _bufferState.NextReadPointer;
        _bufferState.PrepareCompletedBlock(request.BlockEndPointer);
        _pendingReadPointers.Enqueue((completedBlockStartPointer, _bufferState.TotalSamplesReceived));
        _processingWorker.Signal();
        _bufferState.CommitReadPointer();
        return systemDb;
    }

    private void ProcessNextCompletedBlock()
    {
        if (!_pendingReadPointers.TryDequeue(out (int Pointer, long AbsoluteSampleEnd) completed)) return;

        _bufferState.CurrentReadPointer = completed.Pointer;
        _bufferState.CurrentReadAbsoluteSampleEnd = completed.AbsoluteSampleEnd;
        _processSignalCycle();
    }

    public void Dispose() => _processingWorker.Dispose();
}
