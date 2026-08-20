using System;
using SRdeck.DSP;
using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.Services;

public interface ISignalPipeline : IDisposable
{
    bool ResidualDcRemovalEnabled { get; set; }

    int BufferSize { get; }
    IqSampleRingBuffer IqBuffer { get; }
    float[] GainHistory { get; }
    int[] FrequencyHistory { get; }
    int WritePointer { get; set; }
    int ReadPointer { get; set; }
    int CurrentReadPointer { get; set; }
    long CurrentReadAbsoluteSampleEnd { get; set; }
    int NextReadPointer { get; set; }
    long TotalSamplesReceived { get; set; }
    long InputBlockSequence { get; }
    IqSampleExtrema LastCompletedExtrema { get; }
    double EffectiveSampleRateHz { get; }

    void Start();
    void Write(
        short[] samplesI,
        short[] samplesQ,
        int sampleCount,
        int sampleRateHz,
        SignalBlockContext context);
    double Complete(SignalBlockCompletionRequest request);
    bool EnsureIqBufferCapacity(int sampleRateHz);
    bool EnsureDemodulationCapacity(
        RadioState state,
        int sampleRateHz,
        SdrDeviceCapabilities deviceCapabilities);
    int GetMaxAvailableHistorySeconds(int sampleRateHz);
    int GetGridIndex(int pointer, int samplesPerGrid);
    void ResetForRestart();
    void ResetResidualDcRemoval();
}

public interface ISignalPipelineFactory
{
    ISignalPipeline Create(
        SignalBlockCompletedHandler blockCompleted,
        Action processSignalCycle,
        Action<int> synchronizePlaybackFrequency,
        Action<double> updateSystemDb,
        IAgcManager agcManager);
}

public sealed class SignalPipelineFactory : ISignalPipelineFactory
{
    private readonly ISignalBufferState _bufferState;
    private readonly ISignalInputMetrics _inputMetrics;
    private readonly ISignalBufferManager _bufferManager;
    private readonly ISignalBufferWriterFactory _bufferWriterFactory;
    private readonly ISignalBlockCoordinatorFactory _blockCoordinatorFactory;

    public SignalPipelineFactory(
        ISignalBufferState bufferState,
        ISignalInputMetrics inputMetrics,
        ISignalBufferManager bufferManager,
        ISignalBufferWriterFactory bufferWriterFactory,
        ISignalBlockCoordinatorFactory blockCoordinatorFactory)
    {
        _bufferState = bufferState;
        _inputMetrics = inputMetrics;
        _bufferManager = bufferManager;
        _bufferWriterFactory = bufferWriterFactory;
        _blockCoordinatorFactory = blockCoordinatorFactory;
    }

    public ISignalPipeline Create(
        SignalBlockCompletedHandler blockCompleted,
        Action processSignalCycle,
        Action<int> synchronizePlaybackFrequency,
        Action<double> updateSystemDb,
        IAgcManager agcManager) =>
        new SignalPipeline(
            _bufferState,
            _inputMetrics,
            _bufferManager,
            _bufferWriterFactory.Create(blockCompleted),
            _blockCoordinatorFactory.Create(
                processSignalCycle,
                synchronizePlaybackFrequency,
                updateSystemDb,
                agcManager));
}

internal sealed class SignalPipeline : ISignalPipeline
{
    private readonly ISignalBufferState _bufferState;
    private readonly ISignalInputMetrics _inputMetrics;
    private readonly ISignalBufferManager _bufferManager;
    private readonly ISignalBufferWriter _bufferWriter;
    private readonly ISignalBlockCoordinator _blockCoordinator;

    public SignalPipeline(
        ISignalBufferState bufferState,
        ISignalInputMetrics inputMetrics,
        ISignalBufferManager bufferManager,
        ISignalBufferWriter bufferWriter,
        ISignalBlockCoordinator blockCoordinator)
    {
        _bufferState = bufferState;
        _inputMetrics = inputMetrics;
        _bufferManager = bufferManager;
        _bufferWriter = bufferWriter;
        _blockCoordinator = blockCoordinator;
    }

    public int BufferSize => _bufferState.BufferSize;
    public bool ResidualDcRemovalEnabled
    {
        get => _bufferWriter.ResidualDcRemovalEnabled;
        set => _bufferWriter.ResidualDcRemovalEnabled = value;
    }

    public IqSampleRingBuffer IqBuffer => _bufferState.IqBuffer;
    public float[] GainHistory => _bufferState.GainHistory;
    public int[] FrequencyHistory => _bufferState.FrequencyHistory;
    public int WritePointer { get => _bufferState.WritePointer; set => _bufferState.WritePointer = value; }
    public int ReadPointer { get => _bufferState.ReadPointer; set => _bufferState.ReadPointer = value; }
    public int CurrentReadPointer { get => _bufferState.CurrentReadPointer; set => _bufferState.CurrentReadPointer = value; }
    public long CurrentReadAbsoluteSampleEnd { get => _bufferState.CurrentReadAbsoluteSampleEnd; set => _bufferState.CurrentReadAbsoluteSampleEnd = value; }
    public int NextReadPointer { get => _bufferState.NextReadPointer; set => _bufferState.NextReadPointer = value; }
    public long TotalSamplesReceived { get => _bufferState.TotalSamplesReceived; set => _bufferState.TotalSamplesReceived = value; }
    public long InputBlockSequence => _bufferState.InputBlockSequence;
    public IqSampleExtrema LastCompletedExtrema => _inputMetrics.LastCompletedExtrema;
    public double EffectiveSampleRateHz => _inputMetrics.EffectiveSampleRateHz;

    public void Start() => _blockCoordinator.Start();

    public void Write(
        short[] samplesI,
        short[] samplesQ,
        int sampleCount,
        int sampleRateHz,
        SignalBlockContext context) =>
        _bufferWriter.Write(samplesI, samplesQ, sampleCount, sampleRateHz, context);

    public double Complete(SignalBlockCompletionRequest request) =>
        _blockCoordinator.Complete(request);

    public bool EnsureIqBufferCapacity(int sampleRateHz)
    {
        IqBufferCapacityResult result = _bufferManager.EnsureIqBufferCapacity(
            _bufferState.IqBuffer,
            sampleRateHz);
        if (!result.WasResized)
        {
            return false;
        }

        _bufferState.ReplaceBuffer(result.Buffer);
        _inputMetrics.ResetCurrentExtrema();
        _bufferState.ClearHistory();
        return true;
    }

    public bool EnsureDemodulationCapacity(
        RadioState state,
        int sampleRateHz,
        SdrDeviceCapabilities deviceCapabilities) =>
        _bufferManager.EnsureDemodulationCapacity(state, sampleRateHz, deviceCapabilities);

    public int GetMaxAvailableHistorySeconds(int sampleRateHz) =>
        _bufferManager.GetMaxAvailableHistorySeconds(
            _bufferState.BufferSize,
            sampleRateHz);

    public int GetGridIndex(int pointer, int samplesPerGrid) =>
        _bufferState.GetGridIndex(pointer, samplesPerGrid);

    public void ResetForRestart()
    {
        _bufferState.AlignReadPointersToWrite();
        _bufferWriter.ResetBlockAccumulator();
        _inputMetrics.ResetSampleRate();
        _bufferState.ResetInputBlockSequence();
    }

    public void ResetResidualDcRemoval() => _bufferWriter.ResetResidualDcRemoval();

    public void Dispose() => _blockCoordinator.Dispose();
}
