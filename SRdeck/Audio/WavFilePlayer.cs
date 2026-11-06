using System.Diagnostics;
using System.Globalization;
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
            if (_reader.WaveFormat is not { Channels: 2, BitsPerSample: 16 })
                throw new InvalidDataException("IQ WAV は 16 bit PCM・2チャンネル（左=I、右=Q）のみ再生できます。");
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
            Close();
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
            CurrentRfFrequencyHz = IqPlaybackFileInspector.TryReadCenterFrequencyHz(fileName) ?? 0;
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
            else
            {
                CurrentRfFrequencyHz = IqPlaybackFileInspector.TryReadCenterFrequencyHz(fileName) ?? 0;
            }
        }
        catch (Exception ex)
        {
            _gainLog.Clear();
            _gainLogIndex = 0;
            CurrentSystemGainDb = 0.0;
            CurrentRfFrequencyHz = IqPlaybackFileInspector.TryReadCenterFrequencyHz(fileName) ?? 0;
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

        int separatorIndex = fileNameWithoutExtension.LastIndexOf('-');
        if (separatorIndex < 0) return false;
        ReadOnlySpan<char> suffix = fileNameWithoutExtension.AsSpan(separatorIndex + 1);
        if (suffix.Length < 2 ||
            !int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int sequenceNumber) ||
            sequenceNumber <= 0 || sequenceNumber == int.MaxValue) return false;

        int nextNumber = sequenceNumber + 1;
        string prefix = fileNameWithoutExtension[..(separatorIndex + 1)];
        string nextFilePath = Path.Combine(path,
            $"{prefix}{nextNumber.ToString($"D{suffix.Length}", CultureInfo.InvariantCulture)}{extension}");
        if (!File.Exists(nextFilePath)) return false;

        PcmWaveFileReader nextReader = new(nextFilePath);
        bool readerTransferred = false;
        try
        {
            if (nextReader.WaveFormat is not { Channels: 2, BitsPerSample: 16 })
                throw new InvalidDataException("連番ファイルの形式が IQ WAV（16 bit PCM・2チャンネル）ではありません。");
            if (_reader is { } currentReader &&
                currentReader.WaveFormat.SampleRate != nextReader.WaveFormat.SampleRate)
                throw new InvalidDataException("連番IQ WAVのサンプルレートが一致しません。");

            int nextFrequencyHz = IqPlaybackFileInspector.TryReadCenterFrequencyHz(nextFilePath) ?? 0;
            if (CurrentRfFrequencyHz > 0 && nextFrequencyHz > 0 && CurrentRfFrequencyHz != nextFrequencyHz)
                throw new InvalidDataException("連番IQ WAVの中心周波数が一致しません。");

            Close();
            _reader = nextReader;
            readerTransferred = true;
            CurrentFileName = nextFilePath;
            EnsureBufferMatchesReaderFormat();
            LoadGainLog(nextFilePath);
            IsPlaying = true;
            return true;
        }
        finally
        {
            if (!readerTransferred) nextReader.Dispose();
        }
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
