using System;
using System.Collections.Generic;
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
    public ISdrEngine GetEngineForSetup() => _engine;
    public void StopAudioOutputForShutdown() => _audioService.StopOutput();

    public ObservableCollection<ReceiverContext> Receivers { get; } = new();

    // 互換性のため残す
    public ReceiverContext Receiver1 => Receivers.Count > 0 ? Receivers[0] : null!;
    public ReceiverContext PrimaryReceiver => Receiver1;

    public TunerViewModel Tuner => Receivers.Count > 0 ? Receivers[0].Tuner : null!;
    public TunerViewModel PrimaryTuner => Tuner;
    public ZoomOverlayViewModel ZoomOverlay => Receivers.Count > 0 ? Receivers[0].ZoomOverlay : null!;
    public ZoomOverlayViewModel PrimaryZoomOverlay => ZoomOverlay;
    public ObservableCollection<SignalMeterSegment> SignalMeterSegments => Receivers.Count > 0 ? Receivers[0].SignalMeterSegments : null!;
    public ObservableCollection<SignalMeterSegment> PrimarySignalMeterSegments => SignalMeterSegments;
    public System.Collections.Generic.List<DemodWaveOverlayButton> DemodWaveButtons => Receivers.Count > 0 ? Receivers[0].DemodWaveButtons : null!;
    public System.Collections.Generic.List<DemodWaveOverlayButton> PrimaryDemodWaveButtons => DemodWaveButtons;

    public SpectrumOverlayViewModel SpectrumOverlay { get; } = new SpectrumOverlayViewModel();
    public WaterfallOverlayViewModel WaterfallOverlay { get; } = new WaterfallOverlayViewModel();

    public DisplayViewModel Display { get; }
    public SdrControlViewModel SdrControl { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public PluginWorkspaceViewModel PluginWorkspace { get; }

    public event EventHandler? UiTick;

    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly ILastStateService _lastStateService;
    private LastState _lastState;

    private SpectrumClickHandler _spectrumClickHandler = null!;
    private WaterfallClickHandler _waterfallClickHandler = null!;
    private ZoomWindowClickHandler _zoomWindowClickHandler = null!;



    private readonly InputCoordinationService _inputService = new();
    private int _sdrPlayRfGainDb = 20;
    private int _rtlSdrRfGainDb = 100;

    public void SyncAndSaveLastState(RadioControl radioControl)
    {
        if (IsRtlDevice) _rtlSdrRfGainDb = RfGainDb;
        else _sdrPlayRfGainDb = RfGainDb;

        _lastState.CenterFreqHz = radioControl.CenterFreqHz;
        _lastState.TunedFreqHz = radioControl.TunedFreqHz;
        _lastState.DemodMode = radioControl.DemodMode;
        _lastState.StepHz = radioControl.StepHz;
        _lastState.SdrPlayRfGainDb = _sdrPlayRfGainDb;
        _lastState.SdrPlaySensitivity = SdrPlaySensitivity;
        _lastState.RtlSdrRfGainDb = _rtlSdrRfGainDb;

        _lastState.WaterfallColorMode = radioControl.WaterfallColorMode;

        _lastState.IsR1Visible = radioControl.IsR1Visible;
        _lastState.IsPowerOn = radioControl.IsPowerOn;
        _lastState.IsSpeakerOn = radioControl.IsSpeakerOn;
        _lastState.IsSquelchOn = radioControl.IsSquelchOn;
        _lastState.SquelchDb = radioControl.SquelchDb;
        _lastState.IsZoomWindowVisible = radioControl.IsZoomWindowVisible;
        _lastState.SpanHz = radioControl.SpanHz;
        _lastState.MainSpanHz = Display.BaseMainSpanHz;

        _lastState.SpectrumBiasAdj = Display.SpectrumBiasAdj;
        _lastState.WaterfallBiasAdj = Display.WaterfallBiasAdj;
        _lastState.SpectrumZoomBiasAdj = Display.SpectrumZoomBiasAdj;
        _lastState.WaterfallZoomBiasAdj = Display.WaterfallZoomBiasAdj;


        _lastStateService.SaveLastState(_lastState);
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

    private void PersistFftBatchState(int fftBatchMode)
    {
        _lastState.FftBatchMode = fftBatchMode;
        _lastStateService.SaveLastState(_lastState);
    }
}
