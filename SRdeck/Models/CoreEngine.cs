using System;
using System.Windows;
using System.Windows.Threading;
using SRdeckPlugin.Contracts;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.DSP;
using SRdeck.Audio;
using SRdeck.Messages;
using SRdeck.SDR;
using SRdeck.Models.SDR;
using SRdeck.Services;
using SRdeck.Services.Plugins;

namespace SRdeck.Models;

public partial class CoreEngine : ISdrEngine
{
    private readonly ISdrDeviceManager _sdrDeviceManager;
    private readonly IPluginIqDispatcher _pluginIqDispatcher;
    private SdrDeviceCapabilities DeviceCapabilities => _sdrDeviceManager.Capabilities;
    public ISdrDevice? SdrDevice
    {
        get => _sdrDeviceManager.Device;
        set => _sdrDeviceManager.Device = value;
    }

    private void HandleGainHardwareChanged(double systemDb, int gainReductionDb) { SystemDb = systemDb + SystemGainOffset; _agcManager.CurrentGainDb = gainReductionDb; }
    public void SyncSdrProperties() => SyncSdrProperties(Control);

    private void SyncSdrProperties(RadioControl control)
    {
        var sdrDevice = SdrDevice;
        // Playback uses its own sample rate and tuning context. Propagating that
        // control snapshot to the stopped SDR device would overwrite the device's
        // configured sample rate before the next hardware start.
        if (sdrDevice == null || IsPlaying) return;
        if (_sdrDeviceManager.TryInitialize(out SdrDeviceInitialization initialization))
        {
            CurrentGainDb = initialization.CurrentGainDb;
        }
        _rfAgc = RfAgcEnabled == 1;
        _sdrDeviceManager.Synchronize(
            control,
            new SdrDevicePropertyValues(
                control.FsHz,
                GetInputCenterFrequency(control),
                CurrentGainDb,
                control.AdjustmentPpm,
                SdrBiasPpm),
            (long)(GetBufferSampleRateHz() * AppConstants.SDR_FREQUENCY_SWITCH_DELAY_SEC));
    }

    public event Action<string?>? OnTitleChanged;
    public event Action? OnFileFrequencyChanged;
    public event Action? StateUpdated;
    public event Action? DemodHistoryUpdated;
    public event Action? DeviceRemoved;
    public event Action? StreamStalled;

    public void SetWorkloadAccelerationPreferences(
        PluginChannelAccelerationPreference light,
        PluginChannelAccelerationPreference standard,
        PluginChannelAccelerationPreference heavy)
    {
        _pluginIqDispatcher.SetWorkloadAccelerationPreferences(light, standard, heavy);
    }

    private bool _isDisposed = false;
    private readonly IRadioControlStore _radioControlStore;
    public RadioControl Control
    {
        get => _radioControlStore.Snapshot;
        set
        {
            _radioControlStore.Update(_ =>
            {
                var constrainedControl = value;
                if (IsPlaying && _audioService.PlaybackSampleRateHz > 0)
                {
                    constrainedControl.FsHz = _audioService.PlaybackSampleRateHz;
                }
                return ConstrainDeviceFrequency(constrainedControl);
            });
        }
    }

    public SRdeck.Configuration.AppSettings InitialAppSettings { get; set; } = new SRdeck.Configuration.AppSettings();
    private readonly IRadioStateStore _radioStateStore;
    public RadioState State
    {
        get => _radioStateStore.PublishedState;
        set => _radioStateStore.Replace(value);
    }

    public void SetZoomHighResolutionMode(int receiverIndex, bool isHighResolution) =>
        _radioStateStore.SetZoomHighResolutionMode(receiverIndex, isHighResolution);
    private readonly IRadioDiagnosticsStore _diagnosticsStore;
    public RadioDiagnostics Diagnostics => _diagnosticsStore.Snapshot;
    public void UpdateDiagnostics(RadioDiagnosticsMutator mutator) => _diagnosticsStore.Update(mutator);
    public void ResetDiagnostics() => _diagnosticsStore.Reset();
    public int BiasDemod => (int)(RfCalibrationOffset + 18.0f);
    public int RfHzOld;
    private readonly object _tuningSynchronizationLock = new();
    public const int UI_FFT_SIZE = AppConstants.FFT_SIZE;
    private readonly IMainFftService _mainFftService;
    private readonly IAgcManager _agcManager;
    private readonly IAudioService _audioService;
    private readonly ISignalPipeline _signalPipeline;
    private readonly IRadioProcessingPipeline _processingPipeline;
    private readonly ITuningCoordinator _tuningCoordinator;
    public float[] SpectrumFftData { get => _mainFftService.SpectrumData; set => _mainFftService.SpectrumData = value; }
    public float[] WaterfallFftData { get => _mainFftService.WaterfallData; set => _mainFftService.WaterfallData = value; }
    public IFftProcessor? FftProcessor => _mainFftService.Processor;
    private long _lastMainFftTriggerSample = 0;

    private int _requestedSpectrumWidth = 1000;
    public int RequestedSpectrumWidth { get => _requestedSpectrumWidth; set => _requestedSpectrumWidth = Math.Max(10, value); }
    private readonly IInputSessionStateMachine _inputSessionState;
    public InputSessionState SessionState => _inputSessionState.Current;
    public bool IsPlaying => _inputSessionState.IsPlaying;
    public bool IsSdrRunning => _inputSessionState.IsSdrRunning;

    public bool TryStartSdrSession() => _inputSessionState.TryStart(InputSessionState.ReceivingSdr);
    public bool TryStartPlaybackSession() => _inputSessionState.TryStart(InputSessionState.PlayingFile);
    public void StopSdrSession() => _inputSessionState.Stop(InputSessionState.ReceivingSdr);
    public void StopPlaybackSession() => _inputSessionState.Stop(InputSessionState.PlayingFile);
    public int BufferSize => _signalPipeline.BufferSize;
    public IqSampleRingBuffer IqBuffer => _signalPipeline.IqBuffer;
    public float[] BufferGains => _signalPipeline.GainHistory;
    public int[] BufferFrequencies => _signalPipeline.FrequencyHistory;
    public int BufferWPtr { get => _signalPipeline.WritePointer; set => _signalPipeline.WritePointer = value; }
    public int BufferRPtr { get => _signalPipeline.ReadPointer; set => _signalPipeline.ReadPointer = value; }
    public int BufferRPtrNow { get => _signalPipeline.CurrentReadPointer; set => _signalPipeline.CurrentReadPointer = value; }
    public long CurrentReadAbsoluteSampleEnd => _signalPipeline.CurrentReadAbsoluteSampleEnd;
    public int BufferRPtrNext { get => _signalPipeline.NextReadPointer; set => _signalPipeline.NextReadPointer = value; }
    public int LatestBufferPointer => BufferRPtrNext;
    public long TotalSamplesReceived { get => _signalPipeline.TotalSamplesReceived; set => _signalPipeline.TotalSamplesReceived = value; }
    public double SystemDb { get; set; }
    public int CurrentGainDb { get => _agcManager.CurrentGainDb; set => _agcManager.CurrentGainDb = value; }
    public bool ResidualDcRemovalEnabled
    {
        get => _signalPipeline.ResidualDcRemovalEnabled;
        set => _signalPipeline.ResidualDcRemovalEnabled = value;
    }
    public void ResetResidualDcRemoval() => _signalPipeline.ResetResidualDcRemoval();
    public void GainChange() { if (SdrDevice != null) { SdrDevice.RfGainDb = CurrentGainDb; SdrDevice.GainChange(); } }
    public void EnsureIqBufferCapacity()
    {
        int sampleRateHz = GetBufferSampleRateHz();
        if (_signalPipeline.EnsureIqBufferCapacity(sampleRateHz))
        {
            ResetPointersForRestart();
        }

        if (_signalPipeline.EnsureDemodulationCapacity(
            _radioStateStore.WorkingState,
            sampleRateHz,
            DeviceCapabilities))
        {
            State = _radioStateStore.WorkingState;
        }
    }

    private int GetBufferSampleRateHz() =>
        _tuningCoordinator.ResolveSampleRate(
            new InputSampleRateRequest(
                IsPlaying,
                _audioService.PlaybackSampleRateHz,
                Control.FsHz,
                SdrDevice?.FsHz ?? 0));

    public int GetMaxAvailableHistorySec()
    {
        int fsHz = GetBufferSampleRateHz();
        return _signalPipeline.GetMaxAvailableHistorySeconds(fsHz);
    }

    public int ClampHistorySec(int historySec) => Math.Clamp(historySec, 0, GetMaxAvailableHistorySec());
    public void InitializeDSP()
    {
        EnsureIqBufferCapacity();
    }

    public void WarmUpForSdrStart()
    {
        RadioControl control = Control;
        if (control.FsHz <= 0) return;
        if (_mainFftService.Processor is FftProcessor processor &&
            processor.IsPrepared(control))
        {
            return;
        }

        float[] spectrum = new float[AppConstants.FFT_SIZE];
        float[] waterfall = new float[AppConstants.FFT_SIZE];
        float[] waterfallAverage = new float[AppConstants.FFT_SIZE];
        float[] fullResolution = new float[AppConstants.FFT_SIZE];
        float[] noiseFloor = new float[AppConstants.FFT_SIZE];
        _mainFftService.Processor.ProcessFft(
            IqBuffer,
            0,
            control,
            RequestedSpectrumWidth,
            ref spectrum,
            ref waterfall,
            ref waterfallAverage,
            ref fullResolution,
            ref noiseFloor);
    }
    public bool _rfAgc;
    public Dispatcher Dispatcher => Application.Current.Dispatcher;
    public bool HasNewRenderData { get; set; } = false;
    public bool HasNewDemodRenderData { get; set; } = false;
    private volatile bool _hasValidMainFftData;
    public bool HasValidMainFftData { get => _hasValidMainFftData; set => _hasValidMainFftData = value; }
    public int RenderFrameSerial { get; set; } = 0;
    public int SdrCenterFreqHz
    {
        get
        {
            int referenceCenterFrequencyHz = Volatile.Read(ref RfHzOld);
            return referenceCenterFrequencyHz > 0
                ? referenceCenterFrequencyHz
                : Control.CenterFreqHz;
        }
    }
    public int MainFftCenterFreqHz => (IsSdrRunning || IsPlaying) && _mainFftService.CenterFrequencyHz > 0
        ? _mainFftService.CenterFrequencyHz
        : Control.CenterFreqHz;
    public long WaterfallBlockSequence { get => _mainFftService.WaterfallBlockSequence; set => _mainFftService.WaterfallBlockSequence = value; }
    public bool NeedsBackgroundRedraw { get; set; } = false;
    public int SpectrumBiasAdj { get; set; } = 0;
    public int WaterfallBiasAdj { get; set; } = 0;
    public int SpectrumZoomBiasAdj { get; set; } = 0;
    public int WaterfallZoomBiasAdj { get; set; } = 0;
    public float PpmAdjustment { get; set; } = 0f;
    public float RfCalibrationOffset { get; set; } = AppConstants.RF_CAL_OFFSET;
    public float SystemGainOffset { get; set; } = 0.0f;
    public float SdrBiasPpm { get; set; } = AppConstants.DEFAULT_SDR_BIAS_PPM;
    public int MaxGainReduction => SdrDevice?.MaxGainReduction ?? 59;
    public int MinGainReduction { get; set; } = AppConstants.DEFAULT_MIN_GAIN_REDUCTION;
    public int RfAgcEnabled { get; set; } = 0;
    public AgcReleaseMode AgcReleaseMode
    {
        get => _agcManager.ReleaseMode;
        set => _agcManager.ReleaseMode = value;
    }

    public CoreEngine(
        IAudioService audioService,
        IInputSessionStateMachine inputSessionState,
        ISignalPipelineFactory signalPipelineFactory,
        IAgcManagerFactory agcManagerFactory,
        IMainFftServiceFactory mainFftServiceFactory,
        IRadioDiagnosticsStore diagnosticsStore,
        IRadioProcessingPipeline processingPipeline,
        IPluginIqDispatcher pluginIqDispatcher,
        ITuningCoordinator tuningCoordinator,
        ISdrDeviceManagerFactory sdrDeviceManagerFactory,
        IRadioStateStore radioStateStore,
        IRadioControlStore radioControlStore)
    {
        _audioService = audioService;
        _inputSessionState = inputSessionState;
        _diagnosticsStore = diagnosticsStore;
        _processingPipeline = processingPipeline;
        _pluginIqDispatcher = pluginIqDispatcher;
        _tuningCoordinator = tuningCoordinator;
        _agcManager = agcManagerFactory.Create(ApplyGainUpdate);
        _sdrDeviceManager = sdrDeviceManagerFactory.Create(
            ProcessIncomingSamples,
            HandleGainHardwareChanged,
            HandleDeviceRemoved,
            HandleStreamStalled);
        _signalPipeline = signalPipelineFactory.Create(
            HandleCompletedSignalBlock,
            ProcessSignalCycle,
            SyncPlaybackFrequencyToControl,
            value => SystemDb = value,
            _agcManager);
        _radioStateStore = radioStateStore;
        _radioControlStore = radioControlStore;
        _mainFftService = mainFftServiceFactory.Create(SyncMainFftCompleted);
        WeakReferenceMessenger.Default.Register<RadioControlUpdateMessage>(this, (recipient, message) =>
        {
            RadioControl appliedControl = _radioControlStore.Update(
                _ => ConstrainDeviceFrequency(message.NewControl));
            if (message.ApplyFrequencyImmediately)
            {
                SynchronizeTuning(appliedControl, synchronizeSdrProperties: true);
            }
            else
            {
                lock (_tuningSynchronizationLock)
                {
                    SyncSdrProperties(appliedControl);
                }
            }
            NeedsBackgroundRedraw = true;
        });
        State = new RadioState { AveRxPwr = AppConstants.MIN_RSSI_DB * 1.5f, AveDb = AppConstants.MIN_RSSI_DB, MinFftPwr = AppConstants.MIN_RSSI_DB };
        _mainFftService.Start();
        _signalPipeline.Start();
    }

    private RadioControl ConstrainDeviceFrequency(RadioControl control) =>
        SdrDevicePolicy.ConstrainControl(control, DeviceCapabilities, SdrDevice?.FsHz ?? 0);

    private void HandleDeviceRemoved()
    {
        if (_isDisposed) return;

        // Keep the session marked as ReceivingSdr until the UI-driven
        // RadioSessionController stop runs. Changing it here would make the
        // transition coordinator skip the native device cleanup.
        DeviceRemoved?.Invoke();
    }

    private void HandleStreamStalled()
    {
        if (_isDisposed) return;

        // The UI recovery path performs StopAsync followed by StartSdrAsync.
        // Preserve ReceivingSdr so StopAsync actually calls SdrDevice.Stop().
        StreamStalled?.Invoke();
    }

    private int GetInputCenterFrequency(RadioControl control) =>
        _tuningCoordinator.ResolveInputCenterFrequency(
            new InputCenterFrequencyRequest(
                control,
                Volatile.Read(ref RfHzOld),
                SdrDevice?.CenterFreqHz ?? 0));

    private void SyncMainDiagnostics(RadioDiagnostics source, double timeProcCycle, double? forceTimeTotal = null)
        => _diagnosticsStore.UpdateMain(source, timeProcCycle, forceTimeTotal);

    public void ResetPointersForRestart()
    {
        _signalPipeline.ResetForRestart();
        _pluginIqDispatcher.ResetStream();
        _lastMainFftTriggerSample = TotalSamplesReceived;
        _mainFftService.ResetMetrics();
        HasValidMainFftData = false;
        RenderFrameSerial = 0;
        WaterfallBlockSequence = 0;
        HasNewDemodRenderData = false;
    }
    public void DrawSpectrumInit() => NeedsBackgroundRedraw = true;
    public void DrawWaterfallInit() => NeedsBackgroundRedraw = true;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _processingPipeline.StopAndWait();
        ISdrDevice? sdrDevice = _sdrDeviceManager.DetachDevice();
        _inputSessionState.MarkDisposed();
        _agcManager.Dispose();
        sdrDevice?.Dispose();
        
        _signalPipeline.Dispose();
        _mainFftService.Dispose();
        _pluginIqDispatcher.Dispose();

        _audioService.Shutdown();
        GC.SuppressFinalize(this);
    }

    private int GetGridIndex(int pointer)
    {
        // 0.1秒分を1グリッドとし、現在のサンプリングレートに追従させる。
        int samplesPerGrid = Math.Max(1, GetBufferSampleRateHz() / 10);
        return _signalPipeline.GetGridIndex(pointer, samplesPerGrid);
    }
}
