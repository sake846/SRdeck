using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SRdeck.DSP;

namespace SRdeck.Models.SDR;

public sealed class MainFftRequest
{
    public required IqSampleRingBuffer Buffer { get; init; }
    public int ReferencePtr { get; init; }
    public RadioControl Control { get; init; }
    public int RequestedWidth { get; init; }
    public required float[] SpectrumFftData { get; init; }
    public required float[] WaterfallFftData { get; init; }
    public required float[] WaterfallAveragingBuffer { get; init; }
    public required float[] FullResFftData { get; init; }
    public required float[] NoiseFloorFftData { get; init; }
    public long WaterfallBlockSequence { get; init; }
    public long CycleStartTicks { get; init; }
    public int InputCenterFreqHz { get; init; }

    public long RequestId { get; set; }
}

public readonly record struct MainFftMetrics(
    long RequestedCount,
    long CompletedCount,
    long DroppedCount,
    long LatestRequestId,
    long LatestCompletedId,
    int QueueDepth);

public readonly record struct MainFftTiming(
    double ElapsedMs,
    double TotalMs,
    double OsLagMs,
    double CpuPrep,
    double CpuPost,
    double GpuPrep,
    double GpuUpload,
    double GpuShader,
    double GpuDownload,
    double GpuPost,
    double FftCore,

    double FullResCopy,
    double Aggregate,
    double GpuPack,
    double GpuUploadNative,
    double GpuDispatch,
    double GpuReadback);

public sealed record MainFftResult(
    float[] SpectrumFftData,
    float[] WaterfallFftData,
    float[] WaterfallAveragingBuffer,
    float[] FullResFftData,
    float[] NoiseFloorFftData,
    int CenterFrequencyHz,
    long WaterfallBlockSequence,
    MainFftTiming Timing);

public interface IMainFftWorker : IDisposable
{
    IFftProcessor Processor { get; }
    void Start();
    bool TrySubmit(MainFftRequest request);
    MainFftMetrics GetMetrics();
    void ResetMetrics();
}

public interface IMainFftWorkerFactory
{
    IMainFftWorker Create(Action<MainFftResult> onCompleted);
}

public sealed class MainFftWorkerFactory : IMainFftWorkerFactory
{
    private readonly IFftProcessorFactory _fftProcessorFactory;

    public MainFftWorkerFactory(IFftProcessorFactory fftProcessorFactory)
    {
        _fftProcessorFactory = fftProcessorFactory;
    }

    public IMainFftWorker Create(Action<MainFftResult> onCompleted)
    {
        return new MainFftWorker(_fftProcessorFactory.Create(), onCompleted);
    }
}

internal sealed class MainFftWorker : IMainFftWorker
{
    private static readonly AsyncLocal<MainFftWorker?> ExecutingWorker = new();
    private readonly IFftProcessor _processor;
    private readonly Action<MainFftResult> _onCompleted;
    private readonly SemaphoreSlim _requestSignal = new(0, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _sync = new();
    private Task? _workerTask;
    private MainFftRequest? _pendingRequest;
    private bool _isBusy;
    private volatile bool _isRunning;
    private bool _isDisposed;
    private int _resourcesDisposed;
    private long _nextRequestId;
    private long _requestedCount;
    private long _completedCount;
    private long _droppedCount;
    private long _latestCompletedId;

    public MainFftWorker(IFftProcessor processor, Action<MainFftResult> onCompleted)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _onCompleted = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
    }

    public IFftProcessor Processor => _processor;

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_isRunning) return;
            _isRunning = true;
            _workerTask = Task.Factory.StartNew(
                RunAsync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }
    }

    public bool TrySubmit(MainFftRequest request)
    {
        lock (_sync)
        {
            if (!_isRunning || _isBusy)
            {
                _droppedCount++;
                return false;
            }

            request.RequestId = ++_nextRequestId;
            _requestedCount++;
            _isBusy = true;
            _pendingRequest = request;
            _requestSignal.Release();
            return true;
        }
    }

    public MainFftMetrics GetMetrics()
    {
        lock (_sync)
        {
            return new MainFftMetrics(
                _requestedCount,
                _completedCount,
                _droppedCount,
                _nextRequestId,
                _latestCompletedId,
                _pendingRequest != null ? 1 : 0);
        }
    }

    public void ResetMetrics()
    {
        lock (_sync)
        {
            _nextRequestId = 0;
            _requestedCount = 0;
            _completedCount = 0;
            _droppedCount = 0;
            _latestCompletedId = 0;
        }
    }

    private async Task RunAsync()
    {
        ExecutingWorker.Value = this;
        try
        {
            while (true)
            {
                await _requestSignal.WaitAsync(_cancellation.Token).ConfigureAwait(false);

                MainFftRequest? request;
                lock (_sync)
                {
                    if (!_isRunning) break;
                    request = _pendingRequest;
                    _pendingRequest = null;
                }

                if (request == null) continue;
                Execute(request);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                if (_isDisposed)
                {
                    DisposeResources();
                }
            }
            finally
            {
                ExecutingWorker.Value = null;
            }
        }
    }

    private void Execute(MainFftRequest request)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            double osLagMs = (Stopwatch.GetTimestamp() - request.CycleStartTicks) * 1000.0 / Stopwatch.Frequency;
            float[] spectrum = request.SpectrumFftData;
            float[] waterfall = request.WaterfallFftData;
            float[] waterfallAverage = request.WaterfallAveragingBuffer;
            float[] fullResolution = request.FullResFftData;
            float[] noiseFloor = request.NoiseFloorFftData;

            _processor.ProcessFft(
                request.Buffer,
                request.ReferencePtr,
                request.Control,
                request.RequestedWidth,
                ref spectrum,
                ref waterfall,
                ref waterfallAverage,
                ref fullResolution,
                ref noiseFloor);

            double elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            double totalMs = (Stopwatch.GetTimestamp() - request.CycleStartTicks) * 1000.0 / Stopwatch.Frequency;
            var timing = new MainFftTiming(
                elapsedMs,
                totalMs,
                osLagMs,
                _processor.LastCpuPrep,
                _processor.LastCpuPost,
                _processor.LastGpuPrep,
                _processor.LastGpuUpload,
                _processor.LastGpuShader,
                _processor.LastGpuDownload,
                _processor.LastGpuPost,
                _processor.LastFftCore,

                _processor.LastFullResCopy,
                _processor.LastAggregate,
                _processor.LastGpuPack,
                _processor.LastGpuUploadNative,
                _processor.LastGpuDispatch,
                _processor.LastGpuReadback);

            lock (_sync)
            {
                _completedCount++;
                _latestCompletedId = request.RequestId;
            }

            int centerFrequencyHz = request.InputCenterFreqHz;
            _onCompleted(new MainFftResult(
                spectrum,
                waterfall,
                waterfallAverage,
                fullResolution,
                noiseFloor,
                centerFrequencyHz,
                request.WaterfallBlockSequence,
                timing));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainFftWorker] FFT request failed: {ex}");
        }
        finally
        {
            lock (_sync)
            {
                _isBusy = false;
            }
        }
    }

    public void Dispose()
    {
        Task? workerTask;
        lock (_sync)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _isRunning = false;
            workerTask = _workerTask;
        }

        _cancellation.Cancel();
        if (workerTask != null && !workerTask.IsCompleted)
        {
            if (ReferenceEquals(ExecutingWorker.Value, this))
            {
                return;
            }

            if (!workerTask.Wait(TimeSpan.FromSeconds(2)))
            {
                Debug.WriteLine("[MainFftWorker] Timed out while stopping; resources will be released when the worker exits.");
                return;
            }
        }

        DisposeResources();
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _requestSignal.Dispose();
        _cancellation.Dispose();
        _processor.Dispose();
    }
}
