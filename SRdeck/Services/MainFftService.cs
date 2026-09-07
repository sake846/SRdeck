using System;
using System.Collections.Generic;
using System.Threading;
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

public sealed record MainFftFrame(
    long FrameId,
    float[] SpectrumData,
    float[] WaterfallData,
    float[] NoiseFloorData,
    int CenterFrequencyHz,
    long WaterfallBlockSequence);

internal sealed class MainFftFrameSlot(MainFftFrame frame)
{
    public MainFftFrame Frame { get; } = frame;
    public int LeaseCount { get; set; }
    public bool IsPublished { get; set; } = true;
    public bool IsRecycled { get; set; }
}

internal interface IMainFftFrameLeaseOwner
{
    void ReleaseFrame(MainFftFrameSlot slot);
}

public readonly ref struct MainFftFrameLease
{
    private readonly IMainFftFrameLeaseOwner? _owner;
    private readonly MainFftFrameSlot? _slot;

    internal MainFftFrameLease(IMainFftFrameLeaseOwner owner, MainFftFrameSlot slot)
    {
        _owner = owner;
        _slot = slot;
        Frame = slot.Frame;
    }

    internal MainFftFrameLease(MainFftFrame frame)
    {
        _owner = null;
        _slot = null;
        Frame = frame;
    }

    public MainFftFrame Frame { get; }

    public void Dispose()
    {
        if (_owner != null && _slot != null)
        {
            _owner.ReleaseFrame(_slot);
        }
    }
}

public interface IMainFftService : IDisposable
{
    IFftProcessor Processor { get; }
    int CenterFrequencyHz { get; }

    void Start();
    void WarmUp(IqSampleRingBuffer buffer, RadioControl control, int requestedWidth);
    bool TrySubmit(MainFftSubmission submission);
    MainFftFrameLease AcquireFrame();
    void ClearFrame();
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

internal sealed class MainFftService : IMainFftService, IMainFftFrameLeaseOwner
{
    private readonly record struct DisplayBuffers(
        float[] Spectrum,
        float[] Waterfall,
        float[] NoiseFloor);

    private readonly record struct PreparationKey(
        int SampleRateHz,
        int MainSpanHz,
        int ResolutionMode,
        int BatchCount,
        bool IsGpuEnabled,
        int RequestedWidth);

    private readonly IMainFftWorker _worker;
    private readonly IRadioDiagnosticsStore _diagnosticsStore;
    private readonly Action _completed;
    private readonly object _frameSync = new();
    private readonly object _completionSync = new();
    private readonly Queue<DisplayBuffers> _availableDisplayBuffers = new();
    private MainFftFrameSlot _publishedSlot;
    private float[] _writeSpectrumData = new float[AppConstants.FFT_SIZE];
    private float[] _writeWaterfallData = new float[AppConstants.FFT_SIZE];
    private float[] _writeNoiseFloorData = new float[AppConstants.FFT_SIZE];
    private float[] _fullResolutionData = new float[AppConstants.FFT_SIZE];
    private float[] _waterfallAveragingBuffer = new float[AppConstants.FFT_SIZE];
    private PreparationKey? _preparedKey;
    private long _generation;
    private bool _requestInFlight;
    private bool _isDisposed;

    public MainFftService(
        IMainFftWorkerFactory workerFactory,
        IRadioDiagnosticsStore diagnosticsStore,
        Action completed)
    {
        _diagnosticsStore = diagnosticsStore;
        _completed = completed ?? throw new ArgumentNullException(nameof(completed));
        _worker = workerFactory.Create(OnCompleted);
        _publishedSlot = new MainFftFrameSlot(CreateEmptyFrame());
    }

    public IFftProcessor Processor => _worker.Processor;
    public int CenterFrequencyHz => Volatile.Read(ref _publishedSlot).Frame.CenterFrequencyHz;

    public void Start() => _worker.Start();

    public void WarmUp(IqSampleRingBuffer buffer, RadioControl control, int requestedWidth)
    {
        var key = new PreparationKey(
            control.FsHz,
            control.MainSpanHz,
            control.FftResolutionMode,
            control.FftBatchCount,
            control.IsGpuFftEnabled,
            requestedWidth);
        lock (_frameSync)
        {
            bool processorPrepared = _worker.Processor is not FftProcessor processor ||
                                     processor.IsPrepared(control);
            if (_preparedKey == key && processorPrepared) return;

            float[] spectrum = _writeSpectrumData;
            float[] waterfall = _writeWaterfallData;
            float[] waterfallAverage = _waterfallAveragingBuffer;
            float[] fullResolution = _fullResolutionData;
            float[] noiseFloor = _writeNoiseFloorData;
            try
            {
                _worker.Processor.ProcessFft(
                    buffer,
                    0,
                    control,
                    requestedWidth,
                    0,
                    out _,
                    out _,
                    ref spectrum,
                    ref waterfall,
                    ref waterfallAverage,
                    ref fullResolution,
                    ref noiseFloor);
                _preparedKey = key;
            }
            finally
            {
                _writeSpectrumData = spectrum;
                _writeWaterfallData = waterfall;
                _waterfallAveragingBuffer = waterfallAverage;
                _fullResolutionData = fullResolution;
                _writeNoiseFloorData = noiseFloor;
            }
        }
    }

    public bool TrySubmit(MainFftSubmission submission)
    {
        lock (_frameSync)
        {
            bool accepted = _worker.TrySubmit(new MainFftRequest
            {
                Buffer = submission.Buffer,
                ReferencePtr = submission.ReferencePointer,
                Control = submission.Control,
                RequestedWidth = submission.RequestedWidth,
                SpectrumFftData = _writeSpectrumData,
                WaterfallFftData = _writeWaterfallData,
                WaterfallAveragingBuffer = _waterfallAveragingBuffer,
                FullResFftData = _fullResolutionData,
                NoiseFloorFftData = _writeNoiseFloorData,
                WaterfallBlockSequence = submission.InputBlockSequence,
                CycleStartTicks = submission.CycleStartTicks,
                InputCenterFreqHz = submission.InputCenterFrequencyHz,
                Generation = _generation
            });
            if (accepted) _requestInFlight = true;
            return accepted;
        }
    }

    public MainFftFrameLease AcquireFrame()
    {
        lock (_frameSync)
        {
            MainFftFrameSlot slot = _publishedSlot;
            slot.LeaseCount++;
            return new MainFftFrameLease(this, slot);
        }
    }

    public void ClearFrame()
    {
        lock (_completionSync)
        {
            lock (_frameSync)
            {
                _generation++;
                MainFftFrameSlot previous = _publishedSlot;
                previous.IsPublished = false;
                DisplayBuffers buffers = TakeReusableBuffersLocked(previous);
                Array.Clear(buffers.Spectrum);
                Array.Clear(buffers.Waterfall);
                Array.Clear(buffers.NoiseFloor);
                _publishedSlot = new MainFftFrameSlot(new MainFftFrame(
                    0,
                    buffers.Spectrum,
                    buffers.Waterfall,
                    buffers.NoiseFloor,
                    0,
                    0));
            }
        }
    }

    public void ResetMetrics() => _worker.ResetMetrics();

    public void Dispose()
    {
        lock (_frameSync)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }
        _worker.Dispose();
    }

    private void OnCompleted(MainFftResult result)
    {
        bool published = false;
        bool currentGeneration;
        lock (_frameSync)
        {
            _requestInFlight = false;
            _waterfallAveragingBuffer = result.WaterfallAveragingBuffer;
            _fullResolutionData = result.FullResFftData;
            currentGeneration = result.Generation == _generation;

            if (result.HasFrame && currentGeneration && !_isDisposed)
            {
                MainFftFrameSlot previous = _publishedSlot;
                previous.IsPublished = false;
                _publishedSlot = new MainFftFrameSlot(new MainFftFrame(
                    result.RequestId,
                    result.SpectrumFftData,
                    result.WaterfallFftData,
                    result.NoiseFloorFftData,
                    result.CenterFrequencyHz,
                    result.WaterfallBlockSequence));
                SetWriteBuffersLocked(TakeReusableBuffersLocked(previous));
                published = true;
            }
            else
            {
                SetWriteBuffersLocked(new DisplayBuffers(
                    result.SpectrumFftData,
                    result.WaterfallFftData,
                    result.NoiseFloorFftData));
            }
        }

        if (published)
        {
            lock (_completionSync)
            {
                lock (_frameSync)
                {
                    published = result.Generation == _generation &&
                                _publishedSlot.Frame.FrameId == result.RequestId;
                }
                if (published)
                {
                    _completed();
                    _diagnosticsStore.UpdateFft(result.Timing, _worker.GetMetrics());
                }
            }
        }
    }

    void IMainFftFrameLeaseOwner.ReleaseFrame(MainFftFrameSlot slot)
    {
        lock (_frameSync)
        {
            if (slot.LeaseCount <= 0) return;
            slot.LeaseCount--;
            if (slot.LeaseCount != 0 || slot.IsPublished || slot.IsRecycled || _isDisposed) return;

            slot.IsRecycled = true;
            DisplayBuffers released = GetBuffers(slot.Frame);
            if (!_requestInFlight)
            {
                DisplayBuffers currentWrite = new(
                    _writeSpectrumData,
                    _writeWaterfallData,
                    _writeNoiseFloorData);
                SetWriteBuffersLocked(released);
                _availableDisplayBuffers.Enqueue(currentWrite);
            }
            else
            {
                _availableDisplayBuffers.Enqueue(released);
            }
        }
    }

    private DisplayBuffers TakeReusableBuffersLocked(MainFftFrameSlot previous)
    {
        if (previous.LeaseCount == 0 && !previous.IsRecycled)
        {
            previous.IsRecycled = true;
            return GetBuffers(previous.Frame);
        }

        if (_availableDisplayBuffers.Count > 0)
        {
            return _availableDisplayBuffers.Dequeue();
        }

        return new DisplayBuffers(
            new float[previous.Frame.SpectrumData.Length],
            new float[previous.Frame.WaterfallData.Length],
            new float[previous.Frame.NoiseFloorData.Length]);
    }

    private void SetWriteBuffersLocked(DisplayBuffers buffers)
    {
        _writeSpectrumData = buffers.Spectrum;
        _writeWaterfallData = buffers.Waterfall;
        _writeNoiseFloorData = buffers.NoiseFloor;
    }

    private static DisplayBuffers GetBuffers(MainFftFrame frame) => new(
        frame.SpectrumData,
        frame.WaterfallData,
        frame.NoiseFloorData);

    private static MainFftFrame CreateEmptyFrame() => new(
        0,
        new float[AppConstants.FFT_SIZE],
        new float[AppConstants.FFT_SIZE],
        new float[AppConstants.FFT_SIZE],
        0,
        0);
}
