using System;

namespace SRdeck.Audio;

/// <summary>
/// 音声ファイル（WAV等）からデータを読み込むデバイスを抽象化するインターフェースです。
/// </summary>
public interface IAudioFileReader : IDisposable
{
    /// <summary>
    /// 指定されたWAVファイルを開きます。
    /// </summary>
    bool Open(string fileName);
    bool Open(string fileName, double startSeconds);

    /// <summary>
    /// ファイルを閉じます。
    /// </summary>
    void Close();

    /// <summary>
    /// 音声データ（IQ信号等）を読み込みます。
    /// </summary>
    int Read(byte[] buffer);

    /// <summary>
    /// 再生中かどうかを取得します。
    /// </summary>
    bool IsPlaying { get; }

    /// <summary>
    /// 現在のシステムゲイン (dB) を取得します。
    /// </summary>
    double CurrentSystemGainDb { get; }

    /// <summary>
    /// 現在のRF中心周波数を取得します。
    /// </summary>
    int CurrentRfFrequencyHz { get; }

    /// <summary>
    /// 現在開いているファイル名を取得します。
    /// </summary>
    string? CurrentFileName { get; }

    /// <summary>
    /// 現在再生中ファイルのサンプルレート(Hz)を取得します。
    /// </summary>
    int CurrentSampleRateHz { get; }

    /// <summary>
    /// ファイル読み込み用のデフォルトバッファを取得します。
    /// </summary>
    byte[] GetDefaultBuffer();

    /// <summary>
    /// 読み込みが進んだサンプルサイズ分だけ、内部の履歴インデックスを進めます。
    /// </summary>
    void AdvanceSampleIndex(int samples);
}
