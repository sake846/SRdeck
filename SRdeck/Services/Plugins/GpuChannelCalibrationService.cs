using System.Diagnostics;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

internal enum GpuChannelWorkloadClass
{
    Light,
    Standard,
    Heavy
}

internal enum GpuChannelCalibrationSource
{
    Unavailable,
    Failed,
    Measured
}

internal sealed record GpuChannelCalibrationResult(
    GpuChannelCalibrationSource Source,
    GpuChannelCalibrationProfile? Profile);

internal sealed record GpuChannelCalibrationEntry(
    GpuChannelWorkloadClass Workload,
    double CpuMedianMilliseconds,
    double CpuP95Milliseconds,
    double GpuMedianMilliseconds,
    double GpuP95Milliseconds,
    bool UseGpu);

internal sealed record GpuChannelCalibrationProfile(
    IReadOnlyList<GpuChannelCalibrationEntry> Entries)
{
    public bool ShouldUseGpu(PluginChannelRequest request, int inputSampleRateHz, int inputSampleCount)
    {
        try
        {
            GpuChannelWorkloadClass workload = GpuChannelWorkloadClassifier.Classify(
                request, inputSampleRateHz, inputSampleCount);
            return Entries.FirstOrDefault(entry => entry.Workload == workload)?.UseGpu == true;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or
            StandardChannelUnavailableException)
        {
            return false;
        }
    }

    public bool ShouldUseGpuForBatch(
        IReadOnlyList<PluginChannelRequest> requests,
        int inputSampleRateHz,
        int inputSampleCount,
        int cpuParallelism)
    {
        if (requests.Count == 0 || cpuParallelism <= 0) return false;
        try
        {
            double cpuTotal = 0;
            double cpuLongest = 0;
            double gpuTotal = 0;
            foreach (PluginChannelRequest request in requests)
            {
                GpuChannelWorkloadClass workload = GpuChannelWorkloadClassifier.Classify(
                    request, inputSampleRateHz, inputSampleCount);
                GpuChannelCalibrationEntry? entry =
                    Entries.FirstOrDefault(candidate => candidate.Workload == workload);
                if (entry is null || !entry.UseGpu) return false;
                cpuTotal += entry.CpuMedianMilliseconds;
                cpuLongest = Math.Max(cpuLongest, entry.CpuMedianMilliseconds);
                gpuTotal += entry.GpuP95Milliseconds;
            }

            // GPU channel submissions are serialized, while CPU channelization is
            // executed concurrently by the dispatcher. Compare group wall-clock
            // estimates rather than multiplying a single-channel decision.
            int effectiveCpuParallelism = Math.Min(cpuParallelism, requests.Count);
            double cpuWallEstimate = Math.Max(cpuLongest, cpuTotal / effectiveCpuParallelism);
            return GpuChannelCalibrationService.IsGpuAdvantageSufficient(
                cpuWallEstimate, gpuTotal);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or
            StandardChannelUnavailableException)
        {
            return false;
        }
    }
}

internal static class GpuChannelWorkloadClassifier
{
    public static GpuChannelWorkloadClass Classify(
        PluginChannelRequest request,
        int inputSampleRateHz,
        int inputSampleCount)
    {
        (int coarse, int fine) = StandardChannelProcessor.SelectDecimationFactors(
            inputSampleRateHz, request);
        int totalDecimation = checked(coarse * fine);
        if (inputSampleCount <= 20_000 && request.FirTaps <= 33 && totalDecimation <= 2)
            return GpuChannelWorkloadClass.Light;
        if (inputSampleCount >= 65_536 || request.FirTaps >= 64 || totalDecimation >= 16)
            return GpuChannelWorkloadClass.Heavy;
        return GpuChannelWorkloadClass.Standard;
    }
}

internal sealed class GpuChannelCalibrationService
{
    private const int IterationCount = 7;
    private static readonly TimeSpan CalibrationTimeLimit = TimeSpan.FromSeconds(2);
    private readonly NativeStandardChannelGpuBackend gpuBackend;

    public GpuChannelCalibrationService(NativeStandardChannelGpuBackend gpuBackend)
    {
        this.gpuBackend = gpuBackend;
    }

    public async Task<GpuChannelCalibrationResult> CalibrateIfNeededAsync(
        CancellationToken cancellationToken = default)
    {
        if (!gpuBackend.IsAvailable ||
            !NativeStandardChannelGpuBackend.TryGetAdapterIdentity(out _))
        {
            gpuBackend.ApplyCalibration(null);
            return new(GpuChannelCalibrationSource.Unavailable, null);
        }

        GpuChannelCalibrationProfile? calibrated = await Task.Run(
            () => RunCalibration(CreateWorkloads(), cancellationToken), cancellationToken);
        gpuBackend.ApplyCalibration(calibrated);
        return new(
            calibrated is null
                ? GpuChannelCalibrationSource.Failed
                : GpuChannelCalibrationSource.Measured,
            calibrated);
    }

    private GpuChannelCalibrationProfile? RunCalibration(
        IReadOnlyList<CalibrationWorkload> workloads,
        CancellationToken cancellationToken)
    {
        long calibrationStarted = Stopwatch.GetTimestamp();
        var entries = new List<GpuChannelCalibrationEntry>();
        try
        {
            gpuBackend.Reset();
            foreach (CalibrationWorkload workload in workloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(calibrationStarted) >= CalibrationTimeLimit) return null;
                entries.Add(Benchmark(workload, calibrationStarted, cancellationToken));
            }
            return new(entries);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"GPU channel calibration failed: {exception.Message}");
            return null;
        }
        finally
        {
            gpuBackend.Reset();
        }
    }

    private GpuChannelCalibrationEntry Benchmark(
        CalibrationWorkload workload,
        long calibrationStarted,
        CancellationToken cancellationToken)
    {
        PluginChannelRequest cpuRequest = workload.Request with
        {
            AccelerationPreference = PluginChannelAccelerationPreference.Cpu
        };
        PluginChannelRequest gpuRequest = workload.Request with
        {
            AccelerationPreference = PluginChannelAccelerationPreference.GpuRequired
        };
        var cpu = new StandardChannelProcessor(cpuRequest);
        Complex32[] samples = CreateSamples(workload);

        IqBlockMetadata warmupMetadata = CreateMetadata(workload, samples.Length, 0);
        using (IChannelIqBlockLease warmup = cpu.Process(warmupMetadata, samples)) { }
        using (StandardChannelProcessor.SharedChannelBlock warmup =
               gpuBackend.Process(gpuRequest, warmupMetadata, samples)) { }

        var cpuTimes = new double[IterationCount];
        var gpuTimes = new double[IterationCount];
        for (int iteration = 0; iteration < IterationCount; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(calibrationStarted) >= CalibrationTimeLimit)
                throw new TimeoutException("GPU channel calibration exceeded its startup time budget.");
            IqBlockMetadata metadata = CreateMetadata(workload, samples.Length, iteration + 1);
            if ((iteration & 1) == 0)
            {
                cpuTimes[iteration] = MeasureCpu(cpu, metadata, samples);
                gpuTimes[iteration] = MeasureGpu(gpuRequest, metadata, samples);
            }
            else
            {
                gpuTimes[iteration] = MeasureGpu(gpuRequest, metadata, samples);
                cpuTimes[iteration] = MeasureCpu(cpu, metadata, samples);
            }
        }

        double cpuMedian = Percentile(cpuTimes, 0.5);
        double cpuP95 = Percentile(cpuTimes, 0.95);
        double gpuMedian = Percentile(gpuTimes, 0.5);
        double gpuP95 = Percentile(gpuTimes, 0.95);
        bool useGpu = IsGpuAdvantageSufficient(cpuMedian, gpuP95);
        return new(workload.Workload, cpuMedian, cpuP95, gpuMedian, gpuP95, useGpu);
    }

    internal static bool IsGpuAdvantageSufficient(double cpuMedian, double gpuP95) =>
        cpuMedian > 0 && gpuP95 >= 0 && gpuP95 < cpuMedian * 0.8;

    private static double MeasureCpu(
        StandardChannelProcessor cpu,
        IqBlockMetadata metadata,
        Complex32[] samples)
    {
        long started = Stopwatch.GetTimestamp();
        using (IChannelIqBlockLease block = cpu.Process(metadata, samples)) { }
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private double MeasureGpu(
        PluginChannelRequest request,
        IqBlockMetadata metadata,
        Complex32[] samples)
    {
        long started = Stopwatch.GetTimestamp();
        using (StandardChannelProcessor.SharedChannelBlock block =
               gpuBackend.Process(request, metadata, samples)) { }
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static double Percentile(double[] values, double percentile)
    {
        double[] sorted = [.. values.Order()];
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static Complex32[] CreateSamples(CalibrationWorkload workload)
    {
        var samples = new Complex32[workload.InputSampleCount];
        for (int index = 0; index < samples.Length; index++)
        {
            double phase = 2 * Math.PI * workload.SignalOffsetHz * index / workload.InputSampleRateHz;
            samples[index] = new((float)(0.7 * Math.Cos(phase)), (float)(0.7 * Math.Sin(phase)));
        }
        return samples;
    }

    private static IqBlockMetadata CreateMetadata(
        CalibrationWorkload workload,
        int sampleCount,
        long sequence) =>
        new(
            CalibrationStreamId, 1, sequence, sequence * sampleCount,
            Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow,
            workload.InputSampleRateHz, workload.InputCenterFrequencyHz, sampleCount,
            IqInputSource.Playback,
            sequence == 0 ? IqDiscontinuity.StreamStarted : IqDiscontinuity.None);

    private static readonly Guid CalibrationStreamId =
        new("D2AD1239-E55A-4B8E-A595-B2673064C16E");

    private static IReadOnlyList<CalibrationWorkload> CreateWorkloads()
    {
        const long center = 100_000_000;
        return
        [
            new(
                GpuChannelWorkloadClass.Light,
                new PluginChannelRequest(
                    "gpu-calibration-light", center + 125_000, 400_000, 1_000_000,
                    2_000_000, 1_000_000, 33, 2, 4, false),
                2_000_000, center, 16_384, 125_000),
            new(
                GpuChannelWorkloadClass.Standard,
                new PluginChannelRequest(
                    "gpu-calibration-standard", center + 48_000, 96_000, 240_000,
                    600_000, 240_000, 48, 2, 4, false,
                    MaximumFineDecimationFactor: 4),
                2_400_000, center, 49_152, 48_000),
            new(
                GpuChannelWorkloadClass.Heavy,
                new PluginChannelRequest(
                    "gpu-calibration-heavy", center + 12_000, 4_800, 48_000,
                    72_000, 56_000, 64, 3, 4, false,
                    240_000, 400_000, 8),
                2_400_000, center, 120_000, 12_000)
        ];
    }

    private sealed record CalibrationWorkload(
        GpuChannelWorkloadClass Workload,
        PluginChannelRequest Request,
        int InputSampleRateHz,
        long InputCenterFrequencyHz,
        int InputSampleCount,
        double SignalOffsetHz);
}
