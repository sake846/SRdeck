using System;
using System.Windows;
using SRdeck.Renderers;

namespace SRdeck.Models;

/// <summary>
/// ウォーターフォール画面に対するマウスクリック操作を処理するハンドラークラスです。
/// 過去の履歴位置（時間）と周波数を指定し、指定時点の波形履歴の確認やチューニング操作を行います。
/// </summary>
internal class WaterfallClickHandler
{
    private bool _clicked;
    private Point _clickPoint;
    private int _receiverIndex = 1;

    public bool IsClicked => _clicked;
    public Point ClickPoint => _clickPoint;

    public WaterfallClickHandler()
    {
        _clicked = false;
    }

    public void OnClick(Point clickPoint, int receiverIndex = 1)
    {
        _clickPoint = clickPoint;
        _receiverIndex = receiverIndex;
        _clicked = true;
    }

    public bool SyncClickParameters(ref RadioControl radioControl, double displayWidthValue, double displayHeightValue, bool isReceiver1Visible, bool isReceiver2Visible, int displayBandwidthHz = 7000000, double totalHistorySeconds = WaterfallTimeModel.TotalHistorySeconds)
    {
        _clicked = false;
        double displayWidth = Math.Max(1.0, displayWidthValue);
        double displayHeight = Math.Max(1.0, displayHeightValue);
        
        int maxHistorySeconds = AppConstants.IQ_RETENTION_SECONDS;
        int limitHistorySeconds = AppConstants.MAX_HISTORY_SEC;

        if (isReceiver1Visible)
        {
            radioControl.FreqOffsetHz = RenderUtils.XToFreqOffset((float)_clickPoint.X, displayWidth, displayBandwidthHz);
            radioControl.ApplyPrimaryReceiverTuning();
            
            int historySeconds = RenderUtils.YToSec((float)(_clickPoint.Y - 18.0), displayHeight, totalHistorySeconds);
            if (historySeconds >= maxHistorySeconds)
            {
                historySeconds = limitHistorySeconds;
            }
            radioControl.HistorySec = Math.Clamp(historySeconds, 0, limitHistorySeconds);
            
            radioControl.IsPowerOn = true;
            radioControl.IsZoomWindowVisible = true;
            return true;
        }
        return false;
    }
}
