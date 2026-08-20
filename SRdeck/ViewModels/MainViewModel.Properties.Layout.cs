using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Renderers;
using SRdeckPlugin.Contracts;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private double _demodWaveWidth = 10;
    [ObservableProperty] private double _demodWaveHeight = 10;

    public double SpectrumWidth
    {
        get => SpectrumOverlay.SpectrumWidth;
        set
        {
            SpectrumOverlay.SpectrumWidth = value;
            SyncState(_engine.Control, _engine.State);
            UiTick?.Invoke(this, EventArgs.Empty);
        }
    }
    public double SpectrumHeight
    {
        get => SpectrumOverlay.SpectrumHeight;
        set
        {
            SpectrumOverlay.SpectrumHeight = value;
            SyncState(_engine.Control, _engine.State);
            UiTick?.Invoke(this, EventArgs.Empty);
        }
    }
    public double WaterfallWidth
    {
        get => WaterfallOverlay.WaterfallWidth;
        set
        {
            WaterfallOverlay.WaterfallWidth = value;
            SyncState(_engine.Control, _engine.State);
            UiTick?.Invoke(this, EventArgs.Empty);
        }
    }
    public double WaterfallHeight
    {
        get => WaterfallOverlay.WaterfallHeight;
        set
        {
            WaterfallOverlay.WaterfallHeight = value;
            SyncState(_engine.Control, _engine.State);
            UiTick?.Invoke(this, EventArgs.Empty);
        }
    }
    public double ZoomWindowWidth { get => ZoomOverlay.ZoomWindowWidth; set { ZoomOverlay.ZoomWindowWidth = value; } }
    public double ZoomWindowHeight { get => ZoomOverlay.ZoomWindowHeight; set { ZoomOverlay.ZoomWindowHeight = value; } }

    [ObservableProperty] private double _spActualWidth;
    [ObservableProperty] private double _wfActualWidth;
    [ObservableProperty] private double _waterfallRasterHeight = 10;
    [ObservableProperty] private WaterfallTimeMode _waterfallDisplayTimeMode = WaterfallTimeMode.ThreeMinutes;

    public double CurrentWaterfallHistorySeconds => WaterfallTimeModel.GetTotalHistorySeconds(
        WaterfallDisplayTimeMode,
        WaterfallRasterHeight > 0 ? WaterfallRasterHeight : WaterfallHeight);

    partial void OnSpActualWidthChanged(double value) => SyncState(_engine.Control, _engine.State);
    partial void OnWfActualWidthChanged(double value) => SyncState(_engine.Control, _engine.State);
    partial void OnWaterfallRasterHeightChanged(double value)
    {
        OnPropertyChanged(nameof(CurrentWaterfallHistorySeconds));
        SyncState(_engine.Control, _engine.State);
    }

    partial void OnWaterfallDisplayTimeModeChanged(WaterfallTimeMode value)
    {
        OnPropertyChanged(nameof(CurrentWaterfallHistorySeconds));
        WeakReferenceMessenger.Default.Send(new ResetWaterfallTimingMessage());
        SyncState(_engine.Control, _engine.State);
    }
}
