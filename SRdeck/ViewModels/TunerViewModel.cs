using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models;
using SRdeck.DSP;
using SRdeck.Configuration;

namespace SRdeck.ViewModels;

public sealed partial class TunerViewModel : ObservableObject
{
    private readonly ISdrEngine _engine;
    private int _lastBuiltCenterFreq = -1;
    public int Index { get; }

    public TunerViewModel(ISdrEngine engine, int index = 1)
    {
        _engine = engine;
        Index = index;
        if (_engine.InitialAppSettings != null)
        {
            LoadModeButtonSettings(_engine.InitialAppSettings);
        }
    }

    [ObservableProperty]
    private ObservableCollection<CenterFreqDigit> _centerFreqDigits = new ObservableCollection<CenterFreqDigit>();

    [ObservableProperty]
    private string _centerFreqDisplay = "";

    public int CenterFreqRoundingHz { get; set; } = 500000;

    public void BuildCenterFreqDigits(int? centerFreqOverrideHz = null)
    {
        RadioControl radioControl = _engine.Control;
        int centerFreqHz = centerFreqOverrideHz ?? 0;
        if (!centerFreqOverrideHz.HasValue && _syncSuppressCounter > 0)
        {
            int span = radioControl.MainSpanHz > 0 ? radioControl.MainSpanHz : radioControl.BaseMainSpanHz;
            int roundingHz;
            if (span <= 1000000) roundingHz = 100000;
            else if (span <= 2400000) roundingHz = 200000;
            else if (span <= 4000000) roundingHz = 500000;
            else if (span <= 8000000) roundingHz = 500000;
            else if (span <= 16000000) roundingHz = 1000000;
            else if (span <= 32000000) roundingHz = 2000000;
            else roundingHz = 4000000;

            centerFreqHz = (int)(((long)radioControl.CenterFreqHz + roundingHz / 2) / roundingHz * roundingHz);
        }
        else if (!centerFreqOverrideHz.HasValue)
        {
            centerFreqHz = _engine.SdrCenterFreqHz;
        }
        
        _lastBuiltCenterFreq = centerFreqHz;
        CenterFreqDisplay = centerFreqHz.ToString("#,0") + " Hz";
        
        string freqString = centerFreqHz.ToString("0,000,000,000"); // 13 chars (e.g. "0,080,000,000")
        char[] freqChars = freqString.ToCharArray();
        bool isNonZeroSeen = false;
        for (int i = 0; i < freqChars.Length - 1; i++) // Keep at least the last digit
        {
            if (freqChars[i] >= '1' && freqChars[i] <= '9') isNonZeroSeen = true;
            if (!isNonZeroSeen)
            {
                if (freqChars[i] == '0' || freqChars[i] == ',') freqChars[i] = ' ';
            }
        }
        freqString = new string(freqChars);

        var newDigits = new ObservableCollection<CenterFreqDigit>();
        int placeValue = 1;
        
        for (int i = freqString.Length - 1; i >= 0; i--)
        {
            char digitChar = freqString[i];
            bool isCommaPosition = (i == 1 || i == 5 || i == 9);

            if (isCommaPosition)
            {
                newDigits.Insert(0, new CenterFreqDigit { Char = digitChar.ToString(), PlaceValue = 0 });
                continue;
            }

            var digitItem = new CenterFreqDigit 
            { 
                Char = digitChar.ToString(), 
                PlaceValue = placeValue
            };

            int frequencyDeltaHz = placeValue;
            if (placeValue == 100000)
            {
                frequencyDeltaHz = CenterFreqRoundingHz;
            }

            digitItem.IncrementCommand = new RelayCommand(() => AdjustCenterFrequency(frequencyDeltaHz));
            digitItem.DecrementCommand = new RelayCommand(() => AdjustCenterFrequency(-frequencyDeltaHz));
            newDigits.Insert(0, digitItem);
            
            placeValue *= 10;
        }
        CenterFreqDigits = newDigits;
    }

    public void SyncFrequencyFromAppliedControl(RadioControl radioControl)
    {
        if (_syncSuppressCounter > 0)
        {
            _syncSuppressCounter--;
            return;
        }

        int centerFreqHz = radioControl.CenterFreqHz;
        CenterFreqDisplay = centerFreqHz.ToString("#,0") + " Hz";
        if (_lastBuiltCenterFreq != centerFreqHz)
            BuildCenterFreqDigits(centerFreqHz);
        TunedFreqHz = radioControl.TunedFreqHz;
    }

    private void AdjustCenterFrequency(int frequencyDeltaHz)
    {
        BeginSyncSuppression();
        RadioControl radioControl = _engine.Control;
        radioControl.CenterFreqHz += frequencyDeltaHz;
        radioControl.ApplyPrimaryReceiverTuning();
        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        BuildCenterFreqDigits();
    }

    public void SyncFromCore(RadioControl radioControl)
    {
        if (_syncSuppressCounter > 0)
        {
            _syncSuppressCounter--;
            return;
        }

        int currentFreqHz = _engine.SdrCenterFreqHz;
        CenterFreqDisplay = currentFreqHz.ToString("#,0") + " Hz";
        if (_lastBuiltCenterFreq != currentFreqHz)
        {
            BuildCenterFreqDigits();
        }

        IsPowerOn = radioControl.IsPowerOn;
        IsSpeakerOn = radioControl.IsSpeakerOn;
        IsSquelchOn = radioControl.IsSquelchOn;
        SquelchDb = radioControl.SquelchDb;
        SpanHz = radioControl.SpanHz;
        DemodMode = radioControl.DemodMode;
        SyncModeIndexFromCore(radioControl.DemodMode);
        StepHz = radioControl.StepHz;
        TunedFreqHz = radioControl.TunedFreqHz;
        HistorySec = radioControl.HistorySec;
        IsMonoMode = radioControl.IsMonoMode;
    }

    public void SyncState(RadioState radioState)
    {
        RxRssi = radioState.RxRssi;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalPpmDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalPpm))]
    private float _ppmAdjustment = 0f;

    partial void OnPpmAdjustmentChanged(float value)
    {
        BeginSyncSuppression();
        RadioControl radioControl = _engine.Control;
        radioControl.AdjustmentPpm = value;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
    }

    [ObservableProperty]
    private float _pilotLevel = 0f;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalPpmDisplay))]
    [NotifyPropertyChangedFor(nameof(TotalPpm))]
    private float _basePpm = 0f;

    public float TotalPpm
    {
        get => BasePpm + PpmAdjustment;
        set
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return;
            PpmAdjustment = value - BasePpm;
        }
    }

    public string TotalPpmDisplay => (BasePpm + PpmAdjustment).ToString("0.00");

    [ObservableProperty] private long _tunedFreqHz;
    [ObservableProperty] private int _historySec;
    [ObservableProperty] private float _rxRssi;
    [ObservableProperty] private int _squelchDb;
    [ObservableProperty] private bool _isPowerOn;
    [ObservableProperty] private bool _isSpeakerOn;
    [ObservableProperty] private bool _isSquelchOn;
    [ObservableProperty] private bool _isOtherReceiverVisible;
    [ObservableProperty] private bool _isOtherSpeakerOn;
    [ObservableProperty] private int _spanHz;
    [ObservableProperty] private DemodulationMode _demodMode;
    [ObservableProperty] private bool _isStereo;
    [ObservableProperty] private bool _isMonoMode;
    [ObservableProperty] private int _stepHz;

    [RelayCommand]
    private void AdjustPpmCorrection(object deltaValueObject)
    {
        if (deltaValueObject == null || !float.TryParse(deltaValueObject.ToString(), out var deltaPpmValue)) return;
        PpmAdjustment += deltaPpmValue;
    }

    public class ModeOption : ObservableObject
    {
        public string Label { get; set; } = "";
        public DemodulationMode InternalMode { get; set; }
        public int Index { get; set; }
    }

    public partial class CompactModeOption : ObservableObject
    {
        [ObservableProperty] private string _label = "";
        [ObservableProperty] private bool _isActive = false;
        [ObservableProperty] private bool _isEnabled = true;
        public int ButtonIndex { get; set; }
        public string DefaultLabel { get; set; } = "";
        public int Mode1 { get; set; } = -1;
        public int Mode2 { get; set; } = -1;
        public int Mode3 { get; set; } = -1;
    }

    [ObservableProperty]
    private ObservableCollection<ModeOption> _modeOptions = [];


    public ObservableCollection<CompactModeOption> CompactModeOptions { get; } = new();

    public void LoadModeButtonSettings(AppSettings settings)
    {
        if (settings.ModeButtons == null || settings.ModeButtons.Count < 8) return;

        CompactModeOptions.Clear();
        for (int i = 0; i < 8; i++)
        {
            var buttonConfig = settings.ModeButtons[i];
            CompactModeOptions.Add(new CompactModeOption
            {
                ButtonIndex = i,
                Label = buttonConfig.DefaultLabel,
                DefaultLabel = buttonConfig.DefaultLabel,
                Mode1 = NormalizeModeIndex(buttonConfig.Mode1),
                Mode2 = NormalizeModeIndex(buttonConfig.Mode2),
                Mode3 = NormalizeModeIndex(buttonConfig.Mode3)
            });
        }
        SyncCompactModeLabels();
    }

    public bool IsCompactModeActive(CompactModeOption? option)
    {
        if (option == null) return false;
        return option.IsActive;
    }

    [ObservableProperty]
    private int _selectedModeIndex = -1;


    private void SyncCompactModeLabels()
    {
        foreach (var option in CompactModeOptions)
        {
            option.IsEnabled = option.Mode1 >= 0;
            bool isActive = option.IsEnabled && (SelectedModeIndex == option.Mode1 || 
                            (option.Mode2 >= 0 && SelectedModeIndex == option.Mode2) || 
                            (option.Mode3 >= 0 && SelectedModeIndex == option.Mode3));
            option.IsActive = isActive;
            if (isActive && SelectedModeIndex >= 0 && SelectedModeIndex < ModeOptions.Count)
            {
                option.Label = ModeOptions[SelectedModeIndex].Label;
            }
            else
            {
                option.Label = option.DefaultLabel;
            }
        }
    }


    private bool _isSyncingMode = false;
    private int _syncSuppressCounter = 0;
    private void BeginSyncSuppression() => _syncSuppressCounter = 5; // 100ms * 5 = 500ms

    partial void OnSelectedModeIndexChanged(int value)
    {
        if (_isSyncingMode || value < 0 || value >= ModeOptions.Count) return;
        BeginSyncSuppression();
        var selectedOption = ModeOptions[value];
        RadioControl radioControl = _engine.Control;
        
        DemodulationMode targetMode = selectedOption.InternalMode;
        DemodulationMode oldMode = radioControl.DemodMode;
        int currentSpanHz = radioControl.SpanHz;

        int targetStepHz = 25000;
        switch (targetMode)
        {
            case DemodulationMode.USB: 
            case DemodulationMode.LSB: 
            case DemodulationMode.USB_Wide: 
            case DemodulationMode.LSB_Wide:
                targetStepHz = 100;
                break;
            case DemodulationMode.AM_Wide: 
                targetStepHz = 1000;
                break;
            case DemodulationMode.FM_Wide: 
                targetStepHz = 100000;
                break;
            case DemodulationMode.AM: 
            case DemodulationMode.FM_Narrow: 
                targetStepHz = 25000;
                break;
        }

        int targetSpanHz = currentSpanHz;
        bool wasFmWide = oldMode == DemodulationMode.FM_Wide;
        bool isFmWide = targetMode == DemodulationMode.FM_Wide;
        if (isFmWide && !wasFmWide)
        {
            targetSpanHz = 250000;
        }
        else if (!isFmWide && wasFmWide)
        {
            targetSpanHz = 50000;
        }

        radioControl.DemodMode = targetMode;
        radioControl.SpanHz = targetSpanHz;
        radioControl.StepHz = targetStepHz;
        radioControl.ApplyPrimaryReceiverTuning();
        
        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
    }

    [RelayCommand]
    private void CycleModeButtonSelection(int buttonIndex)
    {
        if (buttonIndex < 0 || buttonIndex >= CompactModeOptions.Count) return;
        var option = CompactModeOptions[buttonIndex];
        if (option.Mode1 < 0) return;

        BeginSyncSuppression();
        int targetIndex = option.Mode1;

        if (SelectedModeIndex == option.Mode1)
        {
            if (option.Mode2 >= 0) targetIndex = option.Mode2;
            else targetIndex = option.Mode1;
        }
        else if (SelectedModeIndex == option.Mode2 && option.Mode2 >= 0)
        {
            if (option.Mode3 >= 0) targetIndex = option.Mode3;
            else targetIndex = option.Mode1;
        }
        else if (SelectedModeIndex == option.Mode3 && option.Mode3 >= 0)
        {
            targetIndex = option.Mode1;
        }
        else
        {
            targetIndex = option.Mode1;
        }

        SelectedModeIndex = targetIndex;
        SyncCompactModeLabels();
    }

    private void SyncModeIndexFromCore(DemodulationMode mode)
    {
        _isSyncingMode = true;
        try
        {
            for (int i = 0; i < ModeOptions.Count; i++)
            {
                if (ModeOptions[i].InternalMode == mode)
                {
                    SelectedModeIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _isSyncingMode = false;
        }

        SyncCompactModeLabels();
    }

    partial void OnIsMonoModeChanged(bool value)
    {
        BeginSyncSuppression();
        RadioControl radioControl = _engine.Control;
        radioControl.IsMonoMode = value;
        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
    }

    [RelayCommand]
    private void ToggleMonoMode()
    {
        IsMonoMode = !IsMonoMode;
    }

    private int NormalizeModeIndex(int index) => index >= 0 && index < ModeOptions.Count ? index : -1;
}

public partial class CenterFreqDigit : ObservableObject
{
    [ObservableProperty] private string _char = "";
    [ObservableProperty] private int _placeValue;
    public bool IsClickable => PlaceValue >= 100000;
    public IRelayCommand? IncrementCommand { get; set; }
    public IRelayCommand? DecrementCommand { get; set; }
}
