using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;
using SRdeck.Services;
using SRdeck.ViewModels.Components;
using SRdeck.Renderers;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private System.Windows.Threading.DispatcherTimer? _snapTimer;
    private int _snapTargetHz;

    private void StartSdrCenterSnap()
    {
        if (Display.IsMainViewZoomed) return; // ズーム中はスナップしない

        RadioControl radioControl = _engine.Control;
        int span = radioControl.MainSpanHz > 0 ? radioControl.MainSpanHz : radioControl.BaseMainSpanHz;
        int roundingHz;
        if (span <= 1000000) roundingHz = 100000;
        else if (span <= 2400000) roundingHz = 200000;
        else if (span <= 4000000) roundingHz = 500000;
        else if (span <= 8000000) roundingHz = 500000;
        else if (span <= 16000000) roundingHz = 1000000;
        else if (span <= 32000000) roundingHz = 2000000;
        else roundingHz = 4000000;

        int targetHz = (int)(((long)radioControl.CenterFreqHz + roundingHz / 2) / roundingHz * roundingHz);
        targetHz = (int)Math.Clamp(targetHz, 0, 2000000000);

        if (radioControl.CenterFreqHz == targetHz) return; // すでに一致している場合は何もしない

        _snapTargetHz = targetHz;

        if (_snapTimer == null)
        {
            _snapTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
            _snapTimer.Interval = TimeSpan.FromMilliseconds(16);
            _snapTimer.Tick += HandleSnapTimerTick;
        }
        _snapTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _activityTimer;

    private void InitializeCenterSnapDelayTimer()
    {
        if (_activityTimer == null)
        {
            _activityTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
            _activityTimer.Interval = TimeSpan.FromMilliseconds(200);
            _activityTimer.Tick += (sender, eventArgs) =>
            {
                _activityTimer.Stop();
                StartSdrCenterSnap();
            };
        }
    }

    private void RestartCenterSnapDelayTimer()
    {
        InitializeCenterSnapDelayTimer();
        _activityTimer?.Stop();
        _activityTimer?.Start();
    }

    private void StopCenterSnapDelayTimer()
    {
        _activityTimer?.Stop();
    }

    private void StopSdrCenterSnap()
    {
        _snapTimer?.Stop();
        StopCenterSnapDelayTimer();
    }

    private void CompleteSdrCenterSnapBeforeZoom()
    {
        StopSdrCenterSnap();
        if (Display.IsMainViewZoomed) return;

        RadioControl radioControl = _engine.Control;
        radioControl.MainSpanHz = Display.BaseMainSpanHz;
        radioControl.BaseMainSpanHz = Display.BaseMainSpanHz;
        int targetHz = TuningCoordinator.RoundInputCenterFrequency(radioControl);
        _snapTargetHz = targetHz;
        radioControl.CenterFreqHz = targetHz;
        radioControl.FreqOffsetHz = radioControl.TunedFreqHz - targetHz;
        _engine.Control = radioControl;

        // The control must still be unzoomed when the engine evaluates this update;
        // otherwise the tuning coordinator intentionally retains the previous SDR center.
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(
            radioControl,
            ApplyFrequencyImmediately: true));
    }

    private void HandleSnapTimerTick(object? sender, EventArgs eventArgs)
    {
        RadioControl radioControl = _engine.Control;
        int targetHz = _snapTargetHz;

        double difference = targetHz - radioControl.CenterFreqHz;
        if (Math.Abs(difference) <= 1.5)
        {
            radioControl.CenterFreqHz = targetHz;
            radioControl.FreqOffsetHz = radioControl.TunedFreqHz - targetHz;
            radioControl.ApplyPrimaryReceiverTuning();
            _engine.Control = radioControl;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
            _snapTimer?.Stop();
        }
        else
        {
            int step = (int)Math.Round(difference * 0.25);
            if (step == 0) step = difference > 0 ? 1 : -1;

            radioControl.CenterFreqHz += step;
            radioControl.FreqOffsetHz = radioControl.TunedFreqHz - radioControl.CenterFreqHz;
            radioControl.ApplyPrimaryReceiverTuning();
            _engine.Control = radioControl;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
    }

    [RelayCommand]
    private void ManipulationCompleted()
    {
        StartSdrCenterSnap();
    }
}
