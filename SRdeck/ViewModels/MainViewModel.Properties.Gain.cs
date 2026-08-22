using System;
using System.Collections.Generic;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Models.SDR;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private int _rfGainDb = 50;
    [ObservableProperty] private bool _isRtlDevice;

    [ObservableProperty] private string _gainPrimaryLabel = "GR";
    [ObservableProperty] private string _gainPrimaryUnit = "dB";
    [ObservableProperty] private string _gainSecondaryLabel = "LNA";
    [ObservableProperty] private string _gainSecondaryValue = "0";
    [ObservableProperty] private int _sdrPlaySensitivity = 50;
    [ObservableProperty] private string _sdrPlaySensitivityDescription = "標準";
    [ObservableProperty] private string _sdrPlayGainSummary = "LNA 0 / GR 50 dB / 最大ゲイン比 -30 dB";

    public string RtlSdrGainSummary =>
        $"{(IsAgcEnabled ? "自動" : "手動")} / GAIN {RtlSdrRfGainDb}";

    partial void OnRfGainDbChanged(int value)
    {
        SyncCurrentDeviceRfGain(value);
        SyncGainIndicatorText();
        RefreshSdrPlayGainPresentation();
    }

    private bool _isUpdatingSensitivityFromAgc;

    partial void OnSdrPlaySensitivityChanged(int value)
    {
        if (_isUpdatingSensitivityFromAgc) return;

        if (IsRtlDevice || _engine?.SdrDevice is not SRdeck.SDR.SdrController)
        {
            RefreshSdrPlayGainPresentation();
            return;
        }

        if (IsAgcEnabled)
        {
            SdrPlayRfGainDb = GetSdrPlayAutomaticNominalGainReduction();
            RefreshSdrPlayGainPresentation();
        }
        else
        {
            ApplySdrPlaySensitivity();
        }
    }

    public int SdrPlayRfGainDb
    {
        get => _sdrPlayRfGainDb;
        set
        {
            int clamped = Math.Clamp(value, 0, _engine.MaxGainReduction);
            if (_sdrPlayRfGainDb == clamped) return;
            _sdrPlayRfGainDb = clamped;
            OnPropertyChanged();
            if (!IsRtlDevice)
            {
                RfGainDb = clamped;
                ApplyGainToEngine(clamped);
            }
        }
    }

    public int RtlSdrRfGainDb
    {
        get => _rtlSdrRfGainDb;
        set
        {
            int clamped = Math.Clamp(value, 0, _engine.MaxGainReduction);
            if (_rtlSdrRfGainDb == clamped) return;
            _rtlSdrRfGainDb = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RtlSdrGainSummary));
            if (IsRtlDevice)
            {
                RfGainDb = clamped;
                ApplyGainToEngine(clamped);
            }
        }
    }







    private DateTime? _gainAbove55StartTime;
    private DateTime? _gainBelow45StartTime;
    private int _lastSdrPlayLnaStateCount;

    [ObservableProperty] private int _selectedLnaState = 0;
    [ObservableProperty] private int _selectedNotchFilter = 0; // 0: Off, 1: MW+FM, 2: DAB, 3: Both
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SdrPlayNotchVisibility))]
    private bool _isSdrPlayNotchAvailable;
    [ObservableProperty] private List<SettingsComboBoxOption<int>> _sdrPlayNotchOptions = [];

    public Visibility SdrPlayNotchVisibility =>
        IsSdrPlayNotchAvailable ? Visibility.Visible : Visibility.Collapsed;

    partial void OnSelectedLnaStateChanged(int value)
    {
        if (_engine?.SdrDevice != null)
        {
            _engine.SdrDevice.LnaState = value;
            _engine.SdrDevice.ApplyLnaAndNotch();
        }
        SyncGainIndicatorText();
        RefreshSdrPlayGainPresentation();
    }

    public void SyncDeviceIndicatorMode(string modelName)
    {
        IsRtlDevice = _engine?.SdrDevice?.Capabilities.IsRtlSdr == true ||
                      modelName.Contains("RTL", StringComparison.OrdinalIgnoreCase);

        GainPrimaryLabel = IsRtlDevice ? "GAIN" : "GR";
        GainPrimaryUnit = IsRtlDevice ? "" : "dB";
        GainSecondaryLabel = IsRtlDevice ? "AGC" : "LNA";
        RfGainDb = GetCurrentDeviceRfGain();
        OnPropertyChanged(nameof(SdrPlayRfGainDb));
        OnPropertyChanged(nameof(RtlSdrRfGainDb));
        OnPropertyChanged(nameof(RtlSdrGainSummary));

        SyncGainIndicatorText();
        if (!IsRtlDevice && !IsAgcEnabled && _engine?.SdrDevice is SRdeck.SDR.SdrController)
        {
            ApplySdrPlaySensitivity();
        }
        else
        {
            RefreshSdrPlayGainPresentation();
        }

        RefreshSdrPlayNotchOptions();
    }

    partial void OnIsRtlDeviceChanged(bool value)
    {
        ApplyFftResolutionLimit();
        OnPropertyChanged(nameof(IsSampleRateSelectionEnabled));
    }

    private int GetCurrentDeviceRfGain()
    {
        if (IsRtlDevice) return _rtlSdrRfGainDb;

        return _sdrPlayRfGainDb;
    }

    private void SyncCurrentDeviceRfGain(int value)
    {
        if (IsRtlDevice)
        {
            _rtlSdrRfGainDb = value;
            OnPropertyChanged(nameof(RtlSdrRfGainDb));
            OnPropertyChanged(nameof(RtlSdrGainSummary));
        }

        else
        {
            _sdrPlayRfGainDb = value;
            OnPropertyChanged(nameof(SdrPlayRfGainDb));
        }
    }

    private void ApplyGainToEngine(int gain)
    {
        var p = _engine.Control;
        p.RfGainDb = gain;
        _engine.Control = p;
        _engine.CurrentGainDb = gain;
        _engine.GainChange();
    }

    public void SyncGainIndicatorText()
    {
        GainSecondaryValue = IsRtlDevice
            ? (_engine?.RfAgcEnabled == 1 ? "AUTO" : "MAN")
            : SelectedLnaState.ToString();
    }

    private int GetSdrPlayMaxLnaState()
    {
        int count = SdrPlayGainPolicy.GetLnaStateCount(GetSdrPlayModelName(), _engine.Control.CenterFreqHz);
        return Math.Max(0, count - 1);
    }

    private string GetSdrPlayModelName() =>
        (_engine.SdrDevice as SRdeck.SDR.SdrController)?.ModelName ?? DeviceName;

    private void ApplySdrPlaySensitivity()
    {
        SdrPlayGainSetting setting = SdrPlayGainPolicy.FromSensitivity(
            SdrPlaySensitivity,
            GetSdrPlayModelName(),
            _engine.Control.CenterFreqHz,
            _engine.MinGainReduction,
            _engine.MaxGainReduction);

        SelectedLnaState = setting.LnaState;
        SdrPlayRfGainDb = setting.GainReductionDb;
        RefreshSdrPlayGainPresentation();
    }

    private int GetSdrPlayAutomaticNominalGainReduction()
    {
        int standard = Math.Clamp(50, _engine.MinGainReduction, _engine.MaxGainReduction);
        int profileBiasDb = (int)Math.Round((50 - SdrPlaySensitivity) / 10.0);
        return Math.Clamp(
            standard + profileBiasDb,
            _engine.MinGainReduction,
            _engine.MaxGainReduction);
    }

    private void SyncSdrPlaySensitivityFromCurrentState()
    {
        if (!IsAgcEnabled || IsRtlDevice || _engine?.SdrDevice is not SRdeck.SDR.SdrController)
            return;

        int calculatedSensitivity = SdrPlayGainPolicy.ToSensitivity(
            SelectedLnaState,
            SdrPlayRfGainDb,
            GetSdrPlayModelName(),
            _engine.Control.CenterFreqHz,
            _engine.MinGainReduction,
            _engine.MaxGainReduction);

        if (SdrPlaySensitivity != calculatedSensitivity)
        {
            _isUpdatingSensitivityFromAgc = true;
            SdrPlaySensitivity = calculatedSensitivity;
            _isUpdatingSensitivityFromAgc = false;
        }
    }

    private void RefreshSdrPlayGainPresentation()
    {
        if (IsRtlDevice) return;

        if (IsAgcEnabled && !_isUpdatingSensitivityFromAgc)
        {
            SyncSdrPlaySensitivityFromCurrentState();
        }

        string profile = SdrPlaySensitivity switch
        {
            <= 30 => "強信号向け",
            >= 70 => "微弱信号向け",
            _ => "標準"
        };
        SdrPlaySensitivityDescription = IsAgcEnabled ? $"自動・{profile}" : profile;
        int attenuationDb = SdrPlayGainPolicy.GetAttenuationFromMaximumGainDb(
            GetSdrPlayModelName(),
            _engine.Control.CenterFreqHz,
            SelectedLnaState,
            SdrPlayRfGainDb,
            _engine.MinGainReduction);
        SdrPlayGainSummary = $"LNA {SelectedLnaState} / GR {SdrPlayRfGainDb} dB / 最大ゲイン比 -{attenuationDb} dB";
    }

    partial void OnSelectedNotchFilterChanged(int value)
    {
        if (_engine?.SdrDevice is SRdeck.SDR.SdrController controller)
        {
            int normalized = (int)SdrPlayNotchPolicy.Normalize(controller.ModelName, value);
            if (value != normalized)
            {
                SelectedNotchFilter = normalized;
                return;
            }
        }

        if (_engine?.SdrDevice != null)
        {
            _engine.SdrDevice.NotchFilterMode = value;
            _engine.SdrDevice.ApplyLnaAndNotch();
        }
    }

    private void RefreshSdrPlayNotchOptions()
    {
        var controller = _engine?.SdrDevice as SRdeck.SDR.SdrController;
        string? modelName = controller?.ModelName;
        bool supportsBroadcast = controller != null && SdrPlayNotchPolicy.SupportsBroadcastNotch(modelName);
        bool supportsDab = controller != null && SdrPlayNotchPolicy.SupportsDabNotch(modelName);
        IsSdrPlayNotchAvailable = supportsBroadcast || supportsDab;

        bool isEnglish = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase);
        var options = new List<SettingsComboBoxOption<int>>
        {
            new() { Label = isEnglish ? "Off" : "オフ", Value = (int)SdrPlayNotchFilterMode.Off }
        };
        if (supportsBroadcast)
        {
            options.Add(new()
            {
                Label = "MW+FM",
                Value = (int)SdrPlayNotchFilterMode.MwFm
            });
        }
        if (supportsDab)
        {
            options.Add(new() { Label = "DAB", Value = (int)SdrPlayNotchFilterMode.Dab });
        }
        if (supportsBroadcast && supportsDab)
        {
            options.Add(new()
            {
                Label = "MW+FM + DAB",
                Value = (int)SdrPlayNotchFilterMode.MwFmAndDab
            });
        }

        SdrPlayNotchOptions = options;
        int normalized = (int)SdrPlayNotchPolicy.Normalize(modelName, SelectedNotchFilter);
        if (SelectedNotchFilter != normalized)
        {
            SelectedNotchFilter = normalized;
        }
    }

    public Action<string>? ChangeGainAction { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ChangeGain(string param)
    {
        if (int.TryParse(param, out int delta))
        {
            int effectiveDelta = IsRtlDevice ? delta * 5 : delta;
            int newVal = _engine.CurrentGainDb + effectiveDelta;
            newVal = Math.Clamp(newVal, _engine.MinGainReduction, _engine.MaxGainReduction);
            _engine.CurrentGainDb = newVal;
            if (_engine is Models.CoreEngine core) core.ApplyGainUpdate();
            RfGainDb = newVal;
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ChangeSdrPlayGain(string param)
    {
        if (!int.TryParse(param, out int delta)) return;
        SdrPlayRfGainDb = Math.Clamp(SdrPlayRfGainDb + delta, 0, _engine.MaxGainReduction);
    }

#if ENABLE_RTLSDR
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ChangeRtlSdrGain(string param)
    {
        if (!int.TryParse(param, out int delta)) return;
        RtlSdrRfGainDb = Math.Clamp(RtlSdrRfGainDb + delta, 0, _engine.MaxGainReduction);
    }
#endif

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ChangeLnaState(string param)
    {
        if (int.TryParse(param, out int delta))
        {
            int newVal = SelectedLnaState + delta;
            SelectedLnaState = Math.Clamp(newVal, 0, GetSdrPlayMaxLnaState());
        }
    }
}
