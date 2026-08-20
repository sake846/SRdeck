using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private DispatcherTimer? _cursorHideTimer;
    private DispatcherTimer? _cursorFadeTimer;
    private DispatcherTimer? _cursorUpdateTimer;
    private RadioControl _pendingCursorControl;
    private bool _hasPendingCursorControl;
    [ObservableProperty] private bool _isMouseCursorHidden = true;

    private void InitializeCursorTimer()
    {
        _cursorHideTimer = new DispatcherTimer();
        _cursorHideTimer.Interval = TimeSpan.FromSeconds(4);
        _cursorHideTimer.Tick += (sender, eventArgs) => StartCursorFadeOut();

        _cursorUpdateTimer = new DispatcherTimer(DispatcherPriority.Render);
        _cursorUpdateTimer.Interval = TimeSpan.FromMilliseconds(16);
        _cursorUpdateTimer.Tick += (sender, eventArgs) => FlushCursorUpdate();
    }

    public void ResetCursorTimer()
    {
        if (_cursorFadeTimer != null && _cursorFadeTimer.IsEnabled)
        {
            _cursorFadeTimer.Stop();
        }

        _cursorHideTimer?.Stop();
        _cursorHideTimer?.Start();

        SpectrumOverlay.SpCsOpacity = 1.0;
        WaterfallOverlay.SpCsOpacity = 1.0;
        IsMouseCursorHidden = true;
    }

    private void QueueCursorUpdate(RadioControl radioControl)
    {
        _pendingCursorControl = radioControl;
        _hasPendingCursorControl = true;
        _cursorUpdateTimer?.Start();
    }

    private void CancelPendingCursorUpdate()
    {
        _hasPendingCursorControl = false;
        _cursorUpdateTimer?.Stop();
    }

    private void FlushCursorUpdate()
    {
        if (!_hasPendingCursorControl)
        {
            _cursorUpdateTimer?.Stop();
            return;
        }

        var radioControl = _pendingCursorControl;
        _hasPendingCursorControl = false;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl, IsCursorOnly: true));
    }

    private void StartCursorFadeOut()
    {
        _cursorHideTimer?.Stop();
        
        int steps = 10;
        int currentStep = 0;
        double startOpacity = SpectrumOverlay.SpCsOpacity;
        
        _cursorFadeTimer = new DispatcherTimer();
        _cursorFadeTimer.Interval = TimeSpan.FromMilliseconds(50);
        _cursorFadeTimer.Tick += (sender, eventArgs) =>
        {
            currentStep++;
            double opacity = startOpacity * (1.0 - (double)currentStep / steps);
            if (opacity < 0) opacity = 0;
            
            SpectrumOverlay.SpCsOpacity = opacity;
            WaterfallOverlay.SpCsOpacity = opacity;
            
            if (currentStep >= steps)
            {
                _cursorFadeTimer?.Stop();
                IsMouseCursorHidden = false;
            }
        };
        _cursorFadeTimer?.Start();
    }
}
