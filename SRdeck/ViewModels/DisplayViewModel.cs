using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using System.Collections.ObjectModel;
using System.Linq;

namespace SRdeck.ViewModels;

public sealed partial class DisplayViewModel : ObservableObject
{
    public const int DefaultFixedMainSpanHz = 7_000_000;
    public const int RtlFixedMainSpanHz = 2_000_000;

    public class ZoomModeOption
    {
        public int Value { get; set; }
        public string Label { get; set; } = "";
    }

    public List<ZoomModeOption> ZoomModeOptions { get; } = new()
    {
        new() { Value = 0, Label = "AUTO" },
        new() { Value = 1, Label = "NORM" },
        new() { Value = 2, Label = "HI" }
    };

    public class MainSpanOption
    {
        public int ValueHz { get; set; }
        public string Label { get; set; } = "";
    }

    public List<MainSpanOption> MainSpanOptions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMainSpanHz))]
    [NotifyPropertyChangedFor(nameof(BaseMainSpanHz))]
    [NotifyPropertyChangedFor(nameof(IsMainViewZoomed))]
    [NotifyPropertyChangedFor(nameof(CurrentMainRoundingHz))]
    [NotifyPropertyChangedFor(nameof(SelectedMainSpanHz))]
    [NotifyPropertyChangedFor(nameof(SelectedMainSpanOption))]
    private int _mainSpanIndex = 0;

    private int _mainViewZoomSpanHz = 0;
    private int _fixedMainSpanHz = DefaultFixedMainSpanHz;

    public int BaseMainSpanHz => MainSpanOptions[Math.Clamp(MainSpanIndex, 0, MainSpanOptions.Count - 1)].ValueHz;
    public bool IsMainViewZoomed => _mainViewZoomSpanHz > 0 && _mainViewZoomSpanHz < BaseMainSpanHz;
    public int CurrentMainSpanHz => IsMainViewZoomed ? _mainViewZoomSpanHz : BaseMainSpanHz;
    public int? SelectedMainSpanHz
    {
        get => BaseMainSpanHz;
        set
        {
            if (!value.HasValue) return;
            int index = MainSpanOptions.FindIndex(option => option.ValueHz == value.Value);
            if (index < 0) return;
            if (MainSpanIndex == index)
            {
                SyncMainZoomSpanHz(0);
                return;
            }
            MainSpanIndex = index;
        }
    }

    public MainSpanOption? SelectedMainSpanOption
    {
        get => VisibleMainSpanOptions.FirstOrDefault(option => option.ValueHz == BaseMainSpanHz);
        set
        {
            if (value == null) return;
            SelectedMainSpanHz = value.ValueHz;
        }
    }

    public ObservableCollection<MainSpanOption> VisibleMainSpanOptions { get; } = new();

    partial void OnMainSpanIndexChanged(int value)
    {
        _mainViewZoomSpanHz = 0;
    }

    public void SyncMainSpanHz(int frequencyHz)
    {
        SelectedMainSpanHz = _fixedMainSpanHz;
    }

    public void SyncMainZoomSpanHz(int frequencyHz)
    {
        int baseSpanHz = BaseMainSpanHz;
        int nextZoomSpanHz = frequencyHz <= 0 || frequencyHz >= baseSpanHz
            ? 0
            : Math.Max(10_000, frequencyHz);
        if (_mainViewZoomSpanHz == nextZoomSpanHz) return;
        _mainViewZoomSpanHz = nextZoomSpanHz;
        OnPropertyChanged(nameof(CurrentMainSpanHz));
        OnPropertyChanged(nameof(CurrentMainRoundingHz));
        OnPropertyChanged(nameof(IsMainViewZoomed));
    }

    public int ApplyPreferredMainSpanHz(int? preferredSpanHz)
    {
        int baseSpanHz = BaseMainSpanHz;
        if (!preferredSpanHz.HasValue || preferredSpanHz.Value <= 0)
        {
            SyncMainZoomSpanHz(0);
            return CurrentMainSpanHz;
        }

        int minimumSpanHz = Math.Min(10_000, baseSpanHz);
        int resolvedSpanHz = Math.Clamp(preferredSpanHz.Value, minimumSpanHz, baseSpanHz);
        SyncMainZoomSpanHz(resolvedSpanHz);
        return CurrentMainSpanHz;
    }

    public int CurrentMainRoundingHz
    {
        get
        {
            int span = CurrentMainSpanHz;
            if (span <= 1000000) return 100000;
            if (span <= 2400000) return 200000;
            if (span <= 4000000) return 500000;
            if (span <= 8000000) return 500000;
            if (span <= 16000000) return 1000000;
            if (span <= 32000000) return 2000000;
            return 4000000;
        }
    }

    public DisplayViewModel()
    {
        SyncMainSpanOptionsForDevice(false, 8000000);
    }

    public void SyncMainSpanOptionsForDevice(bool isRtlDevice, int sampleRateHz)
    {
        _fixedMainSpanHz = isRtlDevice ? sampleRateHz : (int)(sampleRateHz * 0.875);

        MainSpanOptions.Clear();
        MainSpanOptions.Add(new MainSpanOption
        {
            ValueHz = _fixedMainSpanHz,
            Label = FormatMainSpanLabel(_fixedMainSpanHz)
        });

        VisibleMainSpanOptions.Clear();
        foreach (var option in MainSpanOptions) VisibleMainSpanOptions.Add(option);
        MainSpanIndex = 0;
        SelectedMainSpanHz = _fixedMainSpanHz;

        OnPropertyChanged(nameof(SelectedMainSpanOption));
    }

    private static string FormatMainSpanLabel(int spanHz)
    {
        if (spanHz % 1_000_000 == 0) return $"{spanHz / 1_000_000}M";
        if (spanHz % 100_000 == 0) return $"{spanHz / 1_000_000.0:0.#}M";
        return $"{spanHz / 1_000_000.0:0.##}M";
    }

    private bool _isBandPlanVisible = true;
    public bool IsBandPlanVisible
    {
        get => _isBandPlanVisible;
        set => SetProperty(ref _isBandPlanVisible, value);
    }
    [RelayCommand]
    private void ToggleBandPlan() => IsBandPlanVisible = !_isBandPlanVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubDisplayToggleText))]
    private bool _isSubDisplayVisible = true;
    public string SubDisplayToggleText => IsSubDisplayVisible ? "◀" : "▶";
    [RelayCommand]
    private void ToggleSubDisplay() => IsSubDisplayVisible = !IsSubDisplayVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubDisplayToggleText2))]
    private bool _isSubDisplayVisible2 = true;
    public string SubDisplayToggleText2 => IsSubDisplayVisible2 ? "◀" : "▶";
    [RelayCommand]
    private void ToggleSecondarySubDisplay() => IsSubDisplayVisible2 = !IsSubDisplayVisible2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomDisplayToggleText))]
    private bool _isZoomDisplayVisible = true;
    public string ZoomDisplayToggleText => IsZoomDisplayVisible ? "◀" : "▶";
    [RelayCommand]
    private void ToggleZoomDisplay() => IsZoomDisplayVisible = !IsZoomDisplayVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomDisplayToggleText2))]
    private bool _isZoomDisplayVisible2 = true;
    public string ZoomDisplayToggleText2 => IsZoomDisplayVisible2 ? "◀" : "▶";
    [RelayCommand]
    private void ToggleSecondaryZoomDisplay() => IsZoomDisplayVisible2 = !IsZoomDisplayVisible2;

    private void PublishBiasUpdate()
    {
        WeakReferenceMessenger.Default.Send(new BiasUpdateMessage(SpectrumBiasAdj, WaterfallBiasAdj, SpectrumZoomBiasAdj, WaterfallZoomBiasAdj));
    }

    [ObservableProperty]
    private int _spectrumBiasAdj = 0;
    partial void OnSpectrumBiasAdjChanged(int value) => PublishBiasUpdate();

    [ObservableProperty]
    private int _waterfallBiasAdj = 0;
    partial void OnWaterfallBiasAdjChanged(int value) => PublishBiasUpdate();

    [ObservableProperty]
    private int _spectrumZoomBiasAdj = 0;
    partial void OnSpectrumZoomBiasAdjChanged(int value) => PublishBiasUpdate();

    [ObservableProperty]
    private int _waterfallZoomBiasAdj = 0;
    partial void OnWaterfallZoomBiasAdjChanged(int value) => PublishBiasUpdate();

    [RelayCommand]
    private void AdjustSpectrumIntensityOffset(object deltaValueObject)
    {
        if (deltaValueObject == null || !int.TryParse(deltaValueObject.ToString(), out var deltaValue)) return;
        SpectrumBiasAdj += deltaValue;
    }

    [RelayCommand]
    private void AdjustWaterfallIntensityOffset(object deltaValueObject)
    {
        if (deltaValueObject == null || !int.TryParse(deltaValueObject.ToString(), out var deltaValue)) return;
        WaterfallBiasAdj += deltaValue;
    }

    [RelayCommand]
    private void ResetSpectrumBias() => SpectrumBiasAdj = 0;

    [RelayCommand]
    private void ResetWaterfallBias() => WaterfallBiasAdj = 0;

    [RelayCommand]
    private void AdjustSpectrumZoomIntensityOffset(object deltaValueObject)
    {
        if (deltaValueObject == null || !int.TryParse(deltaValueObject.ToString(), out var deltaValue)) return;
        SpectrumZoomBiasAdj += deltaValue;
    }

    [RelayCommand]
    private void AdjustWaterfallZoomIntensityOffset(object deltaValueObject)
    {
        if (deltaValueObject == null || !int.TryParse(deltaValueObject.ToString(), out var deltaValue)) return;
        WaterfallZoomBiasAdj += deltaValue;
    }

    [RelayCommand]
    private void ResetSpectrumZoomBias() => SpectrumZoomBiasAdj = 0;

    [RelayCommand]
    private void ResetWaterfallZoomBias() => WaterfallZoomBiasAdj = 0;

    [ObservableProperty]
    private int _zoomMode = 0; // 0: Auto, 1: Normal, 2: High-Res
    partial void OnZoomModeChanged(int value)
    {
        WeakReferenceMessenger.Default.Send(new ZoomModeUpdateMessage(0, value));
    }

    [RelayCommand]
    private void SyncZoomMode(object modeValueObject)
    {
        if (modeValueObject == null || !int.TryParse(modeValueObject.ToString(), out var mode)) return;
        ZoomMode = mode;
    }

    [RelayCommand]
    private void SyncMainSpan(object hzValueObject)
    {
        if (hzValueObject == null || !int.TryParse(hzValueObject.ToString(), out var frequencyHz)) return;
        SyncMainSpanHz(frequencyHz);
    }
}
