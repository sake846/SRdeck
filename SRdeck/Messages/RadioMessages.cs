namespace SRdeck.Messages;

using SRdeck.Models;

/// <summary>
/// UI層 (ViewModel 等) から Sdr / DSP層 (CoreEngine 等) へ
/// 主要なパラメータの変更を伝えるためのメッセージです。
/// </summary>
/// <param name="NewControl">更新したい制御パラメータが格納された構造体</param>
/// <param name="ResetMainViewZoom">外部選局によりメインスペクトラムの拡大状態を解除するかどうか</param>
/// <param name="ApplyFrequencyImmediately">表示更新が再びズームする前に SDR の中心周波数を同期するかどうか</param>
public record class RadioControlUpdateMessage(
    RadioControl NewControl,
    bool IsCursorOnly = false,
    bool ResetMainViewZoom = false,
    bool ApplyFrequencyImmediately = false);

/// <summary>
/// 各画面のバイアス値（表示オフセット）の変更を伝えるためのメッセージです。
/// </summary>
public record class BiasUpdateMessage(int SpectrumBiasAdj, int WaterfallBiasAdj, int SpectrumZoomBiasAdj, int WaterfallZoomBiasAdj);
/// <summary>
/// ズームウィンドウのスパン設定変更を伝えるためのメッセージです。
/// </summary>
public record class ZoomSpanChangeMessage(int ReceiverIndex, int NewSpanHz);
/// <summary>
/// 拡大画面の描画モード（オート/ノーマル/ハイレゾ）の変更を伝えるためのメッセージです。
/// </summary>
public record class ZoomModeUpdateMessage(int ReceiverIndex, int Mode);
