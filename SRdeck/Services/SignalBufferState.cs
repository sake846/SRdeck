using System;
using SRdeck.DSP;
using SRdeck.Models;

namespace SRdeck.Services;

public interface ISignalBufferState
{
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

    void ReplaceBuffer(IqSampleRingBuffer buffer);
    void AlignReadPointersToWrite();
    void PrepareCompletedBlock(int blockEndPointer);
    void CommitReadPointer();
    int GetGridIndex(int pointer, int samplesPerGrid);
    void ResetInputBlockSequence();
    void ClearHistory();
}

public sealed class SignalBufferState : ISignalBufferState
{
    public SignalBufferState()
    {
        IqBuffer = new IqSampleRingBuffer((int)AppConstants.FULL_BW);
        BufferSize = IqBuffer.Capacity;
    }

    public int BufferSize { get; private set; }
    public IqSampleRingBuffer IqBuffer { get; private set; }
    public float[] GainHistory { get; } = new float[AppConstants.STATISTICS_GRID_SIZE];
    public int[] FrequencyHistory { get; } = new int[AppConstants.STATISTICS_GRID_SIZE];
    public int WritePointer { get; set; }
    public int ReadPointer { get; set; }
    public int CurrentReadPointer { get; set; }
    public long CurrentReadAbsoluteSampleEnd { get; set; }
    public int NextReadPointer { get; set; }
    public long TotalSamplesReceived { get; set; }
    public long InputBlockSequence { get; private set; }

    public void ReplaceBuffer(IqSampleRingBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        IqBuffer = buffer;
        BufferSize = buffer.Capacity;
        WritePointer = 0;
        TotalSamplesReceived = 0;
        CurrentReadAbsoluteSampleEnd = 0;
    }

    public void AlignReadPointersToWrite()
    {
        CurrentReadPointer = WritePointer;
        CurrentReadAbsoluteSampleEnd = TotalSamplesReceived;
        NextReadPointer = WritePointer;
        ReadPointer = WritePointer;
    }

    public void PrepareCompletedBlock(int blockEndPointer)
    {
        NextReadPointer = blockEndPointer;
        InputBlockSequence++;
    }

    public void CommitReadPointer() => ReadPointer = NextReadPointer;

    public int GetGridIndex(int pointer, int samplesPerGrid)
    {
        if (pointer < 0)
        {
            pointer = 1;
        }
        else if (pointer == 0)
        {
            pointer = BufferSize;
        }

        long index = (pointer - 1L) / Math.Max(1, samplesPerGrid);
        return (int)Math.Clamp(index, 0, GainHistory.Length - 1L);
    }

    public void ResetInputBlockSequence() => InputBlockSequence = 0;

    public void ClearHistory()
    {
        Array.Clear(GainHistory);
        Array.Clear(FrequencyHistory);
    }
}
