using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SRdeck.Audio;

/// <summary>
/// WAVファイルの再生および付帯情報 (.gain) からゲイン・周波数履歴を読み出して再生状況を管理するクラスです。
/// </summary>
public class WavFilePlayer : IAudioFileReader
{
    private const int GainRecordSize = sizeof(long) + sizeof(float) + sizeof(int);

    private PcmWaveFileReader? _reader;
    private byte[] _buffer = new byte[3200000];
    private readonly List<(long sampleIndex, float SystemGainDb, int CenterFrequencyHz)> _gainLog = new();
    private int _gainLogIndex;
    private long _currentSampleIndex;

    private const int MaxSequenceFiles = 99;
    private const int SequencePatternLength = 3; // "-01" など
    private const int SequenceNumberLength = 2;  // "01" など

    public string? CurrentFileName { get; private set; }
    public bool IsPlaying { get; private set; }
    public double CurrentSystemGainDb { get; private set; }
    public int CurrentRfFrequencyHz { get; private set; }
    public int CurrentSampleRateHz => _reader?.WaveFormat.SampleRate ?? 0;

    /// <summary>
    /// 指定されたWAVファイルを開き、再生の準備を行います。付随する.gainファイルからの情報も読み込みます。
    /// </summary>
    /// <param name="fileName">対象となるWAVファイルのパス</param>
    /// <returns>正常に開けた場合は true、失敗した場合は false</returns>
    public bool Open(string fileName)
    {
        return Open(fileName, 0.0);
    }

    public bool Open(string fileName, double startSeconds)
    {
        try
        {
            Close();
            _reader = new PcmWaveFileReader(fileName);
            CurrentFileName = fileName;
            EnsureBufferMatchesReaderFormat();
            LoadGainLog(fileName);

            if (startSeconds > 0)
            {
                _reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(startSeconds, _reader.TotalTime.TotalSeconds));
                _currentSampleIndex = _reader.Position / _reader.WaveFormat.BlockAlign;
                AlignGainLogToCurrentSample();
            }

            IsPlaying = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error opening WAV file: {ex.Message}");
            throw;
        }
    }

    private void LoadGainLog(string fileName)
    {
        _gainLog.Clear();
        _gainLogIndex = 0;
        _currentSampleIndex = 0L;
        CurrentSystemGainDb = 0.0;
        CurrentRfFrequencyHz = 0;

        string path = Path.ChangeExtension(fileName, ".gain");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using BinaryReader binaryReader = new(File.OpenRead(path));
            if (binaryReader.BaseStream.Length % GainRecordSize != 0)
            {
                throw new InvalidDataException("The .gain file contains an incomplete record.");
            }

            long previousSampleIndex = -1;
            while (binaryReader.BaseStream.Position < binaryReader.BaseStream.Length)
            {
                long sampleIndex = binaryReader.ReadInt64();
                float systemGainDb = binaryReader.ReadSingle();
                int centerFrequencyHz = binaryReader.ReadInt32();
                if (sampleIndex < 0 || sampleIndex < previousSampleIndex || (_reader != null && sampleIndex > _reader.TotalSamples))
                {
                    throw new InvalidDataException("The .gain sample positions do not match the WAV data.");
                }

                _gainLog.Add((sampleIndex, systemGainDb, centerFrequencyHz));
                previousSampleIndex = sampleIndex;
            }

            if (_gainLog.Count > 0)
            {
                CurrentSystemGainDb = _gainLog[0].SystemGainDb;
                CurrentRfFrequencyHz = _gainLog[0].CenterFrequencyHz;
            }
        }
        catch (Exception ex)
        {
            _gainLog.Clear();
            _gainLogIndex = 0;
            CurrentSystemGainDb = 0.0;
            CurrentRfFrequencyHz = 0;
            Debug.Print("Failed to read .gain file: " + ex.Message);
        }
    }

    /// <summary>
    /// WAVファイルから音声データ（IQ信号等）を読み込みます。
    /// 現在のファイル終端に達した場合、連番の次のファイルがあれば自動的に開き直して読み込みを継続します。
    /// </summary>
    /// <param name="buffer">読み込んだデータの格納先となるバイト配列</param>
    /// <returns>実際に読み込まれたバイト数</returns>
    public int Read(byte[] buffer)
    {
        if (_reader == null || !IsPlaying)
        {
            return 0;
        }

        int totalBytesRead = 0;
        while (totalBytesRead < buffer.Length && _reader != null && IsPlaying)
        {
            int bytesRead = _reader.Read(buffer, totalBytesRead, buffer.Length - totalBytesRead);
            if (bytesRead <= 0)
            {
                if (!TryOpenNextSequenceFile())
                {
                    IsPlaying = false;
                    break;
                }
            }
            else
            {
                totalBytesRead += bytesRead;
            }
        }
        return totalBytesRead;
    }

    /// <summary>
    /// 現在再生中のファイル名に従い、連番の次のファイルが存在するかを確認し、存在すれば開いて再生を継続します。
    /// </summary>
    /// <returns>次のファイルが開けた場合は true、それ以外は false</returns>
    private bool TryOpenNextSequenceFile()
    {
        if (string.IsNullOrEmpty(CurrentFileName))
        {
            return false;
        }

        string path = Path.GetDirectoryName(CurrentFileName) ?? "";
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(CurrentFileName);
        string extension = Path.GetExtension(CurrentFileName);

        // ファイル名末尾が "-01.wav" のような形式かチェック
        if (fileNameWithoutExtension.Length < SequencePatternLength)
        {
            return false;
        }

        ReadOnlySpan<char> suffix = fileNameWithoutExtension.AsSpan(fileNameWithoutExtension.Length - SequencePatternLength);
        if (suffix[0] == '-' && int.TryParse(suffix[1..], out int sequenceNumber))
        {
            if (sequenceNumber >= MaxSequenceFiles)
            {
                return false;
            }

            int nextNumber = sequenceNumber + 1;
            string prefix = fileNameWithoutExtension[..^SequenceNumberLength];
            string nextFilePath = Path.Combine(path, $"{prefix}{nextNumber:D2}{extension}");

            if (File.Exists(nextFilePath))
            {
                Close();
                _reader = new PcmWaveFileReader(nextFilePath);
                CurrentFileName = nextFilePath;
                EnsureBufferMatchesReaderFormat();
                LoadGainLog(nextFilePath);
                IsPlaying = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 読み込みが進んだサンプルサイズ分だけインデックスを進め、必要に応じて現在のシステムゲインと中心周波数（RF）状態を同期します。
    /// </summary>
    /// <param name="samples">進んだサンプル数</param>
    public void AdvanceSampleIndex(int samples)
    {
        _currentSampleIndex += samples;
        while (_gainLogIndex + 1 < _gainLog.Count && _currentSampleIndex >= _gainLog[_gainLogIndex + 1].sampleIndex)
        {
            _gainLogIndex++;
            CurrentSystemGainDb = _gainLog[_gainLogIndex].SystemGainDb;
            CurrentRfFrequencyHz = _gainLog[_gainLogIndex].CenterFrequencyHz;
        }
    }

    public byte[] GetDefaultBuffer() => _buffer;

    private void EnsureBufferMatchesReaderFormat()
    {
        if (_reader == null)
        {
            return;
        }

        int bytesPer100ms = Math.Max(4096, _reader.WaveFormat.AverageBytesPerSecond / 10);
        if (_buffer.Length != bytesPer100ms)
        {
            _buffer = new byte[bytesPer100ms];
        }
    }

    public void Close()
    {
        IsPlaying = false;
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose()
    {
        Close();
    }

    private void AlignGainLogToCurrentSample()
    {
        _gainLogIndex = 0;
        if (_gainLog.Count == 0) return;

        CurrentSystemGainDb = _gainLog[0].SystemGainDb;
        CurrentRfFrequencyHz = _gainLog[0].CenterFrequencyHz;
        while (_gainLogIndex + 1 < _gainLog.Count && _currentSampleIndex >= _gainLog[_gainLogIndex + 1].sampleIndex)
        {
            _gainLogIndex++;
            CurrentSystemGainDb = _gainLog[_gainLogIndex].SystemGainDb;
            CurrentRfFrequencyHz = _gainLog[_gainLogIndex].CenterFrequencyHz;
        }
    }
}
