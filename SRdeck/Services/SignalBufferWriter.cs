using System;
using SRdeck.DSP;

namespace SRdeck.Services;

public enum SignalInputSource
{
    Sdr,
    Playback
}

public readonly record struct SignalBlockContext(
    SignalInputSource Source,
    double PlaybackSystemDb,
    int PlaybackRfHz);

public delegate void SignalBlockCompletedHandler(
    int blockEndPointer,
    SignalBlockContext context);

public interface ISignalBufferWriter
{
    bool ResidualDcRemovalEnabled { get; set; }

    void Write(
        short[] samplesI,
        short[] samplesQ,
        int sampleCount,
        int sampleRateHz,
        SignalBlockContext context);

    void ResetBlockAccumulator();

    void ResetResidualDcRemoval();
}

public interface ISignalBufferWriterFactory
{
    ISignalBufferWriter Create(SignalBlockCompletedHandler blockCompleted);
}

public sealed class SignalBufferWriterFactory : ISignalBufferWriterFactory
{
    private readonly ISignalBufferState _bufferState;
    private readonly ISignalInputMetrics _inputMetrics;

    public SignalBufferWriterFactory(
        ISignalBufferState bufferState,
        ISignalInputMetrics inputMetrics)
    {
        _bufferState = bufferState;
        _inputMetrics = inputMetrics;
    }

    public ISignalBufferWriter Create(SignalBlockCompletedHandler blockCompleted) =>
        new SignalBufferWriter(_bufferState, _inputMetrics, blockCompleted);
}

internal sealed class SignalBufferWriter : ISignalBufferWriter
{
    private readonly ISignalBufferState _bufferState;
    private readonly ISignalInputMetrics _inputMetrics;
    private readonly SignalBlockCompletedHandler _blockCompleted;
    private readonly ResidualDcRemovalProcessor _residualDcRemoval = new();
    private readonly object _residualDcRemovalGate = new();
    private short[] _processedI = Array.Empty<short>();
    private short[] _processedQ = Array.Empty<short>();
    private long _blockAccumulator;
    private bool _residualDcRemovalEnabled;

    public SignalBufferWriter(
        ISignalBufferState bufferState,
        ISignalInputMetrics inputMetrics,
        SignalBlockCompletedHandler blockCompleted)
    {
        _bufferState = bufferState;
        _inputMetrics = inputMetrics;
        _blockCompleted = blockCompleted ?? throw new ArgumentNullException(nameof(blockCompleted));
    }

    public bool ResidualDcRemovalEnabled
    {
        get
        {
            lock (_residualDcRemovalGate) return _residualDcRemovalEnabled;
        }
        set
        {
            lock (_residualDcRemovalGate)
            {
                if (_residualDcRemovalEnabled == value) return;
                _residualDcRemovalEnabled = value;
                _residualDcRemoval.Reset();
            }
        }
    }

    public void Write(
        short[] samplesI,
        short[] samplesQ,
        int sampleCount,
        int sampleRateHz,
        SignalBlockContext context)
    {
        if (sampleCount <= 0) return;

        bool residualDcRemovalEnabled;
        lock (_residualDcRemovalGate)
        {
            residualDcRemovalEnabled = _residualDcRemovalEnabled;
            if (residualDcRemovalEnabled)
            {
                EnsureProcessedBufferCapacity(sampleCount);
                _residualDcRemoval.Process(
                    samplesI.AsSpan(0, sampleCount),
                    samplesQ.AsSpan(0, sampleCount),
                    _processedI.AsSpan(0, sampleCount),
                    _processedQ.AsSpan(0, sampleCount),
                    sampleRateHz);
                samplesI = _processedI;
                samplesQ = _processedQ;
            }
        }

        WriteCore(samplesI, samplesQ, sampleCount, sampleRateHz, context);
    }

    private void WriteCore(
        short[] samplesI,
        short[] samplesQ,
        int sampleCount,
        int sampleRateHz,
        SignalBlockContext context)
    {
        long blockSize = Math.Max(1L, sampleRateHz / 10L);
        int sourceOffset = 0;
        while (sourceOffset < sampleCount)
        {
            if (_blockAccumulator >= blockSize)
            {
                _blockAccumulator = 0;
                _blockCompleted(_bufferState.WritePointer, context);
            }

            int untilRingEnd = _bufferState.BufferSize - _bufferState.WritePointer;
            int untilBlockEnd = (int)Math.Min(int.MaxValue, blockSize - _blockAccumulator);
            int chunkSize = Math.Min(
                sampleCount - sourceOffset,
                Math.Min(untilRingEnd, untilBlockEnd));

            ReadOnlySpan<short> sourceI = samplesI.AsSpan(sourceOffset, chunkSize);
            ReadOnlySpan<short> sourceQ = samplesQ.AsSpan(sourceOffset, chunkSize);
            _bufferState.WritePointer = _bufferState.IqBuffer.Write(
                _bufferState.WritePointer,
                sourceI,
                sourceQ);
            _inputMetrics.ExtendExtrema(sourceI, sourceQ);

            sourceOffset += chunkSize;
            _bufferState.TotalSamplesReceived += chunkSize;
            _blockAccumulator += chunkSize;
            if (_blockAccumulator == blockSize)
            {
                _blockAccumulator = 0;
                _blockCompleted(_bufferState.WritePointer, context);
            }
        }

        _inputMetrics.TrackSamples((uint)sampleCount);
    }

    private void EnsureProcessedBufferCapacity(int sampleCount)
    {
        if (_processedI.Length < sampleCount) Array.Resize(ref _processedI, sampleCount);
        if (_processedQ.Length < sampleCount) Array.Resize(ref _processedQ, sampleCount);
    }

    public void ResetBlockAccumulator()
    {
        _blockAccumulator = 0;
        ResetResidualDcRemoval();
    }

    public void ResetResidualDcRemoval()
    {
        lock (_residualDcRemovalGate) _residualDcRemoval.Reset();
    }
}
