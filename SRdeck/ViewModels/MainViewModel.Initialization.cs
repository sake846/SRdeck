using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Configuration;
using SRdeck.DSP;
using SRdeck.Messages;
using SRdeck.Services;
using SRdeck.Services.Plugins;
using SRdeck.ViewModels.Components;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public MainViewModel(ISdrEngine engine, IAudioService audioService, IRadioSessionController sessionController, IDialogService dialogService, ISettingsService settingsService, ILastStateService lastStateService, IPluginManager pluginManager, IPluginIqDispatcher pluginIqDispatcher, PluginWorkspaceViewModel pluginWorkspace)
    {
        _engine = engine;
        _audioService = audioService;
        _pluginManager = pluginManager;
        PluginWorkspace = pluginWorkspace;
        PluginWorkspace.PropertyChanged += OnPluginWorkspacePropertyChanged;
        _dialogService = dialogService;
        _settingsService = settingsService;
        SettingsPersistence = new(_settingsService, lastStateService, _dialogService, _frequencyCatalog);
        _statePersistence = new RadioStatePersistence(lastStateService);
        ApplicationSettings = new(_engine, _settingsService, lastStateService, () => _statePersistence.State);
        ApplicationSettings.PowerSettingsChanged += SyncSleepPrevention;
        ApplicationSettings.LanguageResourceChanged += HandleApplicationLanguageChanged;
        ApplicationSettings.PropertyChanged += HandleApplicationSettingsPropertyChanged;
        SdrPlaySensitivity = Math.Clamp(_statePersistence.State.SdrPlaySensitivity, 0, 100);
        IsReceiver1Visible = true;

        Display = new DisplayViewModel(); 
        Diagnostics = new DiagnosticsViewModel(_engine, pluginManager, pluginIqDispatcher);

        Display.SpectrumBiasAdj = _statePersistence.State.SpectrumBiasAdj;
        Display.WaterfallBiasAdj = _statePersistence.State.WaterfallBiasAdj;
        Display.SpectrumZoomBiasAdj = _statePersistence.State.SpectrumZoomBiasAdj;
        Display.WaterfallZoomBiasAdj = _statePersistence.State.WaterfallZoomBiasAdj;
        // 繧ｹ繝壹け繝医Λ繝隱ｿ謨ｴ蛟､遲峨・蠑輔″邯吶℃
        _engine.SpectrumBiasAdj = _statePersistence.State.SpectrumBiasAdj;
        _engine.WaterfallBiasAdj = _statePersistence.State.WaterfallBiasAdj;
        _engine.SpectrumZoomBiasAdj = _statePersistence.State.SpectrumZoomBiasAdj;
        _engine.WaterfallZoomBiasAdj = _statePersistence.State.WaterfallZoomBiasAdj;
        Display.PropertyChanged += HandleDisplayPropertyChanged;
        SdrControl = new SdrControlViewModel(_engine, sessionController, _dialogService, QueueWindowTitle);
        SdrControl.PropertyChanged += HandleSdrControlPropertyChanged;

        Tuner = new TunerViewModel(_engine, 1);
        Tuner.PropertyChanged += OnPrimaryTunerPropertyChanged;

        SyncButtonStates();
        UpdateSampleRateOptions();

        FrequencyDisplayOptions.Add(new FrequencyDisplayOption { Mode = FrequencyDisplayMode.Both, Label = "バンド・局名表示" });
        FrequencyDisplayOptions.Add(new FrequencyDisplayOption { Mode = FrequencyDisplayMode.BandOnly, Label = "バンド表示" });
        FrequencyDisplayOptions.Add(new FrequencyDisplayOption { Mode = FrequencyDisplayMode.StationOnly, Label = "局名表示" });
        FrequencyDisplayOptions.Add(new FrequencyDisplayOption { Mode = FrequencyDisplayMode.None, Label = "表示しない" });
        SdrControl.FrequencyDisplayOptions = FrequencyDisplayOptions;
        SdrControl.OpenStationCatalogCommand = OpenStationCatalogCommand;
        SdrControl.OpenBandPlanCatalogCommand = OpenBandPlanCatalogCommand;
        SdrControl.FrequencyDisplayOptionChanged = value => SelectedFrequencyDisplayOption = value;
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_lifetimeEnded, this);
        ReadSettings();
        ReloadFrequencyCatalogs();

        InitializeModeButtonSettings();

        InitializeUiControllers(); InitializeParameters(); InitializeAudioEngine(); SyncSelectedFrequencyDisplayOption();
        WindowTitle = AppConstants.DEFAULT_WINDOW_TITLE; Tuner?.BuildCenterFreqDigits();
        _engine.StateUpdated += HandleEngineStateUpdated;
        _engine.OnTitleChanged += HandleEngineTitleChanged;

        WeakReferenceMessenger.Default.Register<RadioControlUpdateMessage>(this, (r, m) => {
            var vm = r as MainViewModel;
            if (vm != null && !vm._lifetimeEnded) {
                var priority = m.IsCursorOnly
                    ? System.Windows.Threading.DispatcherPriority.Render
                    : System.Windows.Threading.DispatcherPriority.Background;
                Application.Current.Dispatcher.InvokeAsync(() => {
                    if (vm._lifetimeEnded) return;
                    if (m.IsCursorOnly) {
                        vm.SyncCursorOverlayVisuals(vm._engine.Control);
                    } else {
                        if (m.ResetMainViewZoom) {
                            vm.QueueActiveDisplayRestoreAfterRetune();
                            vm.SyncMainSpanForAtomicViewUpdate(0);
                        }
                        vm.SyncSampleRateSelectionFromAppliedControl(vm._engine.Control.FsHz);
                        vm.Tuner.SyncFrequencyFromAppliedControl(m.NewControl);
                        vm.SyncState(vm._engine.Control, vm._engine.State);
                        vm.UiTick?.Invoke(vm, EventArgs.Empty); 
                        vm.SyncAndSaveLastState(m.NewControl);
                    }
                }, priority);
            }
        });
        WeakReferenceMessenger.Default.Register<SdrErrorMessage>(this, (r, m) => {
            Application.Current.Dispatcher.InvokeAsync(() => {
                if (_lifetimeEnded) return;
                if (string.IsNullOrEmpty(SdrErrorMessageText))
                {
                    SdrErrorMessageText = m.Message;
                }
                else
                {
                    SdrErrorMessageText += "\n\n" + m.Message;
                }
                SdrErrorOverlayVisibility = Visibility.Visible;
                System.Media.SystemSounds.Hand.Play();
            });
        });
        WeakReferenceMessenger.Default.Register<BiasUpdateMessage>(this, (r, m) => {
            Application.Current.Dispatcher.InvokeAsync(() => {
                if (_lifetimeEnded) return;
                _engine.SpectrumBiasAdj = m.SpectrumBiasAdj; _engine.WaterfallBiasAdj = m.WaterfallBiasAdj; _engine.SpectrumZoomBiasAdj = m.SpectrumZoomBiasAdj; _engine.WaterfallZoomBiasAdj = m.WaterfallZoomBiasAdj;
                _engine.NeedsBackgroundRedraw = true; SyncState(_engine.Control, _engine.State);
            });
        });
        WeakReferenceMessenger.Default.Register<ZoomModeUpdateMessage>(this, (r, m) => {
            Application.Current.Dispatcher.InvokeAsync(() => {
                if (_lifetimeEnded) return;
                var p = _engine.Control;
                if (m.ReceiverIndex == 0 || m.ReceiverIndex == 1) p.ZoomSpectrumMode = m.Mode;
                _engine.Control = p; _engine.NeedsBackgroundRedraw = true; SyncState(_engine.Control, _engine.State);
            });
        });
        WeakReferenceMessenger.Default.Register<SdrDeviceInfoMessage>(this, (r, m) => { Application.Current.Dispatcher.InvokeAsync(() => {
                if (_lifetimeEnded) return;
            DeviceName = m.ModelName;
            DeviceSn = !string.IsNullOrEmpty(m.SerialNumber) ? $" S/N: {m.SerialNumber}" : string.Empty;
            SyncDeviceIndicatorMode(m.ModelName);
            SyncSdrPlayDeviceSettingsAvailability();
            SyncMainSpanOptionsToFs(
                _engine.SdrDevice?.FsHz > 0 ? _engine.SdrDevice.FsHz : _engine.Control.FsHz,
                IsRtlDevice || IsRtlSdrDeviceController(),
                IsRx888Device || IsRx888DeviceController());
            WindowTitle = $"SRdeck  [ {m.ModelName} , S/N: {m.SerialNumber} ]"; 
            if (GetCurrentDeviceRfGain() <= 0)
            {
                RfGainDb = m.InitialRfGain;
            }
            var p = _engine.Control;
            p.RfGainDb = RfGainDb;
            _engine.Control = p;
            _engine.CurrentGainDb = RfGainDb;
            _engine.GainChange();
        }); });
        SyncState(_engine.Control, _engine.State);

        PowerStateManager.StartMonitoring();
        PowerStateManager.PowerStatusChanged += HandlePowerStatusChanged;
        SyncSleepPrevention();
        _ = DetectSdrInternal(showErrors: false);
    }

    private async Task SyncPluginStreamingAsync(bool isStarted)
    {
        PluginOperationResult result = isStarted
            ? await _pluginManager.StartStreamAsync()
            : await _pluginManager.StopStreamAsync();
        if (!result.Succeeded) Console.Error.WriteLine(result.Error);
    }

    private void InitializeUiControllers() { _spectrumClickHandler = new SpectrumClickHandler(); _waterfallClickHandler = new WaterfallClickHandler(); _zoomWindowClickHandler = new ZoomWindowClickHandler(); }

    private void InitializeParameters()
    {
        if (!Enum.IsDefined(_statePersistence.State.DemodMode))
            _statePersistence.State.DemodMode = DemodulationMode.None;

        var displayMode = GetActiveFrequencyDisplayMode();
        bool isBandPlanVisible = (displayMode == FrequencyDisplayMode.Both || displayMode == FrequencyDisplayMode.BandOnly);
        bool isStationNameVisible = (displayMode == FrequencyDisplayMode.Both || displayMode == FrequencyDisplayMode.StationOnly);
        bool isGpuFftEnabled = false; int fftResolutionMode = 0; int fftBatchMode = 0;
        int colorMode = 0;
        float gridTopDb = _engine.InitialAppSettings.Display.GridTopDb ?? AppConstants.DEFAULT_GRID_TOP_DB;
        SpectrumOverlay.GridTopDb = gridTopDb;
        bool isDebugVisible = _engine.InitialAppSettings.Display.DebugDraw == 1;
 
        bool isRtlSdrDevice = IsRtlSdrConfigured(_engine.InitialAppSettings.SdrDeviceType)
            || IsRtlSdrDeviceController();
        bool isHackRfDevice = IsHackRfConfigured(_engine.InitialAppSettings.SdrDeviceType)
            || IsHackRfDeviceController();
        bool isRx888Device = IsRx888Configured(_engine.InitialAppSettings.SdrDeviceType)
            || IsRx888DeviceController();
 
 
        var deviceFft = GetDeviceFftState();
        isGpuFftEnabled = deviceFft.isGpuEnabled;
        fftResolutionMode = deviceFft.fftResolutionMode;
        fftBatchMode = deviceFft.fftBatchMode;
        SyncDeviceIndicatorMode(isRtlSdrDevice
            ? "RTL-SDR"
            : isHackRfDevice ? "HackRF One" : isRx888Device ? "RX-888 MK2" : "SDRplay");
        SdrDeviceKind initialKind = isRtlSdrDevice
            ? SdrDeviceKind.RtlSdr
            : isHackRfDevice
                ? SdrDeviceKind.HackRf
                : isRx888Device ? SdrDeviceKind.Rx888 : SdrDeviceKind.SdrPlay;
        int initialFsHz = SdrSampleRatePolicy.Normalize(_engine.InitialAppSettings.SdrPlaySampleRateHz, initialKind);
        if (_engine.SdrDevice != null)
        {
            _engine.SdrDevice.FsHz = initialFsHz;
        }
        Display.SyncMainSpanOptionsForDevice(isRtlSdrDevice, initialFsHz, isRx888Device);
        RestoreStartupMainSpanSelection(initialFsHz);
 
        var radioControl = new RadioControl {
            FsHz = initialFsHz, CenterFreqHz = _statePersistence.State.CenterFreqHz, TunedFreqHz = _statePersistence.State.TunedFreqHz, FreqOffsetHz = _statePersistence.State.TunedFreqHz - _statePersistence.State.CenterFreqHz,
            BiasPpm = _engine.SdrBiasPpm, AdjustmentPpm = 0f, IsPowerOn = _statePersistence.State.IsPowerOn, IsSpeakerOn = _statePersistence.State.IsSpeakerOn, IsSquelchOn = _statePersistence.State.IsSquelchOn, IsZoomWindowVisible = _statePersistence.State.IsZoomWindowVisible, SquelchDb = _statePersistence.State.SquelchDb, RfGainDb = RfGainDb, DemodMode = _statePersistence.State.DemodMode,
            IsGpuFftEnabled = isGpuFftEnabled, FftResolutionMode = fftResolutionMode, FftBatchCount = (fftBatchMode >= 0 && fftBatchMode < FftBatchOptions.Count) ? FftBatchOptions[fftBatchMode].Count : 1,
            IsR1Visible = true, IsBandPlanVisible = isBandPlanVisible, IsStationNameVisible = isStationNameVisible,
            IsAfcEnabled = false,
            WaterfallColorMode = colorMode, IsDebugVisible = isDebugVisible,
            SpanHz = _statePersistence.State.SpanHz,
            ZoomSpectrumMode = 0, IsMonoMode = false,
            MainSpanHz = Display != null ? Display.CurrentMainSpanHz : (int)AppConstants.FULL_BW,
            BaseMainSpanHz = Display != null ? Display.BaseMainSpanHz : (int)AppConstants.FULL_BW
        };
        ApplyModeDefaults(ref radioControl, radioControl.DemodMode, 1);
        
        // ApplyModeDefaults がステップをリセットするため、その後に LastState から前回のステップを復元する
        radioControl.StepHz = _statePersistence.State.StepHz > 0 ? _statePersistence.State.StepHz : radioControl.StepHz;

        _engine.Control = radioControl; _engine.CurrentGainDb = RfGainDb;
        _engine.EnsureIqBufferCapacity();
        int rHz = Display != null ? Display.CurrentMainRoundingHz : 500000;
        Tuner.CenterFreqRoundingHz = rHz;

        radioControl = _engine.Control; SyncAutoStep(ref radioControl); _engine.Control = radioControl;
        IsGpuFftEnabled = isGpuFftEnabled; FftResolutionMode = fftResolutionMode; FftBatchMode = fftBatchMode;
        ApplyFrequencyDisplayMode(displayMode);
        if (Display != null) { Display.IsBandPlanVisible = isBandPlanVisible; Display.ZoomMode = radioControl.ZoomSpectrumMode; }

        if (_engine.InitialAppSettings != null && _engine.InitialAppSettings.Power != null)
        {
            IsPreventSleepOnAc = _engine.InitialAppSettings.Power.PreventSleepOnAc;
            IsPreventSleepOnBattery = _engine.InitialAppSettings.Power.PreventSleepOnBattery;
            IsDisableWpfRenderingOnServer = _engine.InitialAppSettings.Power.DisableWpfRenderingOnServer;
        }
        if (_engine.InitialAppSettings != null)
        {
            SdrPlaySampleRateHz = _engine.InitialAppSettings.SdrPlaySampleRateHz;
            if (_engine.InitialAppSettings.Display != null)
            {
                SelectedSdrDeviceType = SdrDeviceTypeOptions.Find(o => o.Value == _engine.InitialAppSettings.SdrDeviceType) ?? SdrDeviceTypeOptions[0];
                SelectedGridTopDb = GridTopDbOptions.Find(o => o.Value == _engine.InitialAppSettings.Display.GridTopDb) ?? GridTopDbOptions[0];
                SelectedDebugDraw = DebugDrawOptions.Find(o => o.Value == _engine.InitialAppSettings.Display.DebugDraw) ?? DebugDrawOptions[0];
                SelectedIsGpuFftEnabled = IsGpuFftEnabledOptions.Find(o => o.Value == _engine.InitialAppSettings.Display.IsGpuFftEnabled) ?? IsGpuFftEnabledOptions[0];
                SelectedFftResolutionMode = FftResolutionModeOptions.Find(o => o.Value == _engine.InitialAppSettings.Display.FftResolutionMode) ?? FftResolutionModeOptions[1]; // 8K (Index 1)
            }
            if (_engine.InitialAppSettings.Demodulation != null)
            {
                var demod = _engine.InitialAppSettings.Demodulation;
                _engine.SetWorkloadAccelerationPreferences(
                    demod.LightWorkloadPreference,
                    demod.StandardWorkloadPreference,
                    demod.HeavyWorkloadPreference);
                SelectedDemodLightGpu = DemodChannelAccelerationOptions.Find(o => o.Value == demod.LightWorkloadPreference) ?? DemodChannelAccelerationOptions[0];
                SelectedDemodStandardGpu = DemodChannelAccelerationOptions.Find(o => o.Value == demod.StandardWorkloadPreference) ?? DemodChannelAccelerationOptions[0];
                SelectedDemodHeavyGpu = DemodChannelAccelerationOptions.Find(o => o.Value == demod.HeavyWorkloadPreference) ?? DemodChannelAccelerationOptions[0];
            }
            IsResidualDcRemovalEnabled =
                _engine.InitialAppSettings.SignalProcessing.ResidualDcRemovalEnabled;

            if (_engine.InitialAppSettings.Power != null)
            {
                string? startupSetting = _engine.InitialAppSettings.Power.ProcessPriority;
                StartupProcessPriority = StartupProcessPriorityOptions.Find(o => o.Value == startupSetting) ?? StartupProcessPriorityOptions.Find(o => o.Value == null);

                string? initPriority = _statePersistence.State.ProcessPriority;
                if (!string.IsNullOrEmpty(startupSetting))
                {
                    initPriority = startupSetting;
                }
                SelectedProcessPriority = ProcessPriorityOptions.Find(o => o.Value == initPriority) ?? ProcessPriorityOptions.Find(o => o.Value == "Normal");
                ApplicationSettingsViewModel.ApplyProcessPriority(SelectedProcessPriority?.Value ?? "Normal");
            }
            Language = _engine.InitialAppSettings.Language ?? "ja";
            SyncWpfLanguageResource(Language);
        }
        SyncState(_engine.Control, _engine.State);
        _engine.State = new RadioState { RxRssi = AppConstants.MIN_RSSI_DB, AveRxPwr = AppConstants.MIN_RSSI_DB, AveDb = AppConstants.MIN_RSSI_DB, MinFftPwr = AppConstants.MIN_RSSI_DB };
        SyncState(_engine.Control, _engine.State); _engine.ResetDiagnostics(); BuildSignalMeter();
        InitializeCursorTimer();
    }

    private void InitializeAudioEngine() { _audioService.InitializeOutput(32000, 2); _audioService.PlayOutput(); }
    private void ApplyStartupMainSpanSelection()
    {
        int previousMainSpanHz = Display.IsMainViewZoomed ? Display.CurrentMainSpanHz : 0;
        int lastAppliedDisplayWidthHz = _lastAppliedWaterfallDisplayWidthHz;
        int previousFsHz = _engine.Control.FsHz;
        int fsHz = _engine.SdrDevice?.FsHz > 0 ? _engine.SdrDevice.FsHz : previousFsHz;
        if (fsHz <= 0) return;

        SyncMainSpanOptionsToFs(
            fsHz,
            IsRtlDevice || IsRtlSdrDeviceController(),
            IsRx888Device || IsRx888DeviceController(),
            selectFullSpan: previousFsHz > 0 && previousFsHz != fsHz);
        if (previousMainSpanHz > 0 && previousMainSpanHz != lastAppliedDisplayWidthHz)
            Display.ApplyPreferredMainSpanHz(previousMainSpanHz);
        else
            ApplyActiveWaterfallDisplayRequest(bandwidthChanged: true);
    }

    private void SyncMainSpanOptionsToFs(
        int fsHz,
        bool isRtlDevice,
        bool isRx888Device = false,
        bool selectFullSpan = false)
    {
        if (fsHz <= 0) return;

        Display.SyncMainSpanOptionsForDevice(isRtlDevice, fsHz, isRx888Device);
        Display.SelectedMainSpanHz = Display.BaseMainSpanHz;

        var p = _engine.Control;
        p.FsHz = fsHz;
        p.MainSpanHz = Display.CurrentMainSpanHz;
        p.BaseMainSpanHz = Display.BaseMainSpanHz;
        p.ApplyPrimaryReceiverTuning();
        _engine.Control = p;
        ApplyActiveWaterfallDisplayRequest();
        // The configured FFT preference may have been selected for a faster
        // device. Re-evaluate it after the real input rate becomes known; an
        // oversized native GPU FFT can block in the driver and prevent WPF
        // shutdown as well as live rendering.
        ApplyFftResolutionLimit();
    }

    private void RestoreStartupMainSpanSelection(int fsHz)
    {
        if (Display.VisibleMainSpanOptions.Count == 0)
        {
            return;
        }

        Display.SelectedMainSpanHz = Display.BaseMainSpanHz;
    }
    private void SyncSelectedFrequencyDisplayOption() { FrequencyDisplayMode mode = (IsBandPlanVisible, IsStationNameVisible) switch { (true, true) => FrequencyDisplayMode.Both, (true, false) => FrequencyDisplayMode.BandOnly, (false, true) => FrequencyDisplayMode.StationOnly, _ => FrequencyDisplayMode.None }; var option = System.Linq.Enumerable.FirstOrDefault(FrequencyDisplayOptions, o => o.Mode == mode); if (SelectedFrequencyDisplayOption != option) SelectedFrequencyDisplayOption = option; }

    public void SyncSleepPrevention()
    {
        if (!_lifetimeEnded) ApplicationSettings.ApplySleepPrevention(IsStarted);
    }

    private void InitializeModeButtonSettings()
    {
        ModeButtonSettings.Clear();
        var appSettings = _engine.InitialAppSettings;
        if (appSettings == null || appSettings.ModeButtons == null) return;

        Tuner?.LoadModeButtonSettings(appSettings);

        for (int i = 0; i < appSettings.ModeButtons.Count; i++)
        {
            var cfg = appSettings.ModeButtons[i];
            int buttonIndex = i;
            var item = new ModeButtonSettingItem(
                buttonIndex,
                cfg.DefaultLabel,
                cfg.Mode1,
                cfg.Mode2,
                cfg.Mode3,
                () => {
                    if (_engine.InitialAppSettings?.ModeButtons != null && buttonIndex < _engine.InitialAppSettings.ModeButtons.Count)
                    {
                        var target = ModeButtonSettings[buttonIndex];
                        var savedCfg = _engine.InitialAppSettings.ModeButtons[buttonIndex];
                        savedCfg.DefaultLabel = target.DefaultLabel;
                        savedCfg.Mode1 = target.Mode1;
                        savedCfg.Mode2 = target.Mode2;
                        savedCfg.Mode3 = target.Mode3;
                        _settingsService.SaveSettings(_engine.InitialAppSettings);
                        
                        Tuner?.LoadModeButtonSettings(_engine.InitialAppSettings);
                    }
                }
            );
            ModeButtonSettings.Add(item);
        }
    }
}
