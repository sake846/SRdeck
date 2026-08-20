using System;
using System.IO;
using System.Text;

namespace SRdeck.Audio;

internal sealed class PcmWaveFileWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private bool _disposed;
    private long _dataLength;

    public PcmWaveFileWriter(string filePath, PcmWaveFormat format)
    {
        _stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        WaveFormat = format;
        WriteHeader();
    }

    public PcmWaveFormat WaveFormat { get; }

    public void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer.Write(buffer, offset, count);
        _dataLength += count;
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writer.Flush();
        _stream.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FinalizeHeader();
        _writer.Dispose();
        _stream.Dispose();
    }

    private void WriteHeader()
    {
        _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        _writer.Write(0);
        _writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        _writer.Write(Encoding.ASCII.GetBytes("fmt "));
        _writer.Write(16);
        _writer.Write((short)1);
        _writer.Write((short)WaveFormat.Channels);
        _writer.Write(WaveFormat.SampleRate);
        _writer.Write(WaveFormat.AverageBytesPerSecond);
        _writer.Write(WaveFormat.BlockAlign);
        _writer.Write(WaveFormat.BitsPerSample);
        _writer.Write(Encoding.ASCII.GetBytes("data"));
        _writer.Write(0);
    }

    private void FinalizeHeader()
    {
        _writer.Flush();
        if (_dataLength > uint.MaxValue - 36L)
        {
            throw new InvalidOperationException("WAV data exceeds the RIFF 32-bit size limit.");
        }

        _stream.Position = 4;
        _writer.Write((uint)(36 + _dataLength));
        _stream.Position = 40;
        _writer.Write((uint)_dataLength);
        _writer.Flush();
    }
}
