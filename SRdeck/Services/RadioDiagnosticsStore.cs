using System.Diagnostics;
using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.Services;

public interface IRadioDiagnosticsStore : IDisposable
{
    RadioDiagnostics Snapshot { get; }
    void Update(RadioDiagnosticsMutator mutator);
    void Reset();
    void UpdateMain(RadioDiagnostics source, double timeProcCycle, double? forceTimeTotal);
    void UpdateFft(MainFftTiming timing, MainFftMetrics metrics);
    void ApplySignalInput(
        ref RadioDiagnostics diagnostics,
        SignalInputDiagnosticsSnapshot snapshot);
    void ApplyProcessingCycle(
        ref RadioDiagnostics diagnostics,
        ProcessingCycleDiagnosticsSnapshot snapshot);
}

public sealed class RadioDiagnosticsStore : IRadioDiagnosticsStore
{
    private sealed record SystemUsageSnapshot(
        GpuUsageSnapshot Gpu,
        CpuUsageSnapshot Cpu);

    private readonly object _sync = new();
    private readonly IGpuUsageMonitor _gpuUsageMonitor;
    private readonly ICpuUsageMonitor _cpuUsageMonitor;
    private readonly IRadioDiagnosticsCollector _collector;
    private readonly TimeSpan _usageSampleInterval;
    private readonly TimeSpan _shutdownWaitTimeout;
    private readonly CancellationTokenSource _usageSamplerCancellation = new();
    private readonly Task _usageSamplerTask;
    private SystemUsageSnapshot _usageSnapshot = new(default, default);
    private int _disposeStarted;
    private int _resourcesDisposed;
    private RadioDiagnostics _snapshot;
    private long _fftFpsWindowStartTicks = Stopwatch.GetTimestamp();
    private int _fftFrameCount;
    private double _fftFps;

    public RadioDiagnosticsStore(
        IGpuUsageMonitor gpuUsageMonitor,
        ICpuUsageMonitor cpuUsageMonitor,
        IRadioDiagnosticsCollector collector)
        : this(gpuUsageMonitor, cpuUsageMonitor, collector, TimeSpan.FromMilliseconds(500))
    {
    }

    internal RadioDiagnosticsStore(
        IGpuUsageMonitor gpuUsageMonitor,
        ICpuUsageMonitor cpuUsageMonitor,
        IRadioDiagnosticsCollector collector,
        TimeSpan usageSampleInterval)
        : this(
            gpuUsageMonitor,
            cpuUsageMonitor,
            collector,
            usageSampleInterval,
            TimeSpan.FromSeconds(2))
    {
    }

    internal RadioDiagnosticsStore(
        IGpuUsageMonitor gpuUsageMonitor,
        ICpuUsageMonitor cpuUsageMonitor,
        IRadioDiagnosticsCollector collector,
        TimeSpan usageSampleInterval,
        TimeSpan shutdownWaitTimeout)
    {
        _gpuUsageMonitor = gpuUsageMonitor ?? throw new ArgumentNullException(nameof(gpuUsageMonitor));
        _cpuUsageMonitor = cpuUsageMonitor ?? throw new ArgumentNullException(nameof(cpuUsageMonitor));
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        if (usageSampleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(usageSampleInterval));
        }
        if (shutdownWaitTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownWaitTimeout));
        }
        _usageSampleInterval = usageSampleInterval;
        _shutdownWaitTimeout = shutdownWaitTimeout;
        _usageSamplerTask = Task.Run(SampleSystemUsageAsync);
    }

    public RadioDiagnostics Snapshot
    {
        get
        {
            lock (_sync)
            {
                ApplyCachedSystemUsage();
                long nowTicks = Stopwatch.GetTimestamp();
                double elapsedSeconds = (nowTicks - _fftFpsWindowStartTicks) / (double)Stopwatch.Frequency;
                if (elapsedSeconds >= 1.5 && _snapshot.FftFps > 0)
                {
                    _snapshot.FftFps = 0;
                }
                return _snapshot;
            }
        }
    }

    public void Update(RadioDiagnosticsMutator mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        lock (_sync) mutator(ref _snapshot);
    }

    public void Reset()
    {
        lock (_sync)
        {
            _snapshot = default;
            _fftFrameCount = 0;
            _fftFps = 0;
            _fftFpsWindowStartTicks = Stopwatch.GetTimestamp();
        }
    }

    public void UpdateMain(RadioDiagnostics source, double timeProcCycle, double? forceTimeTotal)
    {
        lock (_sync)
        {
            _snapshot.GainReductionDb = source.GainReductionDb;
            _snapshot.BufferIMaxValue = source.BufferIMaxValue;
            _snapshot.BufferIMinValue = source.BufferIMinValue;
            _snapshot.BufferQMaxValue = source.BufferQMaxValue;
            _snapshot.BufferQMinValue = source.BufferQMinValue;
            _snapshot.AudioWriteIntervalMs = source.AudioWriteIntervalMs;
            _snapshot.EffectiveSampleRateHz = source.EffectiveSampleRateHz;
            _snapshot.TimeProcCycle = timeProcCycle;
            ApplyCachedSystemUsage();

            _snapshot.BufferWPtr = source.BufferWPtr;
            _snapshot.BufferRPtr = source.BufferRPtr;
            _snapshot.BufferPtrDiff = source.BufferPtrDiff;
            if (forceTimeTotal.HasValue) _snapshot.TimeTotal = forceTimeTotal.Value;
        }
    }

    public void UpdateFft(MainFftTiming timing, MainFftMetrics metrics)
    {
        lock (_sync)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            _fftFrameCount++;
            double elapsedSeconds = (nowTicks - _fftFpsWindowStartTicks) / (double)Stopwatch.Frequency;
            if (elapsedSeconds >= 1.0)
            {
                _fftFps = _fftFrameCount / elapsedSeconds;
                _fftFrameCount = 0;
                _fftFpsWindowStartTicks = nowTicks;
            }

            _snapshot.TimeMainFft = timing.ElapsedMs;
            _snapshot.TimeTotal = timing.TotalMs;
            _snapshot.TimeOsLag = timing.OsLagMs;
            _snapshot.TimeCpuPrep = timing.CpuPrep;
            _snapshot.TimeCpuPost = timing.CpuPost;
            _snapshot.TimeFftCore = timing.FftCore;

            _snapshot.TimeFftFullResCopy = timing.FullResCopy;
            _snapshot.TimeFftAggregate = timing.Aggregate;
            _snapshot.TimeGpuPrep = timing.GpuPrep;
            _snapshot.TimeGpuUpload = timing.GpuUpload;
            _snapshot.TimeGpuShader = timing.GpuShader;
            _snapshot.TimeGpuDownload = timing.GpuDownload;
            _snapshot.TimeGpuPost = timing.GpuPost;
            _snapshot.TimeGpuPack = timing.GpuPack;
            _snapshot.TimeGpuUploadNative = timing.GpuUploadNative;
            _snapshot.TimeGpuDispatch = timing.GpuDispatch;
            _snapshot.TimeGpuReadback = timing.GpuReadback;
            ApplyCachedSystemUsage();
            _snapshot.FftFps = _fftFps;
            _snapshot.FftRequestCount = metrics.RequestedCount;
            _snapshot.FftCompletedCount = metrics.CompletedCount;
            _snapshot.FftDroppedCount = metrics.DroppedCount;
            _snapshot.FftLatestRequestId = metrics.LatestRequestId;
            _snapshot.FftLatestCompletedId = metrics.LatestCompletedId;
            _snapshot.FftQueueDepth = metrics.QueueDepth;
        }
    }

    public void ApplySignalInput(
        ref RadioDiagnostics diagnostics,
        SignalInputDiagnosticsSnapshot snapshot) =>
        _collector.ApplySignalInput(ref diagnostics, snapshot);

    public void ApplyProcessingCycle(
        ref RadioDiagnostics diagnostics,
        ProcessingCycleDiagnosticsSnapshot snapshot) =>
        _collector.ApplyProcessingCycle(ref diagnostics, snapshot);

    private async Task SampleSystemUsageAsync()
    {
        CancellationToken cancellationToken = _usageSamplerCancellation.Token;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SystemUsageSnapshot previous = Volatile.Read(ref _usageSnapshot);
                GpuUsageSnapshot gpu = previous.Gpu;
                CpuUsageSnapshot cpu = previous.Cpu;
                try
                {
                    gpu = _gpuUsageMonitor.GetUsage();
                }
                catch
                {
                    // Keep the last valid sample. Diagnostics must not stop the processing paths.
                }
                try
                {
                    cpu = _cpuUsageMonitor.GetUsage();
                }
                catch
                {
                    // Keep the last valid sample. Diagnostics must not stop the processing paths.
                }
                Volatile.Write(ref _usageSnapshot, new SystemUsageSnapshot(gpu, cpu));
                await Task.Delay(_usageSampleInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ApplyCachedSystemUsage()
    {
        SystemUsageSnapshot usage = Volatile.Read(ref _usageSnapshot);
        _snapshot.GpuAppUsagePercent = usage.Gpu.AppUsagePercent;
        _snapshot.GpuUsagePercent = usage.Gpu.TotalUsagePercent;
        _snapshot.CpuAppUsagePercent = usage.Cpu.AppUsagePercent;
        _snapshot.CpuTotalUsagePercent = usage.Cpu.TotalUsagePercent;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _usageSamplerCancellation.Cancel();
        bool completed;
        try
        {
            completed = _usageSamplerTask.Wait(_shutdownWaitTimeout);
        }
        catch (AggregateException ex)
        {
            Debug.WriteLine($"[RadioDiagnosticsStore] Usage sampler stopped with an error: {ex.Flatten().InnerException}");
            completed = true;
        }

        if (completed)
        {
            DisposeResources();
            return;
        }

        Debug.WriteLine("[RadioDiagnosticsStore] Usage sampler shutdown timed out; cleanup will continue after the provider returns.");
        _ = _usageSamplerTask.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception;
                ((RadioDiagnosticsStore)state!).DisposeResources();
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        _usageSamplerCancellation.Dispose();
        if (_gpuUsageMonitor is IDisposable disposableGpu) disposableGpu.Dispose();
        if (_cpuUsageMonitor is IDisposable disposableCpu) disposableCpu.Dispose();
    }
}
