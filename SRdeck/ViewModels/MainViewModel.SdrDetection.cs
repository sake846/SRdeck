using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Configuration;
using SRdeck.Messages;
using SRdeck.Models;
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
    private bool _isLoadingAgcState;

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

        _engine.AgcReleaseMode = value;
        SaveAgcSettings();
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

            ISdrDevice? detectedDevice = await Task.Run(() =>
                SdrDeviceFactory.TryOpenPreferred(out ISdrDevice? device) ? device : null);

            if (detectedDevice == null)
            {
                IsSdrDetected = false;
                DeviceSn = string.Empty;
                if (showErrors)
                {
                    WeakReferenceMessenger.Default.Send(new SdrErrorMessage(
                        "SDRplayまたはRTL-SDRデバイスを検出できませんでした。接続とドライバーを確認してください。"));
                }
                return;
            }

            _engine.SdrDevice = detectedDevice;

            bool isRtl = detectedDevice.Capabilities.Kind == SdrDeviceKind.RtlSdr;
            int sampleRateHz = isRtl ? 2_000_000 : NormalizeSdrPlaySampleRate(SdrPlaySampleRateHz);
            detectedDevice.FsHz = sampleRateHz;
            IsRtlDevice = isRtl;
            if (detectedDevice is SdrController sdrPlay)
            {
                DeviceName = sdrPlay.ModelName;
                if (!string.IsNullOrEmpty(sdrPlay.SerialNumber))
                {
                    DeviceSn = $" (S/N: {sdrPlay.SerialNumber})";
                }
            }
            else
            {
                DeviceName = isRtl ? "RTL-SDR" : "SDRplay";
            }

            var hardwareType = isRtl ? SdrDeviceType.RtlSdr : SdrDeviceType.SdrPlay;
            var hardwareSettings = _settingsService.LoadHardwareSettings(hardwareType);
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

            if (isRtl)
            {
                SdrPlaySampleRateHz = 2_000_000;
            }

            RadioControl control = _engine.Control;
            control.FsHz = sampleRateHz;
            control.RfGainDb = GetCurrentDeviceRfGain();
            _engine.Control = control;
            _engine.CurrentGainDb = control.RfGainDb;
            _engine.EnsureIqBufferCapacity();
            SyncDeviceIndicatorMode(DeviceName);
            SyncSdrPlayDeviceSettingsAvailability();
            SyncMainSpanOptionsToFs(sampleRateHz, isRtl, selectFullSpan: true);
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

    private static int NormalizeSdrPlaySampleRate(int value) => value switch
    {
        8_000_000 or 6_000_000 or 4_000_000 or 2_000_000 or 1_600_000 => value,
        _ => 8_000_000
    };

    private void SetAgcStateFromSettings(bool isEnabled, AgcReleaseMode releaseMode)
    {
        _isLoadingAgcState = true;
        try
        {
            IsAgcEnabled = isEnabled;
            AgcReleaseMode = releaseMode;
        }
        finally { _isLoadingAgcState = false; }
    }

    private void SaveAgcSettings()
    {
        var deviceType = GetEffectiveHardwareSettingsDeviceType();
        var hardwareSettings = _settingsService.LoadHardwareSettings(deviceType);
        hardwareSettings.RfAgcEnabled = IsAgcEnabled ? 1 : 0;
        hardwareSettings.AgcReleaseMode = AgcReleaseMode;
        _settingsService.SaveHardwareSettings(hardwareSettings, deviceType);
    }
}
