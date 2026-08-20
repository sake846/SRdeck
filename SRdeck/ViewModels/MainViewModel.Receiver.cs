using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public void ShiftCenterFrequency(long deltaHz)
    {
        RadioControl radioControl = _engine.Control;
        int oldAbsoluteTunedHz = radioControl.CenterFreqHz + radioControl.FreqOffsetHz;

        long newCenter = (long)radioControl.CenterFreqHz + deltaHz;
        // SDRハードウェアのAPI保護のため、0Hz〜2GHzの範囲にクランプ
        newCenter = Math.Clamp(newCenter, 0L, 2000000000L);
        radioControl.CenterFreqHz = (int)newCenter;

        radioControl.FreqOffsetHz = oldAbsoluteTunedHz - radioControl.CenterFreqHz;

        radioControl.ApplyPrimaryReceiverTuning();

        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        Tuner.BuildCenterFreqDigits();
    }

    public void AdvanceStep(int direction)
    {
        RadioControl radioControl = _engine.Control;
        int currentStep = radioControl.StepHz;
        int index = -1;
        for (int i = 0; i < AppConstants.STEP_LEVELS.Length; i++)
        {
            if (currentStep == AppConstants.STEP_LEVELS[i]) { index = i; break; }
        }
        if (index == -1) index = 0;
        
        index = (index + direction + AppConstants.STEP_LEVELS.Length) % AppConstants.STEP_LEVELS.Length;
        radioControl.StepHz = AppConstants.STEP_LEVELS[index];
        radioControl.ApplyPrimaryReceiverTuning();

        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        Tuner.StepHz = radioControl.StepHz;
    }

    private static readonly int[] ZoomSpanLevels = { 250000, 100000, 50000, 20000, 10000 };

    public void FinerSpan(int receiverIndex)
    {
        RadioControl radioControl = _engine.Control;
        int currentSpan = radioControl.SpanHz;
        int nextSpan = ZoomSpanLevels[0];
        
        for (int i = 0; i < ZoomSpanLevels.Length; i++)
        {
            if (currentSpan > ZoomSpanLevels[i]) { nextSpan = ZoomSpanLevels[i]; break; }
            if (i == ZoomSpanLevels.Length - 1) nextSpan = ZoomSpanLevels[0];
        }

        radioControl.SpanHz = nextSpan; 
        radioControl.ApplyPrimaryReceiverTuning();

        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        Tuner.SpanHz = radioControl.SpanHz;
    }

    public void AdvanceDemodMode(int receiverIndex)
    {
        RadioControl radioControl = _engine.Control;
        DemodulationMode currentMode = radioControl.DemodMode;

        int currentIndex = -1;
        var modeOptions = Tuner.ModeOptions;
        for (int i = 0; i < modeOptions.Count; i++)
        {
            if (modeOptions[i].InternalMode == currentMode) { currentIndex = i; break; }
        }
        if (currentIndex == -1) currentIndex = 0;

        int nextIndex = (currentIndex + 1) % modeOptions.Count;
        Tuner.SelectedModeIndex = nextIndex;
    }

    public void ToggleReceiverPower(int receiverIndex)
    {
        ReceiverButtonClickCommand.Execute(ReceiverCommandType.PowerToggle);
    }

    private void ApplyStep(ref RadioControl parameters, int stepHz, int index)
    {
        parameters.StepHz = stepHz; 
        parameters.ApplyPrimaryReceiverTuning();
    }

    private void ApplySpan(ref RadioControl parameters, int spanHz, int index)
    {
        parameters.SpanHz = spanHz;
    }

    public void ApplyModeDefaults(ref RadioControl parameters, DemodulationMode mode, int index)
    {
        DemodulationMode oldMode = parameters.DemodMode;
        int currentSpan = parameters.SpanHz;

        parameters.DemodMode = mode;

        int stepHz = 25000;
        switch (mode)
        {
            case DemodulationMode.USB: 
            case DemodulationMode.LSB: 
            case DemodulationMode.USB_Wide: 
            case DemodulationMode.LSB_Wide:
                stepHz = 100; break;
            case DemodulationMode.AM_Wide: stepHz = 1000; break;
            case DemodulationMode.FM_Wide: stepHz = 100000; break;
            case DemodulationMode.AM: 
            case DemodulationMode.FM_Narrow: stepHz = 25000; break;
        }

        int spanHz = currentSpan;
        bool wasFmW = oldMode == DemodulationMode.FM_Wide;
        bool isFmW = mode == DemodulationMode.FM_Wide;
        if (spanHz <= 0) spanHz = isFmW ? 250000 : 50000;
        else if (isFmW && !wasFmW) spanHz = 250000;
        else if (!isFmW && wasFmW) spanHz = 50000;

        parameters.StepHz = stepHz; 
        parameters.SpanHz = spanHz;
    }
}
