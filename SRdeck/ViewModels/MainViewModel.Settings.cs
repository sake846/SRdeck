using System;
using SRdeckPlugin.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Configuration;
using SRdeck.Messages;
using SRdeck.Models;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // --- appsettings.json Power Settings ---
    [ObservableProperty] private bool _isPreventSleepOnAc;
    [ObservableProperty] private bool _isPreventSleepOnBattery;
    [ObservableProperty] private bool _isDisableWpfRenderingOnServer;
    [ObservableProperty] private bool _isResidualDcRemovalEnabled;

    partial void OnIsResidualDcRemovalEnabledChanged(bool value)
    {
        if (_engine?.InitialAppSettings == null) return;
        _engine.InitialAppSettings.SignalProcessing.ResidualDcRemovalEnabled = value;
        _engine.ResidualDcRemovalEnabled = value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
    }

    partial void OnIsPreventSleepOnAcChanged(bool value)
    {
        if (_engine?.InitialAppSettings?.Power == null) return;
        _engine.InitialAppSettings.Power.PreventSleepOnAc = value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        SyncSleepPrevention();
    }

    partial void OnIsPreventSleepOnBatteryChanged(bool value)
    {
        if (_engine?.InitialAppSettings?.Power == null) return;
        _engine.InitialAppSettings.Power.PreventSleepOnBattery = value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        SyncSleepPrevention();
    }


    public bool IsCompactWpfMode => IsDisableWpfRenderingOnServer;

    partial void OnIsDisableWpfRenderingOnServerChanged(bool value)
    {
        if (_engine?.InitialAppSettings?.Power == null) return;
        _engine.InitialAppSettings.Power.DisableWpfRenderingOnServer = value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        OnPropertyChanged(nameof(IsCompactWpfMode));
    }

    // --- ComboBox Selections & Persistent Settings ---
    [ObservableProperty] private SettingsComboBoxOption<float?>? _selectedGridTopDb;
    [ObservableProperty] private SettingsComboBoxOption<int?>? _selectedDebugDraw;
    [ObservableProperty] private SettingsComboBoxOption<FrequencyDisplayMode?>? _selectedFrequencyDisplayMode;
    [ObservableProperty] private SettingsComboBoxOption<bool?>? _selectedIsGpuFftEnabled;
    [ObservableProperty] private SettingsComboBoxOption<PluginChannelAccelerationPreference>? _selectedDemodLightGpu;
    [ObservableProperty] private SettingsComboBoxOption<PluginChannelAccelerationPreference>? _selectedDemodStandardGpu;
    [ObservableProperty] private SettingsComboBoxOption<PluginChannelAccelerationPreference>? _selectedDemodHeavyGpu;
    [ObservableProperty] private SettingsComboBoxOption<int?>? _selectedFftResolutionMode;
    [ObservableProperty] private SettingsComboBoxOption<string?>? _selectedProcessPriority;
    [ObservableProperty] private SettingsComboBoxOption<string?>? _startupProcessPriority;
    [ObservableProperty] private SettingsComboBoxOption<SdrDeviceType>? _selectedSdrDeviceType;

    partial void OnSelectedDemodLightGpuChanged(SettingsComboBoxOption<PluginChannelAccelerationPreference>? value)
    {
        if (value == null || _engine?.InitialAppSettings?.Demodulation == null) return;
        _engine.InitialAppSettings.Demodulation.LightWorkloadPreference = value.Value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        SyncDemodWorkloadPreferences();
    }

    partial void OnSelectedDemodStandardGpuChanged(SettingsComboBoxOption<PluginChannelAccelerationPreference>? value)
    {
        if (value == null || _engine?.InitialAppSettings?.Demodulation == null) return;
        _engine.InitialAppSettings.Demodulation.StandardWorkloadPreference = value.Value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        SyncDemodWorkloadPreferences();
    }

    partial void OnSelectedDemodHeavyGpuChanged(SettingsComboBoxOption<PluginChannelAccelerationPreference>? value)
    {
        if (value == null || _engine?.InitialAppSettings?.Demodulation == null) return;
        _engine.InitialAppSettings.Demodulation.HeavyWorkloadPreference = value.Value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        SyncDemodWorkloadPreferences();
    }

    private void SyncDemodWorkloadPreferences()
    {
        if (_engine?.InitialAppSettings?.Demodulation == null) return;
        var demod = _engine.InitialAppSettings.Demodulation;
        _engine.SetWorkloadAccelerationPreferences(
            demod.LightWorkloadPreference,
            demod.StandardWorkloadPreference,
            demod.HeavyWorkloadPreference);
    }

    [ObservableProperty] private string _language = "ja";

    partial void OnSelectedSdrDeviceTypeChanged(SettingsComboBoxOption<SdrDeviceType>? value)
    {
        if (value == null || _engine?.InitialAppSettings == null) return;
        if (_engine.InitialAppSettings.SdrDeviceType == value.Value) return;

        _engine.InitialAppSettings.SdrDeviceType = value.Value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
    }

    partial void OnSelectedGridTopDbChanged(SettingsComboBoxOption<float?>? value)
    {
        if (value == null || _engine?.InitialAppSettings?.Display == null) return;
        _engine.InitialAppSettings.Display.GridTopDb = value.Value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);

        if (value.Value.HasValue)
        {
            SpectrumOverlay.GridTopDb = value.Value.Value;
            _engine.NeedsBackgroundRedraw = true;
            SyncState(_engine.Control, _engine.State);
        }
    }



    partial void OnSelectedDebugDrawChanged(SettingsComboBoxOption<int?>? value)
    {
        if (value == null || _engine?.InitialAppSettings?.Display == null) return;
        _engine.InitialAppSettings.Display.DebugDraw = value.Value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);

        if (value.Value.HasValue)
        {
            RadioControl control = _engine.Control;
            control.IsDebugVisible = value.Value.Value != 0;
            _engine.Control = control;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(control));
        }
    }

    partial void OnSelectedFrequencyDisplayModeChanged(SettingsComboBoxOption<FrequencyDisplayMode?>? value)
    {
        if (value == null || _engine?.InitialAppSettings?.Display == null) return;
        _engine.InitialAppSettings.Display.FrequencyDisplayMode = value.Value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
    }

    partial void OnSelectedIsGpuFftEnabledChanged(SettingsComboBoxOption<bool?>? value)
    {
        if (value == null || _engine?.InitialAppSettings?.Display == null) return;
        _engine.InitialAppSettings.Display.IsGpuFftEnabled = value.Value ?? true;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        if (value.Value.HasValue)
        {
            IsGpuFftEnabled = value.Value.Value;
        }
    }

    partial void OnSelectedFftResolutionModeChanged(SettingsComboBoxOption<int?>? value)
    {
        if (value == null || _engine?.InitialAppSettings?.Display == null) return;
        _engine.InitialAppSettings.Display.FftResolutionMode = value.Value ?? 1;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        if (value.Value.HasValue)
        {
            FftResolutionMode = value.Value.Value;
        }
    }





    partial void OnSelectedProcessPriorityChanged(SettingsComboBoxOption<string?>? value)
    {
        if (_lastState == null) return;
        _lastState.ProcessPriority = value?.Value;
        _lastStateService.SaveLastState(_lastState);
        SyncProcessPriorityToOs(value?.Value);
    }
 
    partial void OnStartupProcessPriorityChanged(SettingsComboBoxOption<string?>? value)
    {
        if (_engine?.InitialAppSettings?.Power == null) return;
        _engine.InitialAppSettings.Power.ProcessPriority = value?.Value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        if (!string.IsNullOrWhiteSpace(value?.Value))
        {
            SyncProcessPriorityToOs(value.Value);
        }
    }

    private void SyncProcessPriorityToOs(string? priorityStr)
    {
        try
        {
            if (string.IsNullOrEmpty(priorityStr)) return;
            var process = System.Diagnostics.Process.GetCurrentProcess();
            switch (priorityStr.ToLowerInvariant())
            {
                case "normal": process.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal; break;
                case "abovenormal": process.PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; break;
                case "high": process.PriorityClass = System.Diagnostics.ProcessPriorityClass.High; break;
                case "realtime": process.PriorityClass = System.Diagnostics.ProcessPriorityClass.RealTime; break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to set process priority: {ex.Message}");
        }
    }

    partial void OnLanguageChanged(string value)
    {
        if (string.IsNullOrEmpty(value) || _engine?.InitialAppSettings == null) return;
        _engine.InitialAppSettings.Language = value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        SyncWpfLanguageResource(value);
        RefreshSdrPlayNotchOptions();
    }

    [ObservableProperty] private int _sdrPlaySampleRateHz;
    private bool _isSynchronizingSampleRateSelection;

    partial void OnSdrPlaySampleRateHzChanged(int value)
    {
        if (_isSynchronizingSampleRateSelection || _engine?.InitialAppSettings == null) return;
        value = NormalizeSdrPlaySampleRate(value);
        if (IsRtlDevice && value != 2_000_000)
        {
            SdrPlaySampleRateHz = 2_000_000;
            return;
        }
        bool settingsChanged = _engine.InitialAppSettings.SdrPlaySampleRateHz != value;
        if (settingsChanged)
        {
            _engine.InitialAppSettings.SdrPlaySampleRateHz = value;
            _settingsService.SaveSettings(_engine.InitialAppSettings);
        }

        if (_engine.SdrDevice != null && _engine.SdrDevice.Capabilities.Kind == SdrDeviceKind.SdrPlay)
        {
            if (!settingsChanged &&
                _engine.SdrDevice.FsHz == value &&
                _engine.Control.FsHz == value)
            {
                return;
            }
            _engine.SdrDevice.FsHz = value;
            var control = _engine.Control;
            control.FsHz = value;
            _engine.Control = control;
            _engine.EnsureIqBufferCapacity();
            SyncMainSpanOptionsToFs(value, isRtlDevice: false, selectFullSpan: true);
        }
    }

    private void SyncSampleRateSelectionFromAppliedControl(int sampleRateHz)
    {
        if (sampleRateHz <= 0 || NormalizeSdrPlaySampleRate(sampleRateHz) != sampleRateHz ||
            SdrPlaySampleRateHz == sampleRateHz)
        {
            return;
        }

        _isSynchronizingSampleRateSelection = true;
        try
        {
            SdrPlaySampleRateHz = sampleRateHz;
        }
        finally
        {
            _isSynchronizingSampleRateSelection = false;
        }
    }
}

public partial class ModeButtonSettingItem : ObservableObject
{
    private readonly Action _onChanged;

    public ModeButtonSettingItem(int buttonIndex, string defaultLabel, int mode1, int mode2, int mode3, Action onChanged)
    {
        ButtonIndex = buttonIndex;
        _defaultLabel = defaultLabel;
        _mode1 = mode1;
        _mode2 = mode2;
        _mode3 = mode3;
        _onChanged = onChanged;
    }

    public int ButtonIndex { get; }
    public string DisplayName => $"Btn {ButtonIndex + 1}";

    [ObservableProperty]
    private string _defaultLabel = "";

    [ObservableProperty]
    private int _mode1;

    [ObservableProperty]
    private int _mode2;

    [ObservableProperty]
    private int _mode3;

    partial void OnDefaultLabelChanged(string value) => _onChanged();
    partial void OnMode1Changed(int value) => _onChanged();
    partial void OnMode2Changed(int value) => _onChanged();
    partial void OnMode3Changed(int value) => _onChanged();
}
