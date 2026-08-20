using System;
using System.Threading.Tasks;
using SRdeck.Configuration;
using SRdeck.DSP;
using SRdeck.Models.SDR;

namespace SRdeck.Models;

public interface IRadioStateContext
{
    RadioControl Control { get; set; }
    RadioState State { get; set; }
    int SdrCenterFreqHz { get; }
    RadioDiagnostics Diagnostics { get; }
    void UpdateDiagnostics(RadioDiagnosticsMutator mutator);
    void ResetDiagnostics();
    AppSettings InitialAppSettings { get; set; }
    InputSessionState SessionState { get; }
    bool IsPlaying { get; }
    bool IsSdrRunning { get; }
}

public interface IRadioDeviceContext
{
    ISdrDevice? SdrDevice { get; set; }
}

public interface IRadioSessionEngine : IRadioStateContext, IRadioDeviceContext
{
    bool TryStartSdrSession();
    bool TryStartPlaybackSession();
    void StopSdrSession();
    void StopPlaybackSession();
    Task ManageAudioBufferAsync();
    void EnsureIqBufferCapacity();
    void ResetPointersForRestart();
    void WarmUpForSdrStart();
}

public interface IRadioRenderContext : IRadioStateContext, IRadioDeviceContext
{
    bool NeedsBackgroundRedraw { get; set; }
    bool HasNewRenderData { get; set; }
    bool HasNewDemodRenderData { get; set; }
    bool HasValidMainFftData { get; set; }
    int RenderFrameSerial { get; set; }
    int MainFftCenterFreqHz { get; }
    long WaterfallBlockSequence { get; set; }
    float[] SpectrumFftData { get; set; }
    float[] WaterfallFftData { get; set; }
    IqSampleRingBuffer IqBuffer { get; }
    IFftProcessor? FftProcessor { get; }
    int LatestBufferPointer { get; }
    float RfCalibrationOffset { get; set; }
    int SpectrumBiasAdj { get; set; }
    int WaterfallBiasAdj { get; set; }
    int SpectrumZoomBiasAdj { get; set; }
    int WaterfallZoomBiasAdj { get; set; }
    void SetZoomHighResolutionMode(int receiverIndex, bool isHighResolution);
}
