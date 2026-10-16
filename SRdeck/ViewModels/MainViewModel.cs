using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Models;
using SRdeck.Configuration;
using SRdeck.Services;
using SRdeck.Services.Plugins;
using SRdeck.ViewModels.Components;
using SRdeckPlugin.Contracts;

namespace SRdeck.ViewModels;

/// <summary>
/// MVVMパターンのメイン画面 MainWindow の ViewModel です。
/// 各種機能ドメインごとの部分クラスに分割されています。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<ModeButtonSettingItem> ModeButtonSettings { get; } = new();
    private readonly ISdrEngine _engine;
    private readonly IAudioService _audioService;
    private readonly IPluginManager _pluginManager;
    private readonly FrequencyCatalogStore _frequencyCatalog = new();
    public ISdrEngine GetEngineForSetup() => _engine;
    public void StopAudioOutputForShutdown() => _audioService.StopOutput();

    public TunerViewModel Tuner { get; }
    public ZoomOverlayViewModel ZoomOverlay { get; } = new();
    public ObservableCollection<SignalMeterSegment> SignalMeterSegments { get; } = new();

    public SpectrumOverlayViewModel SpectrumOverlay { get; } = new SpectrumOverlayViewModel();
    public WaterfallOverlayViewModel WaterfallOverlay { get; } = new WaterfallOverlayViewModel();

    public DisplayViewModel Display { get; }
    public SdrControlViewModel SdrControl { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public ApplicationSettingsViewModel ApplicationSettings { get; }
    public SettingsPersistenceViewModel SettingsPersistence { get; }
    public PluginWorkspaceViewModel PluginWorkspace { get; }

    public event EventHandler? UiTick;

    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly RadioStatePersistence _statePersistence;

    private SpectrumClickHandler _spectrumClickHandler = null!;
    private WaterfallClickHandler _waterfallClickHandler = null!;
    private ZoomWindowClickHandler _zoomWindowClickHandler = null!;



    private readonly InputCoordinationService _inputService = new();

    public void SyncAndSaveLastState(RadioControl radioControl)
    {
        if (IsRtlDevice) _statePersistence.RtlSdrRfGainDb = RfGainDb;
        else if (IsHackRfDevice) _statePersistence.HackRfRfGainDb = RfGainDb;
        else if (IsRx888Device) _statePersistence.Rx888RfGainDb = RfGainDb;
        else _statePersistence.SdrPlayRfGainDb = RfGainDb;

        _statePersistence.SaveRadioState(radioControl, SdrPlaySensitivity, new(
            Display.BaseMainSpanHz, Display.SpectrumBiasAdj, Display.WaterfallBiasAdj,
            Display.SpectrumZoomBiasAdj, Display.WaterfallZoomBiasAdj));
    }

    private (bool isGpuEnabled, int fftResolutionMode, int fftBatchMode) GetDeviceFftState()
    {
        bool isGpuEnabled = true;
        int fftResolutionMode = 1; // 8K (Index 1)
        int fftBatchMode = 0; // 1 time (Index 0)

        if (_engine?.InitialAppSettings?.Display != null)
        {
            isGpuEnabled = _engine.InitialAppSettings.Display.IsGpuFftEnabled;
            fftResolutionMode = _engine.InitialAppSettings.Display.FftResolutionMode;
        }

        return (isGpuEnabled, fftResolutionMode, fftBatchMode);
    }

    private void PersistFftBatchState(int fftBatchMode) =>
        _statePersistence.SaveFftBatchMode(fftBatchMode);
}
