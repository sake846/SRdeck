using System;
using System.Buffers;
using SRdeck.Audio;

namespace SRdeck.Models;

internal sealed class PlaybackIqBlock : IDisposable
{
    private short[]? _samplesI;
    private short[]? _samplesQ;

    public PlaybackIqBlock(short[] samplesI, short[] samplesQ, int sampleCount, double systemGainDb, int rfFrequencyHz, string? previousFileName, string? currentFileName)
    {
        _samplesI = samplesI;
        _samplesQ = samplesQ;
        SampleCount = sampleCount;
        SystemGainDb = systemGainDb;
        RfFrequencyHz = rfFrequencyHz;
        PreviousFileName = previousFileName;
        CurrentFileName = currentFileName;
    }

    public short[] ISamples => _samplesI ?? throw new ObjectDisposedException(nameof(PlaybackIqBlock));
    public short[] QSamples => _samplesQ ?? throw new ObjectDisposedException(nameof(PlaybackIqBlock));
    public int SampleCount { get; }
    public double SystemGainDb { get; }
    public int RfFrequencyHz { get; }
    public string? PreviousFileName { get; }
    public string? CurrentFileName { get; }
    public bool DidFileChange => PreviousFileName != CurrentFileName;

    public void Dispose()
    {
        short[]? samplesI = _samplesI;
        short[]? samplesQ = _samplesQ;
        _samplesI = null;
        _samplesQ = null;

        if (samplesI != null) ArrayPool<short>.Shared.Return(samplesI, clearArray: false);
        if (samplesQ != null) ArrayPool<short>.Shared.Return(samplesQ, clearArray: false);
    }
}

internal static class PlaybackIqBlockReader
{
    public static PlaybackIqBlock? TryRead(IAudioFileReader reader, int fallbackRfFrequencyHz)
    {
        byte[] rawBuffer = reader.GetDefaultBuffer();
        int bytesRead = reader.Read(rawBuffer);
        if (bytesRead <= 0) return null;

        int samplesRead = bytesRead / 4;
        short[] samplesI = ArrayPool<short>.Shared.Rent(samplesRead);
        short[] samplesQ = ArrayPool<short>.Shared.Rent(samplesRead);
        string? previousFileName = reader.CurrentFileName;
        try
        {
            for (int sampleIndex = 0; sampleIndex < samplesRead; sampleIndex++)
            {
                samplesI[sampleIndex] = BitConverter.ToInt16(rawBuffer, sampleIndex * 4);
                samplesQ[sampleIndex] = BitConverter.ToInt16(rawBuffer, sampleIndex * 4 + 2);
                reader.AdvanceSampleIndex(1);
            }

            // The playback reader buffer is sized to 100ms, matching the metadata
            // granularity written by WavFileRecorder.
            double systemDb = reader.CurrentSystemGainDb;
            int rfFrequencyHz = reader.CurrentRfFrequencyHz;
            if (rfFrequencyHz <= 0) rfFrequencyHz = fallbackRfFrequencyHz;

            return new PlaybackIqBlock(
                samplesI,
                samplesQ,
                samplesRead,
                systemDb,
                rfFrequencyHz,
                previousFileName,
                reader.CurrentFileName);
        }
        catch
        {
            ArrayPool<short>.Shared.Return(samplesI, clearArray: false);
            ArrayPool<short>.Shared.Return(samplesQ, clearArray: false);
            throw;
        }
    }
}
