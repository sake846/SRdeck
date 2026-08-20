using System;

namespace SRdeck.Models.Configuration;

/// <summary>
/// ハードウェア（SDR機器やアンテナ）固有の校正値・性能設定を保持するクラスです。
/// ユーザー設定とは別に hardware.json で管理されます。
/// </summary>
public class HardwareSettings
{
    /// <summary>
    /// アンテナ入力から表示値 (dBm) への全体的なオフセット (dB)。
    /// 測定器による校正値として使用します。
    /// </summary>
    public float RfCalibrationOffset { get; set; } = 22.0f;



    /// <summary>
    /// 水晶発振器の偏差補正 (PPM)。
    /// </summary>
    public float SdrBiasPpm { get; set; } = 0.0f;

    /// <summary>
    /// SDRplay の Bias-T 給電を有効にします。対応する機種でのみ使用されます。
    /// </summary>
    public bool SdrPlayBiasTEnabled { get; set; }

    /// <summary>
    /// SDRplay のアンテナ入力。0 が A、1 が B、2 が C です。
    /// </summary>
    public int SdrPlayAntennaIndex { get; set; }

    /// <summary>
    /// RSP2/RSPduo の AM ポート。0 がポート 1、1 がポート 2 です。
    /// </summary>
    public int SdrPlayAmPortIndex { get; set; }

    /// <summary>
    /// RSP2/RSPduo の外部基準クロック出力を有効にします。
    /// </summary>
    public bool SdrPlayExternalReferenceOutputEnabled { get; set; }

    /// <summary>
    /// RSPdx 系の HDR モードを有効にします。
    /// </summary>
    public bool SdrPlayHdrEnabled { get; set; }

    /// <summary>
    /// RSPdx 系 HDR の帯域幅。0=0.2 MHz、1=0.5 MHz、2=1.2 MHz、3=1.7 MHz。
    /// </summary>
    public int SdrPlayHdrBandwidthIndex { get; set; }

    /// <summary>
    /// システム全体のゲイン微調整オフセット (dB)。
    /// 個体差などの補正に使用します。
    /// </summary>
    public float SystemGainOffset { get; set; } = 0.0f;
    
    /// <summary>
    /// ゲインリダクションの下限値 (dB)。
    /// デフォルトは 20 (Normal) です。0 を指定すると最大感度まで開放されます。
    /// </summary>
    public int MinGainReduction { get; set; } = 20;

    /// <summary>
    /// ホスト側 RF AGC の有効化設定 (0: Off, 1: On)。デバイスAGCは常に無効です。
    /// </summary>
    public int RfAgcEnabled { get; set; } = 0;

    private AgcReleaseMode agcReleaseMode = AgcReleaseMode.Slow;

    /// <summary>
    /// ホスト側 RF AGC のリリース速度。既定値は低速（10秒）です。
    /// </summary>
    public AgcReleaseMode AgcReleaseMode
    {
        get => agcReleaseMode;
        set => agcReleaseMode = Enum.IsDefined(value) ? value : AgcReleaseMode.Slow;
    }



}
