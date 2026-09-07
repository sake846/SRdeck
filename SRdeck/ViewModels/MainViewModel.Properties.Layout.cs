using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WaterfallDisplayTimeModeIndex))]
    [NotifyPropertyChangedFor(nameof(WaterfallDisplayTimeModeText))]
    private WaterfallTimeMode _waterfallDisplayTimeMode = WaterfallTimeMode.ThreeMinutes;

    public static readonly WaterfallTimeModeOption[] WaterfallTimeModeSequence =
    [
        new(WaterfallTimeMode.DoubleSpeed, "倍速"),
        new(WaterfallTimeMode.Uncompressed, "等速"),
        new(WaterfallTimeMode.ThreeMinutes, "3 分"),
        new(WaterfallTimeMode.FifteenMinutes, "15 分"),
        new(WaterfallTimeMode.OneHour, "60 分"),
    ];

    public IReadOnlyList<WaterfallTimeModeOption> WaterfallTimeModeOptions => WaterfallTimeModeSequence;

    public int WaterfallDisplayTimeModeIndex
    {
        get
        {
            for (int i = 0; i < WaterfallTimeModeSequence.Length; i++)
            {
                if (WaterfallTimeModeSequence[i].Mode == WaterfallDisplayTimeMode) return i;
            }
            return 2;
        }
        set
        {
            int index = Math.Clamp(value, 0, WaterfallTimeModeSequence.Length - 1);
            WaterfallDisplayTimeMode = WaterfallTimeModeSequence[index].Mode;
        }
    }

    public string WaterfallDisplayTimeModeText
    {
        get
        {
            foreach (var opt in WaterfallTimeModeSequence)
            {
                if (opt.Mode == WaterfallDisplayTimeMode) return opt.Label;
            }
            return "3 分";
        }
    }

    [RelayCommand]
    private void ResetWaterfallTimeMode() => WaterfallDisplayTimeMode = WaterfallTimeMode.ThreeMinutes;

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

public sealed record WaterfallTimeModeOption(WaterfallTimeMode Mode, string Label);

