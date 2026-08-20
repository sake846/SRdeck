using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{


    // --- Radio & FFT Settings ---
    [ObservableProperty] private DemodulationMode _demodMode;
    [ObservableProperty] private int _stepMode = -1;
    [ObservableProperty] private int _fftResolutionMode = 0;
    [ObservableProperty] private int _fftBatchMode = 0;
    [ObservableProperty] private bool _isGpuFftEnabled = false;
    [ObservableProperty] private double _squelchBarWidth;

    // --- Property Changed Handlers ---

    partial void OnFftBatchModeChanged(int value) => ApplyFftAveragingLimit();

    partial void OnIsGpuFftEnabledChanged(bool value)
    {
        if (_engine == null) return;
        RadioControl p = _engine.Control;
        p.IsGpuFftEnabled = value;
        PersistFftBatchState(FftBatchMode);
        _lastStateService.SaveLastState(_lastState);
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(p));
        ApplyFftResolutionLimit();
    }

    partial void OnFftResolutionModeChanged(int value)
    {
        if (_engine == null) return;
        RadioControl p = _engine.Control;
        p.FftResolutionMode = value;
        PersistFftBatchState(FftBatchMode);
        _lastStateService.SaveLastState(_lastState);
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(p));
        ApplyFftResolutionLimit();
    }





    partial void OnStepModeChanged(int value)
    {
        if (value < 0 || value >= StepOptions.Count) return;
        var selected = StepOptions[value];
        RadioControl p = _engine.Control;
        bool changed = false;
        if (p.StepHz != selected.ValueHz) { p.StepHz = selected.ValueHz; p.ApplyPrimaryReceiverTuning(); changed = true; }
        if (changed) WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(p));
    }


}
