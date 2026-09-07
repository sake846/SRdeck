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
        SignalBlockContext inputBlock = _signalPipeline.CurrentReadContext;
        int inputSampleRateHz = inputBlock.SampleRateHz > 0 ? inputBlock.SampleRateHz : control.FsHz;
        _pluginIqDispatcher.TryPublish(new PluginIqPublishRequest(
            IqBuffer,
            BufferRPtrNow,
            Math.Max(1, inputSampleRateHz / 10),
            inputSampleRateHz,
            inputBlock.CenterFrequencyHz != 0 ? inputBlock.CenterFrequencyHz : inputCenterFreqHz,
            CurrentReadAbsoluteSampleEnd,
            inputBlock.SampleRateHz > 0 ? inputBlock.Source :
                IsPlaying ? SignalInputSource.Playback : SignalInputSource.Sdr,
            inputBlock.Discontinuity)
        {
            SamplesI = _signalPipeline.CurrentSamplesI,
            SamplesQ = _signalPipeline.CurrentSamplesQ
        });
        // FFT の非同期化処理 — Rx1/Rx2 復調と並列実行するため、Demod の前に開始する
        // IQリングは読み取り専用のため、Demod との並行読み出しでデータ競合は発生しない
        bool fftTriggered = TrySubmitMainFft(control, inputCenterFreqHz, totalStopwatch);
        var radioState = _radioStateStore.WorkingState;

        SyncRxStatistics(ref radioState, CreateInputCenteredControl(control, inputCenterFreqHz));
        SyncCycleDiagnostics(ref diagnostics);
        FinalizeSignalProcessingCycle(diagnostics, totalStopwatch, fftTriggered);
    }

    private void FinalizeSignalProcessingCycle(
        RadioDiagnostics diagnostics,
        Stopwatch totalStopwatch,
        bool fftTriggered)
    {
        double processingCycleElapsedMs = totalStopwatch.Elapsed.TotalMilliseconds;
        double? forceTimeTotal = fftTriggered ? null : processingCycleElapsedMs;
        SyncMainDiagnostics(diagnostics, processingCycleElapsedMs, forceTimeTotal);

        _radioStateStore.Publish();
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
                streamingDiagnosticsDevice.LastCallbackAgeSeconds,
                streamingDiagnosticsDevice.LastCallbackLengthBytes,
                streamingDiagnosticsDevice.UnexpectedCallbackLengthCount);
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

        using MainFftFrameLease lease = _mainFftService.AcquireFrame();
        MainFftFrame frame = lease.Frame;
        SpectrumStatisticsCalculator.Update(
            ref radioState,
            frame.SpectrumData,
            frame.NoiseFloorData,
            control,
            new SpectrumStatisticsOptions(
                SdrDevice?.FsHz ?? (int)AppConstants.FULL_BW,
                RequestedSpectrumWidth,
                RfCalibrationOffset));
    }
}
