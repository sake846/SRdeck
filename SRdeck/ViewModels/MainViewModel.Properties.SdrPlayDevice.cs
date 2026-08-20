using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Configuration;
using SRdeck.Models.Configuration;
using SRdeck.SDR;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private bool _isLoadingSdrPlayDeviceSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SdrPlayDeviceSettingsVisibility))]
    private bool _isSdrPlayDeviceSettingsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SdrPlayBiasTVisibility))]
    private bool _isSdrPlayBiasTAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SdrPlayAntennaVisibility))]
    private bool _isSdrPlayAntennaAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSdrPlayAntennaCVisible))]
    private bool _isSdrPlayAntennaCAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SdrPlayAmPortVisibility))]
    private bool _isSdrPlayAmPortAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SdrPlayExternalReferenceVisibility))]
    private bool _isSdrPlayExternalReferenceAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SdrPlayHdrVisibility))]
    private bool _isSdrPlayHdrAvailable;

    [ObservableProperty] private bool _sdrPlayBiasTEnabled;
    [ObservableProperty] private int _sdrPlayAntennaIndex;
    [ObservableProperty] private int _sdrPlayAmPortIndex;
    [ObservableProperty] private bool _sdrPlayExternalReferenceOutputEnabled;
    [ObservableProperty] private bool _sdrPlayHdrEnabled;
    [ObservableProperty] private int _sdrPlayHdrBandwidthIndex;

    public Visibility SdrPlayDeviceSettingsVisibility =>
        IsSdrPlayDeviceSettingsAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SdrPlayBiasTVisibility =>
        IsSdrPlayBiasTAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SdrPlayAntennaVisibility =>
        IsSdrPlayAntennaAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SdrPlayAmPortVisibility =>
        IsSdrPlayAmPortAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SdrPlayExternalReferenceVisibility =>
        IsSdrPlayExternalReferenceAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SdrPlayHdrVisibility =>
        IsSdrPlayHdrAvailable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsSdrPlayAntennaCVisible =>
        IsSdrPlayAntennaCAvailable ? Visibility.Visible : Visibility.Collapsed;

    partial void OnSdrPlayBiasTEnabledChanged(bool value) => SaveAndApplySdrPlayDeviceSettings();
    partial void OnSdrPlayAntennaIndexChanged(int value) => SaveAndApplySdrPlayDeviceSettings();
    partial void OnSdrPlayAmPortIndexChanged(int value) => SaveAndApplySdrPlayDeviceSettings();
    partial void OnSdrPlayExternalReferenceOutputEnabledChanged(bool value) => SaveAndApplySdrPlayDeviceSettings();
    partial void OnSdrPlayHdrEnabledChanged(bool value) => SaveAndApplySdrPlayDeviceSettings();
    partial void OnSdrPlayHdrBandwidthIndexChanged(int value) => SaveAndApplySdrPlayDeviceSettings();

    private void LoadSdrPlayDeviceSettings(HardwareSettings settings)
    {
        _isLoadingSdrPlayDeviceSettings = true;
        try
        {
            SdrPlayBiasTEnabled = settings.SdrPlayBiasTEnabled;
            SdrPlayAntennaIndex = Math.Clamp(settings.SdrPlayAntennaIndex, 0, 2);
            SdrPlayAmPortIndex = Math.Clamp(settings.SdrPlayAmPortIndex, 0, 1);
            SdrPlayExternalReferenceOutputEnabled = settings.SdrPlayExternalReferenceOutputEnabled;
            SdrPlayHdrEnabled = settings.SdrPlayHdrEnabled;
            SdrPlayHdrBandwidthIndex = Math.Clamp(settings.SdrPlayHdrBandwidthIndex, 0, 3);
        }
        finally
        {
            _isLoadingSdrPlayDeviceSettings = false;
        }

        ApplySdrPlayDeviceSettingsToController();
    }

    private void SyncSdrPlayDeviceSettingsAvailability()
    {
        var features = (_engine?.SdrDevice as SdrController)?.DeviceFeatures ?? default;
        IsSdrPlayBiasTAvailable = features.SupportsBiasT;
        IsSdrPlayAntennaAvailable = features.SupportsAntennaSelection;
        IsSdrPlayAntennaCAvailable = features.AntennaCount >= 3;
        IsSdrPlayAmPortAvailable = features.SupportsAmPort;
        IsSdrPlayExternalReferenceAvailable = features.SupportsExternalReferenceOutput;
        IsSdrPlayHdrAvailable = features.SupportsHdr;
        IsSdrPlayDeviceSettingsAvailable = features.SupportsBiasT || features.SupportsAntennaSelection ||
            features.SupportsAmPort || features.SupportsExternalReferenceOutput || features.SupportsHdr;

        int maxAntennaIndex = Math.Max(0, features.AntennaCount - 1);
        if (features.AntennaCount > 0 && SdrPlayAntennaIndex > maxAntennaIndex)
        {
            _isLoadingSdrPlayDeviceSettings = true;
            try { SdrPlayAntennaIndex = maxAntennaIndex; }
            finally { _isLoadingSdrPlayDeviceSettings = false; }
        }

        ApplySdrPlayDeviceSettingsToController();
    }

    private void SaveAndApplySdrPlayDeviceSettings()
    {
        if (_isLoadingSdrPlayDeviceSettings) return;

        HardwareSettings settings = _settingsService.LoadHardwareSettings(SdrDeviceType.SdrPlay);
        settings.SdrPlayBiasTEnabled = SdrPlayBiasTEnabled;
        settings.SdrPlayAntennaIndex = SdrPlayAntennaIndex;
        settings.SdrPlayAmPortIndex = SdrPlayAmPortIndex;
        settings.SdrPlayExternalReferenceOutputEnabled = SdrPlayExternalReferenceOutputEnabled;
        settings.SdrPlayHdrEnabled = SdrPlayHdrEnabled;
        settings.SdrPlayHdrBandwidthIndex = SdrPlayHdrBandwidthIndex;
        _settingsService.SaveHardwareSettings(settings, SdrDeviceType.SdrPlay);

        ApplySdrPlayDeviceSettingsToController();
    }

    private void ApplySdrPlayDeviceSettingsToController()
    {
        if (_engine?.SdrDevice is not SdrController controller) return;

        controller.BiasTEnabled = SdrPlayBiasTEnabled;
        controller.AntennaIndex = SdrPlayAntennaIndex;
        controller.AmPortIndex = SdrPlayAmPortIndex;
        controller.ExternalReferenceOutputEnabled = SdrPlayExternalReferenceOutputEnabled;
        controller.HdrEnabled = SdrPlayHdrEnabled;
        controller.HdrBandwidthIndex = SdrPlayHdrBandwidthIndex;
        controller.ApplyDeviceSpecificSettings();
    }
}
