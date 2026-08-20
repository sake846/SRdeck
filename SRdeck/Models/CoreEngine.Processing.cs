using System;
using System.Diagnostics;
using SRdeck.DSP;
using SRdeck.Models;
using SRdeck.Models.SDR;
using SRdeck.Services;
using SRdeck.Services.Plugins;

namespace SRdeck.Models;

public partial class CoreEngine
{
    public void ProcessSignalCycle()
    {
        if (_isDisposed) return;
        _processingPipeline.TryRun(
            () => !_isDisposed && (IsPlaying || IsSdrRunning),
            ExecuteSignalProcessingCycle);
    }

    private void ExecuteSignalProcessingCycle()
    {
        var totalStopwatch = Stopwatch.StartNew();
        var diagnostics = Diagnostics;
        CycleControlContext cycleControl = PrepareCycleControl(ref diagnostics);
        RadioControl control = cycleControl.Control;
        int inputCenterFreqHz = cycleControl.InputCenterFreqHz;
        _pluginIqDispatcher.TryPublish(new PluginIqPublishRequest(
            IqBuffer,
            BufferRPtrNow,
            Math.Max(1, control.FsHz / 10),
            control.FsHz,
            inputCenterFreqHz,
            CurrentReadAbsoluteSampleEnd,
            IsPlaying ? SignalInputSource.Playback : SignalInputSource.Sdr));
        // FFT の非同期化処理 — Rx1/Rx2 復調と並列実行するため、Demod の前に開始する
        // IQリングは読み取り専用のため、Demod との並行読み出しでデータ競合は発生しない
        bool fftTriggered = TrySubmitMainFft(control, inputCenterFreqHz, totalStopwatch);
        var radioState = _radioStateStore.WorkingState;
        bool demodHistoryUpdated = ProcessDemodulationCycle();

        SyncRxStatistics(ref radioState, CreateInputCenteredControl(control, inputCenterFreqHz));
        radioState.MainFftData = _mainFftService.FullResolutionData; // 前回値または最新値をセット
        SyncCycleDiagnostics(ref diagnostics);
        FinalizeSignalProcessingCycle(diagnostics, totalStopwatch, fftTriggered, demodHistoryUpdated);
    }

    private bool ProcessDemodulationCycle()
    {
        HasNewDemodRenderData = true;
        return true;
    }

    private void FinalizeSignalProcessingCycle(
        RadioDiagnostics diagnostics,
        Stopwatch totalStopwatch,
        bool fftTriggered,
        bool demodHistoryUpdated)
    {
        double processingCycleElapsedMs = totalStopwatch.Elapsed.TotalMilliseconds;
        double? forceTimeTotal = fftTriggered ? null : processingCycleElapsedMs;
        SyncMainDiagnostics(diagnostics, processingCycleElapsedMs, forceTimeTotal);

        const float errorExponentialMovingAverageAlpha = 0.001f;
        _radioStateStore.PublishProcessingState(errorExponentialMovingAverageAlpha);
        if (demodHistoryUpdated)
        {
            DemodHistoryUpdated?.Invoke();
        }
    }

    private readonly record struct CycleControlContext(RadioControl Control, int InputCenterFreqHz);

    private CycleControlContext PrepareCycleControl(ref RadioDiagnostics diagnostics)
    {
        int playbackSampleRateHz = IsPlaying ? _audioService.PlaybackSampleRateHz : 0;
        RadioControl control = _radioControlStore.CreateProcessingSnapshot(
            playbackSampleRateHz,
            GetMaxAvailableHistorySec());

        SyncParametersWithUi(ref control, ref diagnostics, BufferRPtrNext);
        int configuredInputCenterFreqHz = GetInputCenterFrequency(control);
        int inputCenterFreqHz = IsPlaying
            ? configuredInputCenterFreqHz
            : SdrDevicePolicy.ResolveActiveInputCenterFrequency(
                _sdrDeviceManager.ActiveCenterFrequencyHz,
                configuredInputCenterFreqHz);

        _radioControlStore.CommitProcessingValues(control);

        return new CycleControlContext(control, inputCenterFreqHz);
    }

    private static RadioControl CreateInputCenteredControl(RadioControl control, int inputCenterFreqHz)
    {
        control.CenterFreqHz = inputCenterFreqHz;
        control.FreqOffsetHz = control.TunedFreqHz - inputCenterFreqHz;
        return control;
    }

    private bool TrySubmitMainFft(RadioControl control, int inputCenterFreqHz, Stopwatch cycleStopwatch)
    {
        if (!_mainFftService.TrySubmit(new MainFftSubmission(
            IqBuffer,
            BufferRPtrNext,
            control,
            RequestedSpectrumWidth,
            _signalPipeline.InputBlockSequence,
            Stopwatch.GetTimestamp() - cycleStopwatch.ElapsedTicks,
            inputCenterFreqHz))) return false;

        _lastMainFftTriggerSample = TotalSamplesReceived;
        return true;
    }

    private void SyncCycleDiagnostics(ref RadioDiagnostics diagnostics)
    {
        SdrStreamingDiagnosticsSnapshot? streamingSnapshot = null;
        if (SdrDevice is ISdrStreamingDiagnostics streamingDiagnosticsDevice)
        {
            streamingSnapshot = new SdrStreamingDiagnosticsSnapshot(
                streamingDiagnosticsDevice.QueuedSampleBlockCount,
                streamingDiagnosticsDevice.CallbackCount,
                streamingDiagnosticsDevice.DroppedCallbackCount,
                streamingDiagnosticsDevice.LastCallbackAgeSeconds);
        }

        _diagnosticsStore.ApplyProcessingCycle(
            ref diagnostics,
            new ProcessingCycleDiagnosticsSnapshot(
                _audioService.BufferedBytes,
                BufferWPtr,
                BufferRPtr,
                BufferSize,
                streamingSnapshot));
    }

    private void SyncMainFftCompleted()
    {
        unchecked { RenderFrameSerial++; }
        HasValidMainFftData = true;
        HasNewRenderData = true;
        StateUpdated?.Invoke();
    }

    private void SyncRxStatistics(ref RadioState radioState, RadioControl control)
    {
        // The FFT worker runs asynchronously.  Until its first completion the
        // service buffers contain only their zero-filled construction values,
        // which must not be folded into the noise-floor EMA.
        if (!HasValidMainFftData) return;

        SpectrumStatisticsCalculator.Update(
            ref radioState,
            _mainFftService.SpectrumData,
            _mainFftService.NoiseFloorData,
            control,
            new SpectrumStatisticsOptions(
                SdrDevice?.FsHz ?? (int)AppConstants.FULL_BW,
                RequestedSpectrumWidth,
                RfCalibrationOffset));
    }
}
