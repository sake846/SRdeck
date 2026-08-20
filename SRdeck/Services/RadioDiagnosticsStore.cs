using System;
using System.Diagnostics;
using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.Services;

public interface IRadioDiagnosticsStore
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
    private readonly object _sync = new();
    private readonly IGpuUsageMonitor _gpuUsageMonitor;
    private readonly IRadioDiagnosticsCollector _collector;
    private RadioDiagnostics _snapshot;
    private long _fftFpsWindowStartTicks = Stopwatch.GetTimestamp();
    private int _fftFrameCount;
    private double _fftFps;

    public RadioDiagnosticsStore(
        IGpuUsageMonitor gpuUsageMonitor,
        IRadioDiagnosticsCollector collector)
    {
        _gpuUsageMonitor = gpuUsageMonitor ?? throw new ArgumentNullException(nameof(gpuUsageMonitor));
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
    }

    public RadioDiagnostics Snapshot
    {
        get
        {
            lock (_sync) return _snapshot;
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
            SyncGpuUsage();



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
            SyncGpuUsage();
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

    private void SyncGpuUsage()
    {
        var gpuUsage = _gpuUsageMonitor.GetUsage();
        _snapshot.GpuAppUsagePercent = gpuUsage.AppUsagePercent;
        _snapshot.GpuUsagePercent = gpuUsage.TotalUsagePercent;
    }
}
