using System;
using SRdeck.DSP;
using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.Services;

public sealed record MainFftSubmission(
    IqSampleRingBuffer Buffer,
    int ReferencePointer,
    RadioControl Control,
    int RequestedWidth,
    long InputBlockSequence,
    long CycleStartTicks,
    int InputCenterFrequencyHz);

public interface IMainFftService : IDisposable
{
    float[] SpectrumData { get; set; }
    float[] WaterfallData { get; set; }
    float[] FullResolutionData { get; }
    float[] NoiseFloorData { get; }
    IFftProcessor Processor { get; }
    int CenterFrequencyHz { get; }
    long WaterfallBlockSequence { get; set; }

    void Start();
    bool TrySubmit(MainFftSubmission submission);
    void ResetMetrics();
}

public interface IMainFftServiceFactory
{
    IMainFftService Create(Action completed);
}

public sealed class MainFftServiceFactory : IMainFftServiceFactory
{
    private readonly IMainFftWorkerFactory _workerFactory;
    private readonly IRadioDiagnosticsStore _diagnosticsStore;

    public MainFftServiceFactory(
        IMainFftWorkerFactory workerFactory,
        IRadioDiagnosticsStore diagnosticsStore)
    {
        _workerFactory = workerFactory;
        _diagnosticsStore = diagnosticsStore;
    }

    public IMainFftService Create(Action completed) =>
        new MainFftService(_workerFactory, _diagnosticsStore, completed);
}

internal sealed class MainFftService : IMainFftService
{
    private readonly IMainFftWorker _worker;
    private readonly IRadioDiagnosticsStore _diagnosticsStore;
    private readonly Action _completed;
    private float[] _waterfallAveragingBuffer = new float[AppConstants.FFT_SIZE];

    public MainFftService(
        IMainFftWorkerFactory workerFactory,
        IRadioDiagnosticsStore diagnosticsStore,
        Action completed)
    {
        _diagnosticsStore = diagnosticsStore;
        _completed = completed ?? throw new ArgumentNullException(nameof(completed));
        _worker = workerFactory.Create(OnCompleted);
    }

    public float[] SpectrumData { get; set; } = new float[AppConstants.FFT_SIZE];
    public float[] WaterfallData { get; set; } = new float[AppConstants.FFT_SIZE];
    public float[] FullResolutionData { get; private set; } = new float[AppConstants.FFT_SIZE];
    public float[] NoiseFloorData { get; private set; } = new float[AppConstants.FFT_SIZE];
    public IFftProcessor Processor => _worker.Processor;
    public int CenterFrequencyHz { get; private set; }
    public long WaterfallBlockSequence { get; set; }

    public void Start() => _worker.Start();

    public bool TrySubmit(MainFftSubmission submission)
    {
        return _worker.TrySubmit(new MainFftRequest
        {
            Buffer = submission.Buffer,
            ReferencePtr = submission.ReferencePointer,
            Control = submission.Control,
            RequestedWidth = submission.RequestedWidth,
            SpectrumFftData = SpectrumData,
            WaterfallFftData = WaterfallData,
            WaterfallAveragingBuffer = _waterfallAveragingBuffer,
            FullResFftData = FullResolutionData,
            NoiseFloorFftData = NoiseFloorData,
            WaterfallBlockSequence = submission.InputBlockSequence,
            CycleStartTicks = submission.CycleStartTicks,
            InputCenterFreqHz = submission.InputCenterFrequencyHz
        });
    }

    public void ResetMetrics() => _worker.ResetMetrics();

    public void Dispose() => _worker.Dispose();

    private void OnCompleted(MainFftResult result)
    {
        SpectrumData = result.SpectrumFftData;
        WaterfallData = result.WaterfallFftData;
        _waterfallAveragingBuffer = result.WaterfallAveragingBuffer;
        FullResolutionData = result.FullResFftData;
        NoiseFloorData = result.NoiseFloorFftData;
        CenterFrequencyHz = result.CenterFrequencyHz;
        WaterfallBlockSequence = result.WaterfallBlockSequence;

        _completed();
        _diagnosticsStore.UpdateFft(result.Timing, _worker.GetMetrics());
    }
}
