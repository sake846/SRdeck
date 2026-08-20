using System;
using System.Windows;
using SRdeck.Models;

namespace SRdeck.Services;

/// <summary>
/// スワイプ操作による周波数・履歴の変動計算（アキュムレータ管理）を担当するサービスです。
/// </summary>
public class InputCoordinationService
{
    private double _spectrumSwipeAccumulator = 0;
    private double _waterfallSwipeXAccumulator = 0;
    private double _waterfallSwipeYAccumulator = 0;

    private const double SPECTRUM_SWIPE_SENSITIVITY = 40.0; // 周波数スワイプのしきい値
    private const double WATERFALL_X_SENSITIVITY = 100.0; // ウォーターフォール周波数スワイプの正規化分母
    private const double WATERFALL_Y_SENSITIVITY = 500.0; // ウォーターフォール履歴スワイプの正規化分母

    public bool ProcessSpectrumSwipe(double deltaX, ref RadioControl radioControl, int swipeStep)
    {
        bool isChanged = false;
        if (radioControl.CursorFreqHz != -1)
        {
            radioControl.CursorFreqHz = -1;
            isChanged = true;
        }

        _spectrumSwipeAccumulator += deltaX;
        
        if (Math.Abs(_spectrumSwipeAccumulator) >= SPECTRUM_SWIPE_SENSITIVITY)
        {
            int stepCount = (int)(_spectrumSwipeAccumulator / SPECTRUM_SWIPE_SENSITIVITY);
            int newCenter = radioControl.CenterFreqHz - (stepCount * swipeStep);
            newCenter = (int)Math.Round((double)newCenter / swipeStep) * swipeStep;
            
            if (newCenter != radioControl.CenterFreqHz)
            {
                int oldAbsoluteTunedHz = radioControl.CenterFreqHz + radioControl.FreqOffsetHz;
                radioControl.CenterFreqHz = newCenter;
                radioControl.FreqOffsetHz = oldAbsoluteTunedHz - radioControl.CenterFreqHz;
                radioControl.ApplyPrimaryReceiverTuning();
                
                _spectrumSwipeAccumulator -= (stepCount * SPECTRUM_SWIPE_SENSITIVITY);
                isChanged = true;
            }
        }
        return isChanged;
    }

    public bool ProcessWaterfallSwipe(double deltaX, double deltaY, bool isShift, ref RadioControl radioControl, int maxHistorySec = AppConstants.MAX_HISTORY_SEC)
    {
        bool isChanged = false;
        if (radioControl.CursorFreqHz != -1) { radioControl.CursorFreqHz = -1; isChanged = true; }

        double spanHz = radioControl.SpanHz;
        double stepHz = radioControl.StepHz;
        
        // 周波数移動
        _waterfallSwipeXAccumulator += deltaX * (spanHz / WATERFALL_X_SENSITIVITY); 
        if (Math.Abs(_waterfallSwipeXAccumulator) >= stepHz)
        {
            int stepCount = (int)(_waterfallSwipeXAccumulator / stepHz);
            radioControl.FreqOffsetHz += (int)(stepCount * stepHz);
            _waterfallSwipeXAccumulator -= stepCount * stepHz;
            isChanged = true;
        }

        // 履歴移動 (Y軸は方向反転)
        _waterfallSwipeYAccumulator -= deltaY * ((maxHistorySec + 1) / WATERFALL_Y_SENSITIVITY); 
        if (Math.Abs(_waterfallSwipeYAccumulator) >= 1.0)
        {
            int secondsDifference = (int)_waterfallSwipeYAccumulator;
            radioControl.HistorySec = Math.Clamp(radioControl.HistorySec - secondsDifference, 0, maxHistorySec);
            _waterfallSwipeYAccumulator = 0;
            isChanged = true;
        }

        if (isChanged)
        {
            radioControl.ApplyPrimaryReceiverTuning();
        }
        return isChanged;
    }
}
