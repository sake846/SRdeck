using System;
using SRdeck.Models;
using SRdeck.Models.SDR;
using SRdeck.Services;
using SRdeckPlugin.Contracts;

namespace SRdeck.Models;

public partial class CoreEngine
{
    public void ProcessIncomingSamples(short[] bufferI, short[] bufferQ, uint sampleCount)
    {
        StoreSignalSamples(bufferI, bufferQ, sampleCount, SignalInputSource.Sdr);
    }

    private void ProcessIncomingSampleBlock(SdrSampleBlock block) =>
        StoreSignalSamples(block.SamplesI, block.SamplesQ, block.SampleCount, SignalInputSource.Sdr,
            discontinuity: block.Metadata.IsDiscontinuous
                ? IqDiscontinuity.SamplesDropped : IqDiscontinuity.None);

    private void ProcessPlaybackInputSamples(short[] samplesI, short[] samplesQ, int sampleCount, double systemDb, int rfFrequencyHz)
    {
        StoreSignalSamples(samplesI, samplesQ, (uint)Math.Max(0, sampleCount), SignalInputSource.Playback, systemDb, rfFrequencyHz);
    }

    private void StoreSignalSamples(
        short[] samplesI,
        short[] samplesQ,
        uint sampleCount,
        SignalInputSource source,
        double playbackSystemDb = 0.0,
        int playbackRfFrequencyHz = 0,
        IqDiscontinuity discontinuity = IqDiscontinuity.None)
    {
        int validSampleCount = (int)Math.Min(sampleCount, (uint)Math.Min(samplesI.Length, samplesQ.Length));

        if (source == SignalInputSource.Sdr)
        {
            _sdrDeviceManager.AdvanceFrequencyTransition(validSampleCount);
        }

        int sampleRateHz = GetBufferSampleRateHz();
        int centerFrequencyHz = source == SignalInputSource.Playback
            ? playbackRfFrequencyHz
            : SdrDevicePolicy.ResolveActiveInputCenterFrequency(
                _sdrDeviceManager.ActiveCenterFrequencyHz, GetInputCenterFrequency(Control));
        _signalPipeline.Write(
            samplesI,
            samplesQ,
            validSampleCount,
            new SignalBlockContext(source, sampleRateHz, playbackSystemDb, playbackRfFrequencyHz,
                discontinuity, centerFrequencyHz));
    }

    private void HandleCompletedSignalBlock(int blockEndPointer, SignalBlockContext context)
    {
        RadioControl control = Control;
        int playbackFallbackRfFrequencyHz = context.Source == SignalInputSource.Playback
            ? GetInputCenterFrequency(control)
            : 0;
        SignalRuntimeState signalRuntime = Volatile.Read(ref _signalRuntimeState);
        var runtimeSnapshot = new SignalBlockRuntimeSnapshot(
            signalRuntime.SystemDb,
            signalRuntime.SystemGainOffset,
            playbackFallbackRfFrequencyHz,
            _sdrDeviceManager.ActiveCenterFrequencyHz,
            signalRuntime.RfAgcEnabled == 1,
            DeviceCapabilities,
            MinGainReduction,
            MaxGainReduction);
        SystemDb = _signalPipeline.Complete(new SignalBlockCompletionRequest(
            blockEndPointer,
            context,
            runtimeSnapshot));
    }

    public void ApplyFrequencyUpdate()
    {
        ApplyFrequencyUpdate(Control);
    }

    private void ApplyFrequencyUpdate(RadioControl control)
    {
        ResetResidualDcRemoval();
        SyncSdrProperties(control);
        SdrDevice?.FreqChange();
    }

    public void ApplyGainUpdate()
    {
        ResetResidualDcRemoval();
        SyncSdrProperties();
        SdrDevice?.GainChange();
    }

    private void SyncParametersWithUi(ref RadioControl control, ref RadioDiagnostics diagnostics, int referencePointer)
    {
        SynchronizeTuning(control, synchronizeSdrProperties: false);
        int gridIndex = GetGridIndex(referencePointer);
        control.SystemDb = BufferGains[gridIndex];
        control.AdjustmentPpm = PpmAdjustment;
        IqSampleExtrema extrema = _signalPipeline.LastCompletedExtrema;
        _diagnosticsStore.ApplySignalInput(
            ref diagnostics,
            new SignalInputDiagnosticsSnapshot(
                CurrentGainDb,
                extrema,
                State.RxRssi,
                _signalPipeline.EffectiveSampleRateHz));
    }

    private void SynchronizeTuning(RadioControl control, bool synchronizeSdrProperties)
    {
        // Plugin requests arrive through the messenger while the processing worker
        // evaluates the same state once per cycle. Serialize both paths so a later
        // display-only zoom cannot restore the previous hardware center.
        lock (_tuningSynchronizationLock)
        {
            int previousCenterFrequencyHz = Volatile.Read(ref _referenceCenterFrequencyHz);
            TuningSynchronizationResult tuningResult = _tuningCoordinator.Evaluate(
                new TuningSynchronizationRequest(
                    control,
                    DeviceCapabilities,
                    SdrDevice?.FsHz ?? 0,
                    previousCenterFrequencyHz,
                    IsPlaying,
                    RfCalibrationOffset));
            RfCalibrationOffset = tuningResult.CalibrationOffset;
            if (tuningResult.NeedsBackgroundRedraw)
            {
                NeedsBackgroundRedraw = true;
                if (tuningResult.RequiresHardwareFrequencyUpdate)
                {
                    ApplyFrequencyUpdate(control);
                    synchronizeSdrProperties = false;
                }
            }

            if (synchronizeSdrProperties)
            {
                SyncSdrProperties(control);
            }

            Volatile.Write(ref _referenceCenterFrequencyHz, tuningResult.ReferenceCenterFrequencyHz);
        }
    }

}
