using System;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Renderers;
using CommunityToolkit.Mvvm.Input;
using SRdeck.Messages;

namespace SRdeck.ViewModels;

public class SpanOption
{
    public int Value { get; set; }
    public string Label { get; set; } = "";
}

public class ZoomOverlayViewModel : ObservableObject
{
    private double _zwLeft;
    public double ZwLeft { get => _zwLeft; set => SetProperty(ref _zwLeft, value); }

    private double _zwTop;
    public double ZwTop { get => _zwTop; set => SetProperty(ref _zwTop, value); }

    private Visibility _zwVisible = Visibility.Hidden;
    public Visibility ZwVisible { get => _zwVisible; set => SetProperty(ref _zwVisible, value); }

    private int _receiverIndex = 1;
    public int ReceiverIndex { get => _receiverIndex; set => SetProperty(ref _receiverIndex, value); }

    private bool _userPreferredEmbedded;
    public bool UserPreferredEmbedded
    {
        get => _userPreferredEmbedded;
        set => SetProperty(ref _userPreferredEmbedded, value);
    }

    private bool _isEmbeddedLocked;
    public bool IsEmbeddedLocked
    {
        get => _isEmbeddedLocked;
        set
        {
            if (SetProperty(ref _isEmbeddedLocked, value))
            {
                if (value)
                {
                    IsEmbedded = true;
                }
                else
                {
                    IsEmbedded = UserPreferredEmbedded;
                }
                _toggleEmbedCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    private bool _isEmbedded;
    public bool IsEmbedded 
    { 
        get => _isEmbedded; 
        set {
            if (IsEmbeddedLocked) value = true;
            if (SetProperty(ref _isEmbedded, value)) {
                OnPropertyChanged(nameof(EmbedToggleButtonText));
                if (!IsEmbeddedLocked)
                {
                    UserPreferredEmbedded = value;
                }
            }
        }
    }

    public string EmbedToggleButtonText => IsEmbedded ? "↑" : "↓";

    private IRelayCommand? _toggleEmbedCommand;
    public IRelayCommand ToggleEmbedCommand => _toggleEmbedCommand ??= new RelayCommand(() => IsEmbedded = !IsEmbedded, () => !IsEmbeddedLocked);

    public SpanOption[] SpanOptions => new int[] { 10000, 20000, 50000, 100000, 250000 }
        .Select(spanValue => new SpanOption { Value = spanValue, Label = $"{(spanValue / 1000.0).ToString("0.#")} kHz" })
        .ToArray();

    private int _selectedSpan = 50000;
    public int SelectedSpan
    {
        get => _selectedSpan;
        set
        {
            if (SetProperty(ref _selectedSpan, value) && value > 0)
            {
                WeakReferenceMessenger.Default.Send(new ZoomSpanChangeMessage(_receiverIndex, value));
            }
        }
    }

    private double _zoomWindowWidth = 418;
    public double ZoomWindowWidth { get => _zoomWindowWidth; set => SetProperty(ref _zoomWindowWidth, value); }

    private double _zoomWindowHeight = 151;
    public double ZoomWindowHeight { get => _zoomWindowHeight; set => SetProperty(ref _zoomWindowHeight, value); }

    private double _zwBandLeft;
    public double ZwBandLeft { get => _zwBandLeft; set => SetProperty(ref _zwBandLeft, value); }

    private double _zwBandWidth;
    public double ZwBandWidth { get => _zwBandWidth; set => SetProperty(ref _zwBandWidth, value); }

    private Visibility _zwBandVisible = Visibility.Hidden;
    public Visibility ZwBandVisible { get => _zwBandVisible; set => SetProperty(ref _zwBandVisible, value); }

    private string _zwTunedFreqText = "";
    public string ZwTunedFreqText { get => _zwTunedFreqText; set => SetProperty(ref _zwTunedFreqText, value); }

    private string _zwSpanKHzText = "";
    public string ZwSpanKHzText { get => _zwSpanKHzText; set => SetProperty(ref _zwSpanKHzText, value); }

    private double _zwBtnLX;
    public double ZwBtnLX { get => _zwBtnLX; set => SetProperty(ref _zwBtnLX, value); }

    private double _zwBtnRX;
    public double ZwBtnRX { get => _zwBtnRX; set => SetProperty(ref _zwBtnRX, value); }

    private double _zwBtnCX;
    public double ZwBtnCX { get => _zwBtnCX; set => SetProperty(ref _zwBtnCX, value); }

    private Visibility _zwBtnVisible = Visibility.Visible;
    public Visibility ZwBtnVisible { get => _zwBtnVisible; set => SetProperty(ref _zwBtnVisible, value); }

    public (double x, double y, bool visible) GetDesiredLayout(RadioControl radioControl, double zoomWidth, double zoomHeight, double waterfallWidth, double waterfallHeight, int receiverIndex = 1, bool isReceiverVisible = true, double totalHistorySeconds = WaterfallTimeModel.TotalHistorySeconds)
    {
        bool isPowerOn = radioControl.IsPowerOn;
        int freqOffsetHz = radioControl.FreqOffsetHz;
        int historySec = radioControl.HistorySec;
        
        bool isZoomVis = radioControl.IsZoomWindowVisible;
        if (isPowerOn && isReceiverVisible && isZoomVis)
        {
            if (!IsEmbedded)
            {
                zoomWidth = 418;
                zoomHeight = 198;
                float effectiveBandwidthHz = radioControl.MainSpanHz > 0 ? radioControl.MainSpanHz : 7000000f;
                double centerPositionX = RenderUtils.FreqToX(freqOffsetHz, (float)waterfallWidth, effectiveBandwidthHz);
                double desiredLeftX = centerPositionX - zoomWidth / 2.0;

            double desiredTopY = RenderUtils.SecToY(historySec + 20, (float)waterfallHeight, totalHistorySeconds) - waterfallHeight;

            if (desiredTopY < -waterfallHeight) desiredTopY = -waterfallHeight;
            else if (desiredTopY > -zoomHeight - 5) desiredTopY = -zoomHeight - 5;

            if (desiredLeftX < 30) desiredLeftX = 30;

            if (desiredLeftX < 30) desiredLeftX = 30;
            else if (desiredLeftX + zoomWidth > waterfallWidth - 30) desiredLeftX = waterfallWidth - zoomWidth - 30;

                return (desiredLeftX, desiredTopY, true);
            }
            else
            {
                // Embedded mode: Still visible but position is managed by ReceiverView
                return (0, 0, true);
            }
        }
        return (0, 0, false);
    }

    public void SyncLayout(double left, double top, bool visible)
    {
        ZwLeft = left;
        ZwTop = top;
        ZwVisible = visible ? Visibility.Visible : Visibility.Hidden;
    }

    public void SyncOverlayLayout(RadioControl radioControl, double zoomWidth, double zoomHeight, double waterfallWidth, double waterfallHeight, int receiverIndex = 1, bool isReceiverVisible = true, double totalHistorySeconds = WaterfallTimeModel.TotalHistorySeconds)
    {
        var (left, top, visible) = GetDesiredLayout(radioControl, zoomWidth, zoomHeight, waterfallWidth, waterfallHeight, receiverIndex, isReceiverVisible, totalHistorySeconds);
        SyncLayout(left, top, visible);
        SyncOverlayContent(radioControl, zoomWidth, receiverIndex);
    }

    public void SyncOverlayContent(RadioControl radioControl, double zoomWidth, int receiverIndex)
    {
        double bandwidthHz = radioControl.DemodMode switch
        {
            DemodulationMode.USB => 500,
            DemodulationMode.LSB => 500,
            DemodulationMode.USB_Wide => 3000,
            DemodulationMode.LSB_Wide => 3000,
            DemodulationMode.AM => 6000,
            DemodulationMode.AM_Wide => 11000,
            DemodulationMode.FM_Narrow => 15000,
            DemodulationMode.FM_Wide => 200000,
            _ => 3000
        };

        int spanHz = radioControl.SpanHz;
        int tunedFreqHz = radioControl.TunedFreqHz;

        if (zoomWidth > 0 && spanHz > 0) {
            ZwBtnLX = (zoomWidth * 0.15) - 20;
            ZwBtnRX = (zoomWidth * 0.85) - 20;
            ZwBtnCX = (zoomWidth * 0.50) - 20;

            double zoomBandwidth = bandwidthHz * zoomWidth / spanHz;
            ZwBandVisible = Visibility.Visible;
            ZwBandLeft = (zoomWidth / 2.0) - (zoomBandwidth / 2.0);
            ZwBandWidth = zoomBandwidth;
            
            ZwTunedFreqText = tunedFreqHz.ToString("#,0") + " Hz";
            ZwSpanKHzText = (spanHz / 10000.0).ToString("f3") + " kHz/div";
            
            if (_selectedSpan != spanHz)
            {
                _selectedSpan = spanHz;
                OnPropertyChanged(nameof(SelectedSpan));
            }
        } else {
            ZwBandVisible = Visibility.Hidden;
        }

        // Hide navigation buttons when embedded as they are not needed and positioning is off
        ZwBtnVisible = IsEmbedded ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool _isHighResMode;
    public bool IsHighResMode { get => _isHighResMode; set => SetProperty(ref _isHighResMode, value); }
}
