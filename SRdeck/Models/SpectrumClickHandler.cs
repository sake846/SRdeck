using System;
using System.Windows;
using SRdeck.Renderers;

namespace SRdeck.Models;

/// <summary>
/// スペクトラム上のクリック操作によるレシーバーパラメータ調整をハンドリングするクラスです。
/// </summary>
public class SpectrumClickHandler
{
    private Point _clickPoint;
    private int _receiverIndex;
    private bool _clicked;

    public bool IsClicked => _clicked;

    public void RegisterClick(Point clickPoint, int receiverIndex)
    {
        _clickPoint = clickPoint;
        _receiverIndex = receiverIndex;
        _clicked = true;
    }

    public void OnClick(Point clickPoint, int receiverIndex)
    {
        RegisterClick(clickPoint, receiverIndex);
    }

    public bool SyncClickParameters(ref RadioControl radioControl, double displayWidthValue, bool isReceiver1Visible, bool isReceiver2Visible, int displayBandwidthHz = 7000000)
    {
        _clicked = false;
        double displayWidth = Math.Max(1.0, displayWidthValue);

        if (_receiverIndex == 1 && isReceiver1Visible)
        {
            radioControl.FreqOffsetHz = RenderUtils.XToFreqOffset((float)_clickPoint.X, displayWidth, displayBandwidthHz);
            radioControl.ApplyPrimaryReceiverTuning();
            radioControl.HistorySec = 0;
            radioControl.IsPowerOn = true;
            radioControl.IsZoomWindowVisible = true;
            return true;
        }
        return false;
    }
}
