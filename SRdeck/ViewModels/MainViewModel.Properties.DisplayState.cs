using System.Windows;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // --- UI State & Flags ---
    [ObservableProperty] private bool _isHelpVisible = false;
    [ObservableProperty] private bool _isSdrToggleEnabled;
    [ObservableProperty] private string _windowTitle = AppConstants.DEFAULT_WINDOW_TITLE;
    [ObservableProperty] private string _deviceModel = string.Empty;
    [ObservableProperty] private string _deviceSn = string.Empty;
    [ObservableProperty] private string _deviceName = "Searching...";

    public Visibility DeviceVisibility => (!string.IsNullOrEmpty(DeviceModel) || !string.IsNullOrEmpty(DeviceSn)) ? Visibility.Visible : Visibility.Collapsed;
    partial void OnDeviceModelChanged(string value) => OnPropertyChanged(nameof(DeviceVisibility));
    partial void OnDeviceSnChanged(string value) => OnPropertyChanged(nameof(DeviceVisibility));

    partial void OnWindowTitleChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.Contains("[") && value.Contains("]"))
        {
            int start = value.IndexOf("[");
            int end = value.IndexOf("]");
            if (end > start)
            {
                string dev = value.Substring(start + 1, end - start - 1).Trim();
                if (dev.Contains(",") && dev.Contains("S/N:"))
                {
                    var parts = dev.Split(',');
                    DeviceModel = parts[0].Trim();
                    DeviceSn = " (" + parts[1].Trim() + ")";
                }
                else
                {
                    DeviceModel = dev;
                }
            }
        }
        else if (value == AppConstants.DEFAULT_WINDOW_TITLE || value == "SRdeck")
        {
            DeviceModel = string.Empty;
        }
    }

    [ObservableProperty] private bool _isReceiver1Visible = true;
    [ObservableProperty] private bool _isDelayActive;
    [ObservableProperty] private bool _isBandPlanVisible;
    [ObservableProperty] private bool _isStationNameVisible = true;

    public bool IsStarted => SdrControl?.IsStarted ?? false;
    public bool IsStopped => SdrControl?.IsStopped ?? true;
    public bool IsSampleRateSelectionEnabled => IsStopped && !IsRtlDevice;
    public bool IsAnySourceActive => IsStarted;
    public bool IsSdrActive => SdrControl?.StartButtonText == "動作中";

    // --- Display Options ---
    public ObservableCollection<FrequencyDisplayOption> FrequencyDisplayOptions { get; } = new();
    [ObservableProperty] private FrequencyDisplayOption? _selectedFrequencyDisplayOption;

    [ObservableProperty] private DemodWaveMode _demodWaveDisplayMode;
    [ObservableProperty] private DemodWaveMode _demodWaveDisplayMode2;

    public string DemodWaveTimeLabel => DemodWaveDisplayMode switch
    {
        DemodWaveMode.FFT => "1/3 Oct",
        DemodWaveMode.Lissajous => "500 msec",
        DemodWaveMode.Vector => "500 msec",
        DemodWaveMode.Compare => "500 msec",
        _ => "500 msec"
    };

    public string DemodWaveTimeLabel2 => DemodWaveDisplayMode2 switch
    {
        DemodWaveMode.FFT => "1/3 Oct",
        DemodWaveMode.Lissajous => "500 msec",
        DemodWaveMode.Vector => "500 msec",
        DemodWaveMode.Compare => "500 msec",
        _ => "500 msec"
    };

    partial void OnDemodWaveDisplayModeChanged(DemodWaveMode value) => OnPropertyChanged(nameof(DemodWaveTimeLabel));
    partial void OnDemodModeChanged(DemodulationMode value) => OnPropertyChanged(nameof(DemodWaveTimeLabel));
    partial void OnDemodWaveDisplayMode2Changed(DemodWaveMode value) => OnPropertyChanged(nameof(DemodWaveTimeLabel2));

    partial void OnIsBandPlanVisibleChanged(bool value)
    {
        if (_engine == null || _isUpdatingDisplayOption) return;
        _isUpdatingDisplayOption = true;
        try
        {
            RadioControl p = _engine.Control;
            p.IsBandPlanVisible = value;
            _engine.Control = p;
            if (Display != null) Display.IsBandPlanVisible = value;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(p));
            SyncSelectedFrequencyDisplayOption();
            PersistFrequencyDisplayModeState();
        }
        finally { _isUpdatingDisplayOption = false; }
    }

    partial void OnIsStationNameVisibleChanged(bool value)
    {
        if (_engine == null || _isUpdatingDisplayOption) return;
        _isUpdatingDisplayOption = true;
        try
        {
            RadioControl p = _engine.Control;
            p.IsStationNameVisible = value;
            _engine.Control = p;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(p));
            SyncSelectedFrequencyDisplayOption();
            PersistFrequencyDisplayModeState();
        }
        finally { _isUpdatingDisplayOption = false; }
    }

    private void PersistFrequencyDisplayModeState()
    {
        _lastState.FrequencyDisplayMode = (IsBandPlanVisible, IsStationNameVisible) switch
        {
            (true, true) => FrequencyDisplayMode.Both,
            (true, false) => FrequencyDisplayMode.BandOnly,
            (false, true) => FrequencyDisplayMode.StationOnly,
            _ => FrequencyDisplayMode.None
        };
        _lastStateService.SaveLastState(_lastState);
    }

    private bool _isUpdatingDisplayOption;
    partial void OnSelectedFrequencyDisplayOptionChanged(FrequencyDisplayOption? value)
    {
        if (value == null || _engine == null || _isUpdatingDisplayOption) return;
        _isUpdatingDisplayOption = true;
        try
        {
            RadioControl p = _engine.Control;
            switch (value.Mode)
            {
                case FrequencyDisplayMode.Both: p.IsBandPlanVisible = true; p.IsStationNameVisible = true; break;
                case FrequencyDisplayMode.BandOnly: p.IsBandPlanVisible = true; p.IsStationNameVisible = false; break;
                case FrequencyDisplayMode.StationOnly: p.IsBandPlanVisible = false; p.IsStationNameVisible = true; break;
                case FrequencyDisplayMode.None: p.IsBandPlanVisible = false; p.IsStationNameVisible = false; break;
            }
            IsBandPlanVisible = p.IsBandPlanVisible;
            IsStationNameVisible = p.IsStationNameVisible;
            if (Display != null) Display.IsBandPlanVisible = p.IsBandPlanVisible;
            _engine.Control = p;
            _lastState.FrequencyDisplayMode = value.Mode;
            _lastStateService.SaveLastState(_lastState);
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(p));
        }
        finally { _isUpdatingDisplayOption = false; }
    }

    partial void OnIsReceiver1VisibleChanged(bool value)
    {
        if (_engine == null) return;
        RadioControl p = _engine.Control;
        p.IsR1Visible = value;
        if (!value) { p.IsPowerOn = false; p.IsSpeakerOn = false; p.IsZoomWindowVisible = false; }
        _engine.Control = p;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(p));
    }

    public void SyncButtonStates()
    {
        IsSdrToggleEnabled = IsSdrDetected && !IsDetectingSdr;
    }
}
