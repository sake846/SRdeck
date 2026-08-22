using System;

namespace SRdeck.Models;

/// <summary>
/// アプリケーション全体の制御・描画・受信状態などに使用される各種パラメータ構造体を定義するファイルです。
/// </summary>

public enum DemodulationMode
{
    None = 0,
    USB = 1,
    LSB = 2,
    USB_Wide = 3,
    LSB_Wide = 4,
    AM = 5,
    AM_Wide = 6,
    FM_Narrow = 7,
    FM_Wide = 8
}

/// <summary>
/// 復調波形画面の表示モードを定義する列挙型です。
/// </summary>
public enum DemodWaveMode
{
    Wave = 0,      // 実時間波形
    FFT = 1,       // 復調後FFT
    Lissajous = 2, // リサージュ表示 (45度)
    Vector = 3,    // ベクトル表示 (位相2倍)
    Compare = 4    // リサージュ・ベクトル比較表示
}

/// <summary>
/// 周波数に関連する表示項目（バンドプラン、局名など）の表示モードを定義する列挙型です。
/// </summary>
public enum FrequencyDisplayMode
{
    Both = 0,        // バンド・局名両方
    BandOnly = 1,    // バンドのみ
    StationOnly = 2, // 局名のみ
    None = 3         // 表示なし
}

/// <summary>
/// 無線機 (Radio) の制御および復調制御に関連する基本パラメータ構造体です。
/// </summary>
public record struct RadioControl
{
    public int FsHz;
    public int CenterFreqHz;
    public int TunedFreqHz;
    public int FreqOffsetHz;
    public int HistorySec;

    public System.Windows.Point CursorPoint;
    public int CursorFreqHz;
    public int CursorFreqOffsetHz;
    public int CursorHistorySec;
    public int CursorPowerDb;

    public bool IsPowerOn;
    public bool IsSpeakerOn;
    public bool IsSquelchOn;
    public int SquelchDb;
    public int RfGainDb;
    public int SpanHz;
    public DemodulationMode DemodMode;
    public int StepHz;
    public bool IsZoomWindowVisible;
    public bool IsAfcEnabled;

    public bool IsMonoMode;

    public int MainSpanHz; // 現在表示中のスパン幅
    public int BaseMainSpanHz; // SPNで選択した受信周波数の基準幅
    public int MaxOffsetHz => Math.Max(MainSpanHz, BaseMainSpanHz) / 2;
    public const int DefaultStepHz = 1000;

    public float SystemDb;
    public int WaterfallColorMode;
    public bool IsStationNameVisible;
    public bool IsBandPlanVisible;
    public bool IsDebugVisible;
    public DemodWaveMode DemodWaveDisplayMode;
    public int FftResolutionMode;
    public int FftBatchCount;
    public bool IsGpuFftEnabled;
    public bool IsR1Visible;
    public int ZoomSpectrumMode; // 0:Auto, 1:Normal, 2:HighRes

    public float BiasPpm;
    public float AdjustmentPpm;
    public float BiasGain;

    /// <summary>
    /// 現在の周波数オフセット (FreqOffsetHz) を現在のステップ刻み (StepHz) に合わせて丸め込み、
    /// TunedFreqHz とスナップ済みの FreqOffsetHz を同期させます。
    /// </summary>
    public void ApplyPrimaryReceiverTuning()
    {
        int step = (StepHz <= 0) ? DefaultStepHz : StepHz;
        FreqOffsetHz = ClampFrequencyOffsetToSpan(FreqOffsetHz, SpanHz, step);
        TunedFreqHz = (CenterFreqHz + FreqOffsetHz + step / 2) / step * step;
        FreqOffsetHz = TunedFreqHz - CenterFreqHz;
    }

    /// <summary>
    /// AFC用の精密同調。ステップサイズを無視して 1Hz 単位でオフセットを適用します。
    /// </summary>
    public void ApplyPrimaryReceiverAfcTuning()
    {
        FreqOffsetHz = ClampFrequencyOffsetToSpan(FreqOffsetHz, SpanHz, 1);
        TunedFreqHz = CenterFreqHz + FreqOffsetHz;
    }

    private int ClampFrequencyOffsetToSpan(int freqOffset, int span, int step)
    {
        int offsetLimit = Math.Max(0, MaxOffsetHz - (span / 2));
        return Math.Clamp(freqOffset, -offsetLimit, offsetLimit);
    }

    /// <summary>
    /// 指定された復調モードに応じたデフォルトの Span と Step を設定します。
    /// </summary>
    public void SetModeDefaults(DemodulationMode mode)
    {
        int span, step;
        switch (mode)
        {
            case DemodulationMode.USB:
            case DemodulationMode.LSB:
            case DemodulationMode.USB_Wide:
            case DemodulationMode.LSB_Wide:
                span = 50000; step = 1000; break;
            case DemodulationMode.AM:
            case DemodulationMode.AM_Wide:
            case DemodulationMode.FM_Narrow:
                span = 50000; step = 25000; break;
            case DemodulationMode.FM_Wide:
                span = 250000; step = 100000; break;
            default:
                span = 50000; step = 1000; break;
        }

        DemodMode = mode;
        SpanHz = span;
        StepHz = step;
    }
}

/// <summary>
/// DSP処理にて受け渡しされる無線機の状態 (RadioState) を保持するクラスです。
/// </summary>
public class RadioState
{
    public int[] BasebandIData = Array.Empty<int>();
    public int[] BasebandQData = Array.Empty<int>();
    public float RxRssi;
    public bool IsSquelchOpen;

    public float AveDb;
    public float Ave2Db;
    public float MaxDb;
    public float MinFftPwr;
    public float Min2FftPwr;
    public long MinFftScanMinHz;
    public long MinFftScanMaxHz;
    public float AveFftPwr; // P_fft (dBFS)
    public float AveRxPwr; // P_rx (dBm)
    public float RfCalibrationDelta; // P_rx (dBm) - P_raw (dBFS) = -SystemDb + RfCalibrationOffset
    public float[] MainFftData = Array.Empty<float>(); // Averaged FFT data for spectrum/zoom (4096 points)
    public bool IsZoomHighResMode;

    internal RadioState CreateSnapshot() => (RadioState)MemberwiseClone();
}

/// <summary>
/// 各処理時間やバッファ状態を計測・表示するための診断用 (RadioDiagnostics) 構造体です。
/// </summary>
public delegate void RadioDiagnosticsMutator(ref RadioDiagnostics diagnostics);

public struct RadioDiagnostics
{
    public int GainReductionDb;
    public short BufferIMaxValue;
    public short BufferIMinValue;
    public short BufferQMaxValue;
    public short BufferQMinValue;

    public float RxRssi;

    public double TimeMainFft;
    public double TimeProcCycle;
    public double TimeTotal;
    public double TimeOsLag;
    public double TimeCpuPrep;
    public double TimeCpuPost;
    public double TimeFftCore;

    public double TimeFftFullResCopy;
    public double TimeFftAggregate;

    public double TimeGpuPrep;
    public double TimeGpuUpload;
    public double TimeGpuShader;
    public double TimeGpuDownload;
    public double TimeGpuPost;
    public double TimeGpuPack;
    public double TimeGpuUploadNative;
    public double TimeGpuDispatch;
    public double TimeGpuReadback;
    public double GpuAppUsagePercent;
    public double GpuUsagePercent;
    public double FftFps;
    public double WpfFps;
    public double DemodFps;
    public double AudioWriteIntervalMs;



    public long FftRequestCount;
    public long FftCompletedCount;
    public long FftDroppedCount;
    public long FftLatestRequestId;
    public long FftLatestCompletedId;
    public int FftQueueDepth;
    public long WpfFftFrameSerial;
    public long WpfFftDroppedFrames;
    public double TimeWpfSpectrum;
    public double TimeWpfSpectrumPrepare;
    public double TimeWpfSpectrumLock;
    public double TimeWpfSpectrumDraw;
    public double TimeWpfSpectrumUnlock;
    public double TimeWpfWaterfall;
    public double TimeWpfZoom;
    public double TimeWpfDemod;
    public int WpfGpuPathFlags; // bit0: Spectrum, bit1: Waterfall, bit2: Zoom, bit3: Demod
    public int WpfGpuInitSp;
    public int WpfGpuInitWf;
    public int WpfGpuInitZm;
    public int WpfGpuInitDm;
    public double EffectiveSampleRateHz;

    public int BufferWPtr;
    public int BufferRPtr;
    public int BufferPtrDiff;
    public int SdrQueuedSampleBlockCount;
    public long SdrCallbackCount;
    public long SdrDroppedCallbackCount;
    public double SdrLastCallbackAgeSeconds;
    public int SdrLastCallbackLengthBytes;
    public long SdrUnexpectedCallbackLengthCount;
}
