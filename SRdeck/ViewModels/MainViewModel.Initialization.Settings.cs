using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Configuration;
using SRdeck.Models;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
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
}
