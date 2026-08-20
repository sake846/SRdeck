using System;
using System.Diagnostics;
using System.Windows;

namespace SRdeck.Models;

/// <summary>
/// ズームウィンドウ（拡大画面）に対するマウスクリック操作を処理するハンドラークラスです。
/// クリック位置に応じてチューニング周波数の微調整、履歴時間の移動、スパン（表示帯域）の切り替えを行います。
/// </summary>
internal class ZoomWindowClickHandler
{
    private bool _clicked;
    private Point _clickPoint;

    private const int LeftRegionMaxX = 124;
    private const int RightRegionMinX = 291;
    private const int TopRegionMaxY = 50;
    private const int BottomRegionMinY = 102;
    private const int HistoryStepSec = 10;

    public bool IsClicked => _clicked;
    public Point ClickPoint => _clickPoint;

    public ZoomWindowClickHandler()
    {
        _clicked = false;
    }

    public void OnClick(Point clickPoint)
    {
        _clickPoint = clickPoint;
        _clicked = true;
    }

    public void SyncClickParameters(ref RadioControl radioControl, int receiverIndex = 1, int maxHistorySeconds = AppConstants.MAX_HISTORY_SEC)
    {
        _clicked = false;
        int clickX = (int)_clickPoint.X;
        int clickY = (int)_clickPoint.Y;

        string region = clickX switch
        {
            < LeftRegionMaxX => "Left",
            > RightRegionMinX => "Right",
            _ => clickY switch
            {
                < TopRegionMaxY => "Top",
                > BottomRegionMinY => "Bottom",
                _ => "Center"
            }
        };

        ProcessZoomRegionClick(region, ref radioControl, receiverIndex, maxHistorySeconds);
    }

    public void ProcessZoomRegionClick(string region, ref RadioControl radioControl, int receiverIndex = 1, int maxHistorySeconds = AppConstants.MAX_HISTORY_SEC)
    {
        switch (region)
        {
            case "Left":
                AdjustFrequency(ref radioControl, receiverIndex, -1);
                break;
            case "Right":
                AdjustFrequency(ref radioControl, receiverIndex, 1);
                break;
            case "Top":
                AdjustHistory(ref radioControl, receiverIndex, -HistoryStepSec, maxHistorySeconds);
                break;
            case "Bottom":
                AdjustHistory(ref radioControl, receiverIndex, HistoryStepSec, maxHistorySeconds);
                break;
            case "Center":
                ToggleSpan(ref radioControl, receiverIndex);
                break;
        }
    }

    private void AdjustFrequency(ref RadioControl radioControl, int receiverIndex, int direction)
    {
        radioControl.FreqOffsetHz += radioControl.StepHz * direction;
        radioControl.ApplyPrimaryReceiverTuning();
        Debug.Print($"ZoomWindow: TunedFreqHz = {radioControl.TunedFreqHz:#,0}, FreqOffsetHz = {radioControl.FreqOffsetHz:#,0}");
    }

    private void AdjustHistory(ref RadioControl radioControl, int receiverIndex, int seconds, int maxHistorySeconds)
    {
        radioControl.HistorySec = Math.Clamp(radioControl.HistorySec + seconds, 0, maxHistorySeconds);
        Debug.Print($"ZoomWindow: HistorySec = {radioControl.HistorySec}");
    }

    private void ToggleSpan(ref RadioControl radioControl, int receiverIndex)
    {
        int currentSpan = radioControl.SpanHz;
        int nextSpan = currentSpan switch
        {
            250000 => 100000, 
            100000 => 50000, 
            50000 => 20000, 
            20000 => 10000, 
            _ => 250000, 
        };
        
        radioControl.SpanHz = nextSpan;
        
        Debug.Print($"ZoomWindow: SpanHz = {nextSpan:#,0} (Receiver: {receiverIndex})");
    }

    private void SyncZoomFrequency(ref RadioControl radioControl, int receiverIndex)
    {
        radioControl.ApplyPrimaryReceiverTuning();
        Debug.Print($"ZoomWindowClickHandler: TunedFreqHz = {radioControl.TunedFreqHz:#,0}, FreqOffsetHz = {radioControl.FreqOffsetHz:#,0}");
    }
}
