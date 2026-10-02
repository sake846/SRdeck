using System.Collections.ObjectModel;
using SRdeckPlugin.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Configuration;
using SRdeck.Messages;
using SRdeck.Models;
using SRdeck.SDR;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // Existing bindings delegate to the separately owned application preferences.
    public bool IsPreventSleepOnAc { get => ApplicationSettings.IsPreventSleepOnAc; set => ApplicationSettings.IsPreventSleepOnAc = value; }
    public bool IsPreventSleepOnBattery { get => ApplicationSettings.IsPreventSleepOnBattery; set => ApplicationSettings.IsPreventSleepOnBattery = value; }
    public bool IsDisableWpfRenderingOnServer { get => ApplicationSettings.IsDisableWpfRenderingOnServer; set => ApplicationSettings.IsDisableWpfRenderingOnServer = value; }
    public bool IsResidualDcRemovalEnabled { get => ApplicationSettings.IsResidualDcRemovalEnabled; set => ApplicationSettings.IsResidualDcRemovalEnabled = value; }
    public bool IsCompactWpfMode => ApplicationSettings.IsDisableWpfRenderingOnServer;
    public SettingsComboBoxOption<PluginChannelAccelerationPreference>? SelectedDemodLightGpu { get => ApplicationSettings.SelectedDemodLightGpu; set => ApplicationSettings.SelectedDemodLightGpu = value; }
    public SettingsComboBoxOption<PluginChannelAccelerationPreference>? SelectedDemodStandardGpu { get => ApplicationSettings.SelectedDemodStandardGpu; set => ApplicationSettings.SelectedDemodStandardGpu = value; }
    public SettingsComboBoxOption<PluginChannelAccelerationPreference>? SelectedDemodHeavyGpu { get => ApplicationSettings.SelectedDemodHeavyGpu; set => ApplicationSettings.SelectedDemodHeavyGpu = value; }
    public SettingsComboBoxOption<string?>? SelectedProcessPriority { get => ApplicationSettings.SelectedProcessPriority; set => ApplicationSettings.SelectedProcessPriority = value; }
    public SettingsComboBoxOption<string?>? StartupProcessPriority { get => ApplicationSettings.StartupProcessPriority; set => ApplicationSettings.StartupProcessPriority = value; }
    public string Language { get => ApplicationSettings.Language; set => ApplicationSettings.Language = value; }

    // --- ComboBox Selections & Persistent Settings ---
    [ObservableProperty] private SettingsComboBoxOption<float?>? _selectedGridTopDb;
    [ObservableProperty] private SettingsComboBoxOption<int?>? _selectedDebugDraw;
    [ObservableProperty] private SettingsComboBoxOption<bool?>? _selectedIsGpuFftEnabled;
    [ObservableProperty] private SettingsComboBoxOption<int?>? _selectedFftResolutionMode;
    [ObservableProperty] private SettingsComboBoxOption<SdrDeviceType>? _selectedSdrDeviceType;

    partial void OnSelectedSdrDeviceTypeChanged(SettingsComboBoxOption<SdrDeviceType>? value)
    {
        if (value == null || _engine?.InitialAppSettings == null) return;
        if (_engine.InitialAppSettings.SdrDeviceType == value.Value) return;

        _engine.InitialAppSettings.SdrDeviceType = value.Value;
        _settingsService.SaveSettings(_engine.InitialAppSettings);
        IsRtlDevice = IsRtlSdrConfigured(value.Value);
        IsHackRfDevice = IsHackRfConfigured(value.Value);
        IsRx888Device = IsRx888Configured(value.Value);
        UpdateSampleRateOptions();
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





    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveBitDepthText))]
    private int _sdrPlaySampleRateHz;
    private bool _isSynchronizingSampleRateSelection;

    public string EffectiveBitDepthText => GetEffectiveBitDepthText();

    private string GetEffectiveBitDepthText()
    {
        if (IsRtlDevice || IsHackRfDevice)
        {
            return "有効 8 bit";
        }
        if (IsRx888Device)
        {
            return "有効 16 bit";
        }

        int sampleRate = SdrPlaySampleRateHz > 0 ? SdrPlaySampleRateHz : 6_000_000;
        if (sampleRate < 2_000_000)
        {
            return "有効 14 bit以上";
        }
        if (sampleRate <= 6_048_000)
        {
            return "有効 14 bit";
        }
        if (sampleRate <= 8_064_000)
        {
            return "有効 12 bit";
        }
        if (sampleRate <= 9_216_000)
        {
            return "有効 10 bit";
        }
        return "有効 8 bit";
    }

    public record SampleRateOption(string Label, int Value);
    public ObservableCollection<SampleRateOption> SampleRateOptions { get; } = new();

    private SdrDeviceKind CurrentSampleRateDeviceKind => IsRtlDevice
        ? SdrDeviceKind.RtlSdr
        : IsHackRfDevice
            ? SdrDeviceKind.HackRf
            : IsRx888Device ? SdrDeviceKind.Rx888 : SdrDeviceKind.SdrPlay;

    public void UpdateSampleRateOptions()
    {
        int previousSampleRateHz = SdrPlaySampleRateHz;
        bool wasSynchronizing = _isSynchronizingSampleRateSelection;
        _isSynchronizingSampleRateSelection = true;
        try
        {
            SampleRateOptions.Clear();
            foreach (int sampleRateHz in SdrSampleRatePolicy.GetSupportedRates(CurrentSampleRateDeviceKind))
            {
                SampleRateOptions.Add(new SampleRateOption(
                    FormattableString.Invariant($"{sampleRateHz / 1_000_000.0:0.#} MS/s"), sampleRateHz));
            }
        }
        finally
        {
            // Clearing a bound ComboBox can write an empty selection back to us.
            SdrPlaySampleRateHz = previousSampleRateHz;
            _isSynchronizingSampleRateSelection = wasSynchronizing;
        }
        // Zero means settings have not been loaded yet during construction.
        if (previousSampleRateHz > 0)
        {
            SdrPlaySampleRateHz = NormalizeSampleRateForDevice(previousSampleRateHz, CurrentSampleRateDeviceKind);
        }
        // Re-select even when the value did not change but the items were replaced.
        OnPropertyChanged(nameof(SdrPlaySampleRateHz));
    }

    partial void OnSdrPlaySampleRateHzChanged(int value)
    {
        if (_isSynchronizingSampleRateSelection || _engine?.InitialAppSettings == null) return;
        int normalizedValue = NormalizeSampleRateForDevice(value, CurrentSampleRateDeviceKind);
        if (value != normalizedValue)
        {
            SdrPlaySampleRateHz = normalizedValue;
            return;
        }
        bool settingsChanged = _engine.InitialAppSettings.SdrPlaySampleRateHz != value;
#if ENABLE_RX888
        if (_engine.SdrDevice is Rx888Mk2Controller rx888)
        {
            if (!rx888.ApplySampleRate(value))
            {
                SdrPlaySampleRateHz = rx888.FsHz;
                return;
            }
            var rx888Control = _engine.Control;
            rx888Control.FsHz = value;
            _engine.Control = rx888Control;
            _engine.EnsureIqBufferCapacity();
            SyncMainSpanOptionsToFs(
                value, isRtlDevice: false, isRx888Device: true, selectFullSpan: true);
            if (settingsChanged)
            {
                _engine.InitialAppSettings.SdrPlaySampleRateHz = value;
                _settingsService.SaveSettings(_engine.InitialAppSettings);
            }
            return;
        }
#endif
        if (settingsChanged)
        {
            _engine.InitialAppSettings.SdrPlaySampleRateHz = value;
            _settingsService.SaveSettings(_engine.InitialAppSettings);
        }

        if (_engine.SdrDevice is { } directDevice &&
            directDevice.Capabilities.Kind is SdrDeviceKind.RtlSdr or SdrDeviceKind.HackRf)
        {
            if (!directDevice.ApplySampleRate(value))
            {
                SdrPlaySampleRateHz = directDevice.FsHz;
                return;
            }
            var directControl = _engine.Control;
            directControl.FsHz = value;
            _engine.Control = directControl;
            _engine.EnsureIqBufferCapacity();
            SyncMainSpanOptionsToFs(value, isRtlDevice: IsRtlDevice, selectFullSpan: true);
        }
        else if (_engine.SdrDevice != null && _engine.SdrDevice.Capabilities.Kind == SdrDeviceKind.SdrPlay)
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

    internal void SyncSampleRateSelectionFromAppliedControl(int sampleRateHz)
    {
        if (sampleRateHz <= 0 || NormalizeSampleRateForDevice(sampleRateHz, CurrentSampleRateDeviceKind) != sampleRateHz ||
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
        if (!_engine.IsPlaying && _engine.SdrDevice?.FsHz == sampleRateHz)
            SyncMainSpanOptionsToFs(
                sampleRateHz, IsRtlDevice, IsRx888Device, selectFullSpan: true);
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
