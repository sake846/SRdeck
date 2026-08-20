using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;
using SRdeck.ViewModels.Components;
using SRdeck.Renderers;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // UI レイアウト・インタラクション定数
    private const double ZOOM_WINDOW_MIN_PADDING = 30;
    private const double ZOOM_WINDOW_CONFLICT_DIST = 10;
    private const double CENTER_FREQ_DRAG_STEP = 500000;
    private const double WATERFALL_HISTORY_MAX_SEC = 180;
    private const double ZOOM_DRAG_THRESHOLD = 3;

    [RelayCommand]
    private void ToggleStationName()
    {
        IsStationNameVisible = !IsStationNameVisible;
    }

    private void BuildSignalMeter()
    {
        var greenBrush = Application.Current.Resources["LedYellowGreenBrush"] as SolidColorBrush ?? Brushes.Transparent;
        var redBrush = Application.Current.Resources["LedRedBrush"] as SolidColorBrush ?? Brushes.Transparent;

        void AddSegments(ObservableCollection<SignalMeterSegment> list)
        {
            for (int i = 0; i < 3; i++) list.Add(new SignalMeterSegment { Height = 10, ActiveColor = greenBrush, IsActive = false });
            for (int i = 3; i < 6; i++) list.Add(new SignalMeterSegment { Height = 12, ActiveColor = greenBrush, IsActive = false });
            for (int i = 6; i < 9; i++) list.Add(new SignalMeterSegment { Height = 14, ActiveColor = greenBrush, IsActive = false });
            for (int i = 9; i < 12; i++) list.Add(new SignalMeterSegment { Height = 16, ActiveColor = redBrush, IsActive = false });
        }
        AddSegments(SignalMeterSegments);
    }

    [ObservableProperty] private bool _isStepAreaExpanded = true;
    public string StepAreaToggleLabel => IsStepAreaExpanded ? "◀" : "▶";

    [RelayCommand]
    private void ToggleStepArea() { IsStepAreaExpanded = !IsStepAreaExpanded; OnPropertyChanged(nameof(StepAreaToggleLabel)); }

    public void SyncState(RadioControl p, RadioState r)
    {
        Tuner.SyncState(r);
        SyncTunerParameters(p, r);
        SyncSignalMeters(r);
        SyncButtons(p);
        SyncOverlayVisuals(p, r);
        IsReceiver1Visible = p.IsR1Visible;
    }

    private void SyncTunerParameters(RadioControl p, RadioState r)
    {
        SyncTunerViewModel(Tuner, p.TunedFreqHz, p.HistorySec, r.RxRssi, p.SquelchDb, p.IsPowerOn, p.IsSpeakerOn, p.IsSquelchOn);
        ZoomOverlay.IsHighResMode = r.IsZoomHighResMode;
        var currentStepOpt = StepOptions.FirstOrDefault(o => o.ValueHz == p.StepHz);
        int targetStepMode = currentStepOpt?.Index ?? -1;
        if (StepMode != targetStepMode) StepMode = targetStepMode;
        IsDelayActive = Tuner.IsPowerOn;
        SquelchBarWidth = CalculateMeterWidth(Tuner.SquelchDb);
    }

    private void SyncTunerViewModel(TunerViewModel vm, long freq, int history, float rssi, int squelch, bool power, bool speaker, bool squelchOn)
    {
        vm.TunedFreqHz = freq; vm.HistorySec = history; vm.RxRssi = rssi; vm.SquelchDb = squelch;
        vm.IsPowerOn = power; vm.IsSpeakerOn = speaker; vm.IsSquelchOn = squelchOn;
    }

    private void SyncSignalMeters(RadioState r) { SyncSignalMeter(SignalMeterSegments, r.RxRssi); }

    private void SyncButtons(RadioControl p)
    {
        if (_lastState.WaterfallColorMode != p.WaterfallColorMode) { _lastState.WaterfallColorMode = p.WaterfallColorMode; _lastStateService.SaveLastState(_lastState); }
    }

    [RelayCommand]
    private void ApplyStepSelection(object stepObj)
    {
        if (stepObj == null) return;
        int stepHz = 0;
        if (stepObj is int i) stepHz = i; else if (int.TryParse(stepObj.ToString(), out int parsed)) stepHz = parsed;
        if (stepHz > 0)
        {
            RadioControl p = _engine.Control;
            ApplyStep(ref p, stepHz, 1); _engine.Control = p;
            var selectedOption = StepOptions.FirstOrDefault(x => x.ValueHz == stepHz);
            if (selectedOption != null) StepMode = selectedOption.Index;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(p));
        }
    }

    private double CalculateMeterWidth(double db)
    {
        if (db < -141) return 0;
        if (db <= -93) return (db + 141.0);
        if (db <= -63) return 48.0 + (db + 93.0) * 0.6;
        return 72.0;
    }

    private void SyncSignalMeter(ObservableCollection<SignalMeterSegment> segments, float rssiRxPwr)
    {
        for (int i = 0; i < 9; i++) { float threshold = -141f + (i * 6f); if (i < segments.Count) segments[i].IsActive = rssiRxPwr >= threshold; }
        for (int j = 9; j < 12; j++) { float threshold = -93f + ((j - 8) * 10f); if (j < segments.Count) segments[j].IsActive = rssiRxPwr >= threshold; }
    }
}
