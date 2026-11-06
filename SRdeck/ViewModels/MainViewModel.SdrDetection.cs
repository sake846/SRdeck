using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Configuration;
using SRdeck.Messages;
using SRdeck.Models;
using SRdeck.Models.Configuration;
using SRdeck.SDR;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSdrSettingsVisible))]
    private bool _isSdrDetected;

    [ObservableProperty]
    private bool _isDetectingSdr;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RtlSdrGainSummary))]
    private bool _isAgcEnabled;
    [ObservableProperty]
    private AgcReleaseMode _agcReleaseMode = AgcReleaseMode.Slow;
    [ObservableProperty]
    private AgcSensitivityProfile? _selectedAgcSensitivityProfile;
    [ObservableProperty]
    private AgcReleaseMode _selectedAgcSensitivityMode = AgcReleaseMode.Slow;
    public ObservableCollection<AgcSensitivityProfile> AgcSensitivityProfiles { get; } = new();
    private bool _isLoadingAgcState;
    private bool _isLoadingAgcSensitivityProfiles;
    private bool _isResettingAgcSensitivityProfiles;

    public bool IsSdrSettingsVisible => IsSdrDetected;

    partial void OnIsSdrDetectedChanged(bool value) => SyncButtonStates();
    partial void OnIsDetectingSdrChanged(bool value) => SyncButtonStates();

    partial void OnIsAgcEnabledChanged(bool value)
    {
        if (_engine == null || _isLoadingAgcState) return;

        _engine.RfAgcEnabled = value ? 1 : 0;
        if (_engine.SdrDevice != null)
        {
            // The AGC button controls host-side AGC only.
            _engine.SdrDevice.RfAgcEnabled = false;
        }

        // Keep the receiver in manual gain mode. With host AGC off, the single
        // sensitivity control selects both LNA state and gain reduction.
        if (!IsRtlDevice && _engine.SdrDevice is SRdeck.SDR.SdrController)
        {
            ApplySdrPlaySensitivity();
        }
        else
        {
            ApplyGainToEngine(GetCurrentDeviceRfGain());
        }
        RefreshSdrPlayGainPresentation();

        SaveAgcSettings();

        RadioControl control = _engine.Control;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(control));
        SyncGainIndicatorText();
    }

    partial void OnAgcReleaseModeChanged(AgcReleaseMode value)
    {
        if (_engine == null || _isLoadingAgcState) return;

        ApplyAgcSensitivityProfile(value);

        bool isEnabled = value != AgcReleaseMode.Off;
        if (IsAgcEnabled != isEnabled)
        {
            IsAgcEnabled = isEnabled;
        }

        if (isEnabled)
        {
            _engine.AgcReleaseMode = value;
        }
        SaveAgcSettings();
    }

    partial void OnSelectedAgcSensitivityProfileChanged(AgcSensitivityProfile? value)
    {
        if (value is not null && SelectedAgcSensitivityMode != value.Mode)
            SelectedAgcSensitivityMode = value.Mode;
        if (value?.Mode == AgcReleaseMode)
        {
            _engine.AgcControlSettings = value.ControlSettings;
        }
    }

    partial void OnSelectedAgcSensitivityModeChanged(AgcReleaseMode value)
    {
        SelectedAgcSensitivityProfile = AgcSensitivityProfiles.FirstOrDefault(item => item.Mode == value);
    }

    private void LoadAgcSensitivityProfiles(IEnumerable<AgcSensitivityProfile>? profiles)
    {
        _isLoadingAgcSensitivityProfiles = true;
        try
        {
            foreach (AgcSensitivityProfile profile in AgcSensitivityProfiles)
                profile.PropertyChanged -= OnAgcSensitivityProfilePropertyChanged;
            AgcSensitivityProfiles.Clear();

            List<AgcSensitivityProfile> defaultProfiles = AgcSensitivityProfile.CreateDefaults();
            foreach (AgcReleaseMode mode in AgcSensitivityProfile.EditableModes)
            {
                AgcSensitivityProfile profile = profiles?.FirstOrDefault(item => item.Mode == mode)
                    ?? defaultProfiles.First(item => item.Mode == mode);
                profile.PropertyChanged += OnAgcSensitivityProfilePropertyChanged;
                AgcSensitivityProfiles.Add(profile);
            }

            AgcReleaseMode selectedMode = AgcSensitivityProfile.EditableModes.Contains(AgcReleaseMode)
                ? AgcReleaseMode
                : AgcReleaseMode.Slow;
            SelectedAgcSensitivityMode = selectedMode;
            SelectedAgcSensitivityProfile = AgcSensitivityProfiles.FirstOrDefault(item => item.Mode == selectedMode);
        }
        finally
        {
            _isLoadingAgcSensitivityProfiles = false;
        }
    }

    private void OnAgcSensitivityProfilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingAgcSensitivityProfiles || _isResettingAgcSensitivityProfiles) return;
        if (sender is AgcSensitivityProfile profile && profile.Mode == AgcReleaseMode)
            _engine.AgcControlSettings = profile.ControlSettings;

        SaveAgcSensitivityProfiles();
    }

    private void ApplyAgcSensitivityProfile(AgcReleaseMode mode)
    {
        AgcSensitivityProfile? profile = AgcSensitivityProfiles.FirstOrDefault(item => item.Mode == mode);
        _engine.AgcControlSettings = profile?.ControlSettings ?? AgcSensitivityProfile.CreateDefault(mode).ControlSettings;
    }

    private void SaveAgcSensitivityProfiles()
    {
        SdrDeviceType deviceType = GetEffectiveHardwareSettingsDeviceType();
        HardwareSettings settings = _settingsService.LoadHardwareSettings(deviceType);
        settings.AgcSensitivityProfiles = AgcSensitivityProfiles.ToList();
        _settingsService.SaveHardwareSettings(settings, deviceType);
    }

    [RelayCommand]
    private void ResetAgcSensitivityProfiles()
    {
        _isResettingAgcSensitivityProfiles = true;
        try
        {
            foreach (AgcSensitivityProfile profile in AgcSensitivityProfiles)
                profile.ResetToDefaults();
        }
        finally
        {
            _isResettingAgcSensitivityProfiles = false;
        }
        ApplyAgcSensitivityProfile(AgcReleaseMode);
        SaveAgcSensitivityProfiles();
    }

    [RelayCommand]
    private async Task DetectSdr()
    {
        await DetectSdrInternal(showErrors: true);
    }

    private async Task DetectSdrInternal(bool showErrors)
    {
        if (IsDetectingSdr || IsStarted) return;

        IsDetectingSdr = true;
        try
        {
            // A stopped SDR session keeps its device object attached so that it
            // can be started again quickly. Release that native handle before
            // probing, otherwise a second detection can collide with the still
            // selected device (especially with SDRplay's API lock).
            ISdrDevice? previousDevice = _engine.SdrDevice;
            _engine.SdrDevice = null;
            IsSdrDetected = false;
            DeviceSn = string.Empty;
            if (previousDevice != null)
            {
                await Task.Run(() => previousDevice.Dispose());
            }

            SdrDeviceType configuredDeviceType = _engine.InitialAppSettings.SdrDeviceType;
            ISdrDevice? detectedDevice = await Task.Run(() =>
                SdrDeviceFactory.TryOpenPreferred(configuredDeviceType, out ISdrDevice? device)
                    ? device
                    : null);

            if (detectedDevice == null)
            {
                IsSdrDetected = false;
                DeviceSn = string.Empty;
                if (showErrors)
                {
                    WeakReferenceMessenger.Default.Send(new SdrErrorMessage(
                        configuredDeviceType == SdrDeviceType.HackRf
                            ? "HackRF One を検出できませんでした。接続、WinUSB ドライバー、hackrf.dll を確認してください。"
                            : "選択された SDR デバイスを検出できませんでした。接続、ドライバー、実行時 DLL を確認してください。"));
                }
                return;
            }

            _engine.SdrDevice = detectedDevice;

            SdrDeviceKind deviceKind = detectedDevice.Capabilities.Kind;
            bool isRtl = deviceKind == SdrDeviceKind.RtlSdr;
            bool isHackRf = deviceKind == SdrDeviceKind.HackRf;
            bool isRx888 = deviceKind == SdrDeviceKind.Rx888;
            int sampleRateHz = SdrSampleRatePolicy.Normalize(SdrPlaySampleRateHz, deviceKind);
            if ((isRtl || isHackRf || isRx888) && !detectedDevice.ApplySampleRate(sampleRateHz))
            {
                detectedDevice.Dispose();
                _engine.SdrDevice = null;
                IsSdrDetected = false;
                WeakReferenceMessenger.Default.Send(new SdrErrorMessage(
                    $"SDR のサンプルレート設定に失敗しました ({sampleRateHz / 1_000_000.0:F1} Msps)。"));
                return;
            }
            detectedDevice.FsHz = sampleRateHz;
            IsRtlDevice = isRtl;
            IsHackRfDevice = isHackRf;
            IsRx888Device = isRx888;
            if (detectedDevice is SdrController sdrPlay)
            {
                DeviceName = sdrPlay.ModelName;
                if (!string.IsNullOrEmpty(sdrPlay.SerialNumber))
                {
                    DeviceSn = $" S/N: {sdrPlay.SerialNumber}";
                }
            }
            else
            {
                DeviceName = detectedDevice switch
                {
                    RtlSdrController rtlSdr => rtlSdr.ModelName,
#if ENABLE_HACKRF
                    HackRfController hackRf => hackRf.ModelName,
#endif
                    _ => isRx888 ? "RX-888 MK2" : isRtl ? "RTL-SDR" : (isHackRf ? "HackRF One" : "SDRplay")
                };
            }

            var hardwareType = isRtl
                ? SdrDeviceType.RtlSdr
                : isHackRf
                    ? SdrDeviceType.HackRf
                    : isRx888 ? SdrDeviceType.Rx888Mk2 : SdrDeviceType.SdrPlay;
            var hardwareSettings = _settingsService.LoadHardwareSettings(hardwareType);
            _engine.RfCalibrationOffset = hardwareSettings.RfCalibrationOffset;
            _engine.SystemGainOffset = hardwareSettings.SystemGainOffset;
            _engine.SdrBiasPpm = hardwareSettings.SdrBiasPpm;
            LoadPpmDisplayValue();
            _engine.MinGainReduction = hardwareSettings.MinGainReduction;
            _engine.RfAgcEnabled = hardwareSettings.RfAgcEnabled;
            LoadAgcSensitivityProfiles(hardwareSettings.AgcSensitivityProfiles);
            ApplyAgcSensitivityProfile(hardwareSettings.AgcReleaseMode);
            _engine.AgcReleaseMode = hardwareSettings.AgcReleaseMode;
            LoadSdrPlayDeviceSettings(hardwareSettings);
            SetAgcStateFromSettings(
                hardwareSettings.RfAgcEnabled == 1,
                hardwareSettings.AgcReleaseMode);

            SdrPlaySampleRateHz = sampleRateHz;

            RadioControl control = _engine.Control;
            control.FsHz = sampleRateHz;
            control.RfGainDb = GetCurrentDeviceRfGain();
            _engine.Control = control;
            _engine.CurrentGainDb = control.RfGainDb;
            _engine.EnsureIqBufferCapacity();
            SyncDeviceIndicatorMode(DeviceName);
            SyncSdrPlayDeviceSettingsAvailability();
            SyncMainSpanOptionsToFs(
                sampleRateHz, isRtl, isRx888, selectFullSpan: true);
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(_engine.Control));
            IsSdrDetected = true;
        }
        catch (Exception exception)
        {
            IsSdrDetected = false;
            if (showErrors)
            {
                WeakReferenceMessenger.Default.Send(new SdrErrorMessage(
                    $"SDRデバイスの検出中にエラーが発生しました。\n{exception.Message}"));
            }
            else
            {
                Console.WriteLine($"SDR device detection failed: {exception.Message}");
            }
        }
        finally
        {
            IsDetectingSdr = false;
        }
    }

    [RelayCommand]
    private void ToggleAgc() => IsAgcEnabled = !IsAgcEnabled;

    private void SetAgcStateFromSettings(bool isEnabled, AgcReleaseMode releaseMode)
    {
        _isLoadingAgcState = true;
        try
        {
            IsAgcEnabled = isEnabled;
            AgcReleaseMode = isEnabled
                ? (releaseMode == AgcReleaseMode.Off ? AgcReleaseMode.Slow : releaseMode)
                : AgcReleaseMode.Off;
            SelectedAgcSensitivityMode = AgcSensitivityProfile.EditableModes.Contains(AgcReleaseMode)
                ? AgcReleaseMode
                : AgcReleaseMode.Slow;
        }
        finally { _isLoadingAgcState = false; }
    }

    private void SaveAgcSettings()
    {
        var deviceType = GetEffectiveHardwareSettingsDeviceType();
        var hardwareSettings = _settingsService.LoadHardwareSettings(deviceType);
        hardwareSettings.RfAgcEnabled = IsAgcEnabled ? 1 : 0;
        if (AgcReleaseMode != AgcReleaseMode.Off)
        {
            hardwareSettings.AgcReleaseMode = AgcReleaseMode;
        }
        _settingsService.SaveHardwareSettings(hardwareSettings, deviceType);
    }
}
