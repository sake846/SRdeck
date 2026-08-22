using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Configuration;
using SRdeck.Models;
using SRdeck.Models.Configuration;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private bool _isLoadingPpmDisplay;

    private void ReadSettings()
    {
        if (_engine != null)
        {
            _engine.InitialAppSettings = _settingsService.LoadSettings();
            IsRtlDevice = IsRtlSdrConfigured(_engine.InitialAppSettings.SdrDeviceType);
            var hardwareDeviceType = GetEffectiveHardwareSettingsDeviceType();
            var hardwareSettings = _settingsService.LoadHardwareSettings(hardwareDeviceType);
            _engine.RfCalibrationOffset = hardwareSettings.RfCalibrationOffset;
            _engine.SystemGainOffset = hardwareSettings.SystemGainOffset;
            _engine.SdrBiasPpm = hardwareSettings.SdrBiasPpm;
            LoadPpmDisplayValue();
            _engine.MinGainReduction = hardwareSettings.MinGainReduction;
            _engine.RfAgcEnabled = hardwareSettings.RfAgcEnabled;
            _engine.AgcReleaseMode = hardwareSettings.AgcReleaseMode;
            LoadSdrPlayDeviceSettings(hardwareSettings);
            SetAgcStateFromSettings(
                hardwareSettings.RfAgcEnabled == 1,
                hardwareSettings.AgcReleaseMode);
            RfGainDb = GetCurrentDeviceRfGain();
            _engine.ResidualDcRemovalEnabled =
                _engine.InitialAppSettings.SignalProcessing.ResidualDcRemovalEnabled;
            _engine.InitializeDSP();
        }
    }

    private SdrDeviceType GetEffectiveHardwareSettingsDeviceType()
    {
        var configuredDeviceType = _engine.InitialAppSettings.SdrDeviceType;
        if (configuredDeviceType != SdrDeviceType.Auto)
        {
            return configuredDeviceType;
        }

        return _engine.SdrDevice?.Capabilities.Kind switch
        {
            SdrDeviceKind.RtlSdr => SdrDeviceType.RtlSdr,

            _ => SdrDeviceType.SdrPlay
        };
    }

    private void LoadPpmDisplayValue()
    {
        if (Tuner == null) return;

        _isLoadingPpmDisplay = true;
        try
        {
            Tuner.BasePpm = _engine.SdrBiasPpm;
            Tuner.PpmAdjustment = 0f;
        }
        finally
        {
            _isLoadingPpmDisplay = false;
        }
    }

    private void OnPrimaryTunerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingPpmDisplay || e.PropertyName != nameof(TunerViewModel.TotalPpm)) return;
        if (!IsSdrDetected || _engine.SdrDevice == null || Tuner == null) return;

        float ppm = Tuner.TotalPpm;
        SdrDeviceType deviceType = GetEffectiveHardwareSettingsDeviceType();
        HardwareSettings settings = _settingsService.LoadHardwareSettings(deviceType);
        settings.SdrBiasPpm = ppm;
        _settingsService.SaveHardwareSettings(settings, deviceType);

        _engine.SdrBiasPpm = ppm;
        _isLoadingPpmDisplay = true;
        try
        {
            Tuner.BasePpm = ppm;
            Tuner.PpmAdjustment = 0f;
        }
        finally
        {
            _isLoadingPpmDisplay = false;
        }
    }
}
