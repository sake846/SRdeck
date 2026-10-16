using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models;
using SRdeck.Services;

namespace SRdeck.ViewModels;

// Owns the main screen's subscriptions to external event sources and UI timers.
public partial class MainViewModel : ObservableObject
{
    private volatile bool _lifetimeEnded;

    private void HandleApplicationLanguageChanged(string value)
    {
        if (_lifetimeEnded) return;
        SyncWpfLanguageResource(value);
        RefreshSdrPlayNotchOptions();
    }

    private void HandleApplicationSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_lifetimeEnded) return;
        OnPropertyChanged(e.PropertyName);
        if (e.PropertyName == nameof(ApplicationSettingsViewModel.IsDisableWpfRenderingOnServer))
            OnPropertyChanged(nameof(IsCompactWpfMode));
    }

    private void HandleDisplayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_lifetimeEnded) return;
        if (e.PropertyName == nameof(DisplayViewModel.IsBandPlanVisible))
            IsBandPlanVisible = Display.IsBandPlanVisible;
        if (e.PropertyName == nameof(DisplayViewModel.CurrentMainSpanHz))
        {
            int r = Display.CurrentMainRoundingHz;
            if (Tuner != null)
            {
                Tuner.CenterFreqRoundingHz = r;
                Tuner.BuildCenterFreqDigits();
            }

            // Cursor-anchored zoom applies span and center together below.  Publishing the
            // intermediate "new span / old center" state makes the anchor visibly jump.
            if (_isApplyingAtomicMainViewUpdate) return;

            var p = _engine.Control;
            p.MainSpanHz = Display.CurrentMainSpanHz;
            p.BaseMainSpanHz = Display.BaseMainSpanHz;
            // ApplyPrimaryReceiverTuning clamps constraints based on the new dynamic MaxOffsetHz.
            p.ApplyPrimaryReceiverTuning();
            _engine.Control = p;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(p));
        }
    }

    private void HandleSdrControlPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_lifetimeEnded) return;
        if (e.PropertyName == nameof(SdrControlViewModel.StartButtonText))
        {
            OnPropertyChanged(nameof(IsSdrActive));
            SyncButtonStates();
        }
        if (e.PropertyName == nameof(SdrControlViewModel.IsStarted) ||
            e.PropertyName == nameof(SdrControlViewModel.IsStopped))
        {
            OnPropertyChanged(nameof(IsStarted));
            OnPropertyChanged(nameof(IsStopped));
            OnPropertyChanged(nameof(IsSampleRateSelectionEnabled));
            OnPropertyChanged(nameof(IsAnySourceActive));
            SyncSleepPrevention();
            if (e.PropertyName == nameof(SdrControlViewModel.IsStarted))
            {
                if (SdrControl.IsStarted) IsHelpVisible = false;
                if (SdrControl.IsStarted && !_engine.IsPlaying) ApplyStartupMainSpanSelection();
                _ = SyncPluginStreamingAsync(SdrControl.IsStarted);
            }
        }
    }

    private void HandleEngineTitleChanged(string? fileName) =>
        QueueWindowTitle(AppConstants.DEFAULT_WINDOW_TITLE + " [ " + FormatFilePath(fileName) + " ]");

    private void QueueWindowTitle(string title)
    {
        if (_lifetimeEnded) return;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!_lifetimeEnded) WindowTitle = title;
        });
    }

    private void HandlePowerStatusChanged(object? sender, EventArgs e)
    {
        if (_lifetimeEnded) return;
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!_lifetimeEnded) SyncSleepPrevention();
        });
    }

    private Task DetachLifetimeSubscriptionsAsync()
    {
        // MainWindow runs shutdown on a worker. UI timers and child view models
        // still belong to the dispatcher that constructed this screen.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(DetachLifetimeSubscriptions,
                System.Windows.Threading.DispatcherPriority.Send).Task;

        DetachLifetimeSubscriptions();
        return Task.CompletedTask;
    }

    private void DetachLifetimeSubscriptions()
    {
        PluginWorkspace.PropertyChanged -= OnPluginWorkspacePropertyChanged;
        ApplicationSettings.PowerSettingsChanged -= SyncSleepPrevention;
        ApplicationSettings.LanguageResourceChanged -= HandleApplicationLanguageChanged;
        ApplicationSettings.PropertyChanged -= HandleApplicationSettingsPropertyChanged;
        Display.PropertyChanged -= HandleDisplayPropertyChanged;
        SdrControl.PropertyChanged -= HandleSdrControlPropertyChanged;
        SdrControl.Dispose();
        Tuner.PropertyChanged -= OnPrimaryTunerPropertyChanged;
        _engine.StateUpdated -= HandleEngineStateUpdated;
        _engine.OnTitleChanged -= HandleEngineTitleChanged;
        PowerStateManager.PowerStatusChanged -= HandlePowerStatusChanged;
        if (_waterfallDisplayRequestNotifier is not null)
            _waterfallDisplayRequestNotifier.WaterfallDisplayRequestChanged -= OnWaterfallDisplayRequestChanged;
        _waterfallDisplayRequestNotifier = null;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _cursorHideTimer?.Stop();
        _cursorFadeTimer?.Stop();
        _cursorUpdateTimer?.Stop();
        _snapTimer?.Stop();
        _activityTimer?.Stop();
        _hasPendingCursorControl = false;
    }
}
