using System.Buffers;
using System.Diagnostics;
using SRdeckPlugin.Contracts;
using SRdeckCore.SignalProcessing;

namespace SRdeck.Services.Plugins;

public sealed class StandardChannelUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Stateful block channelizer used by the host. One instance represents one
/// request/consumer stream and must be called serially in source-block order.
/// </summary>
public sealed class StandardChannelProcessor
{
    private readonly PluginChannelRequest request;
    private readonly IPluginMetrics metrics;
    private readonly ComplexFrequencyTranslator translator = new();
    private readonly BoundedCicDecimator decimator = new();
    private readonly BoundedCicDecimator fineDecimator = new();
    private readonly CicCompensationFilter cicCompensation = new();
    private readonly CicCompensationFilter fineCicCompensation = new();
    private readonly PolyphaseRationalResampler resampler;
    private readonly Action<float, float> outputSink;
    private Complex32[]? outputBuffer;
    private int outputCount;
    private bool configured;
    private Guid streamId;
    private long generation;
    private long sourceSampleOrigin;
    private long nextOutputSample;
    private AppliedChannelConfiguration configuration;
    internal int ConfigurationCount { get; private set; }
    internal int StreamResetCount { get; private set; }

    public StandardChannelProcessor(PluginChannelRequest request, IPluginMetrics? metrics = null)
    {
        Validate(request);
        this.request = request;
        this.metrics = metrics ?? NullPluginMetrics.Instance;
        resampler = new PolyphaseRationalResampler(request.FirTaps);
        outputSink = AppendOutput;
    }

    public PluginChannelRequest Request => request;
    public AppliedChannelConfiguration Configuration => configured
        ? configuration
        : throw new InvalidOperationException("The channel processor has not received an input block.");

    public IChannelIqBlockLease Process(IqBlockMetadata metadata, ReadOnlySpan<Complex32> samples)
    {
        SharedChannelBlock shared = ProcessShared(metadata, samples);
        try { return shared.Acquire(request.Id); }
        finally { shared.Dispose(); }
    }

    internal SharedChannelBlock ProcessShared(IqBlockMetadata metadata, ReadOnlySpan<Complex32> samples)
    {
        if (metadata.SampleCount != samples.Length)
            throw new ArgumentException("IQ metadata sample count does not match the supplied block.", nameof(metadata));
        if (metadata.SampleRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(metadata), "The input sample rate must be positive.");

        bool configurationChanged = !configured ||
            configuration.InputSampleRateHz != metadata.SampleRateHz ||
            configuration.InputCenterFrequencyHz != metadata.CenterFrequencyHz;
        if (configurationChanged)
        {
            Configure(metadata);
        }
        else if (streamId != metadata.StreamId || generation != metadata.Generation ||
                 metadata.Discontinuity != IqDiscontinuity.None)
        {
            ResetStream(metadata);
        }

        long outputStart = nextOutputSample;
        int estimatedCount = Math.Max(16, checked((int)Math.Ceiling(
            samples.Length * (double)request.OutputSampleRateHz / metadata.SampleRateHz) +
            resampler.InterpolationFactor + 4));
        outputBuffer = ArrayPool<Complex32>.Shared.Rent(estimatedCount);
        outputCount = 0;
        long started = Stopwatch.GetTimestamp();
        try
        {
            translator.Configure(request.CenterFrequencyHz - metadata.CenterFrequencyHz, metadata.SampleRateHz);
            foreach (Complex32 sample in samples)
            {
                translator.Mix(sample.I, sample.Q, out float mixedI, out float mixedQ);
                if (!decimator.TryProcess(mixedI, mixedQ, out float coarseI, out float coarseQ))
                    continue;
                cicCompensation.Process(coarseI, coarseQ, out coarseI, out coarseQ);
                if (configuration.FineDecimationFactor > 1 &&
                    !fineDecimator.TryProcess(coarseI, coarseQ, out coarseI, out coarseQ))
                    continue;
                if (configuration.FineDecimationFactor > 1)
                    fineCicCompensation.Process(coarseI, coarseQ, out coarseI, out coarseQ);
                resampler.Process(coarseI, coarseQ, outputSink);
            }

            Complex32[] owner = outputBuffer;
            outputBuffer = null;
            var channelMetadata = new ChannelIqBlockMetadata(
                metadata,
                outputStart,
                sourceSampleOrigin,
                outputCount,
                configuration);
            nextOutputSample += outputCount;
            metrics.AddCounter(PluginProcessingStage.Channelization, "input_samples", samples.Length, "samples");
            metrics.AddCounter(PluginProcessingStage.Channelization, "output_samples", outputCount, "samples");
            return new SharedChannelBlock(channelMetadata, owner, outputCount);
        }
        finally
        {
            metrics.RecordDuration(PluginProcessingStage.Channelization, "processing", Stopwatch.GetElapsedTime(started));
            if (outputBuffer is not null)
            {
                ArrayPool<Complex32>.Shared.Return(outputBuffer);
                outputBuffer = null;
            }
        }
    }

    private void Configure(IqBlockMetadata metadata)
    {
        if (request.AccelerationPreference == PluginChannelAccelerationPreference.GpuRequired)
            throw new StandardChannelUnavailableException(
                $"Channel '{request.Id}' requires a GPU streaming channel backend, but none is available.");
        double offset = request.CenterFrequencyHz - metadata.CenterFrequencyHz;
        if (Math.Abs(offset) + request.BandwidthHz * 0.5 > metadata.SampleRateHz * 0.5)
            throw new StandardChannelUnavailableException(
                $"Channel '{request.Id}' is outside the input Nyquist bandwidth.");

        (int coarseFactor, int fineFactor) = SelectDecimationFactors(metadata.SampleRateHz, request);
        int totalFactor = checked(coarseFactor * fineFactor);
        double intermediateRate = metadata.SampleRateHz / (double)totalFactor;
        double cutoffHz = request.BandwidthHz * 0.5;
        if (cutoffHz >= Math.Min(intermediateRate, request.OutputSampleRateHz) * 0.5)
            throw new StandardChannelUnavailableException(
                $"Channel '{request.Id}' bandwidth leaves no transition band at the selected rates.");

        decimator.Configure(coarseFactor, request.CicStages);
        fineDecimator.Configure(fineFactor, request.CicStages);
        cicCompensation.Configure(coarseFactor, request.CicStages);
        fineCicCompensation.Configure(fineFactor, request.CicStages);
        resampler.Configure(metadata.SampleRateHz, totalFactor, request.OutputSampleRateHz, cutoffHz);
        translator.Configure(offset, metadata.SampleRateHz);
        translator.ResetPhase();
        streamId = metadata.StreamId;
        generation = metadata.Generation;
        sourceSampleOrigin = metadata.AbsoluteSampleStart;
        nextOutputSample = 0;
        configuration = new AppliedChannelConfiguration(
            request.Id,
            request.CenterFrequencyHz,
            metadata.CenterFrequencyHz,
            metadata.SampleRateHz,
            request.OutputSampleRateHz,
            request.BandwidthHz,
            coarseFactor,
            fineFactor,
            resampler.InterpolationFactor,
            resampler.DecimationFactor,
            request.FirTaps,
            request.CicStages,
            resampler.GroupDelaySamples * totalFactor + decimator.GroupDelayInputSamples +
                cicCompensation.GroupDelayOutputSamples * coarseFactor +
                fineDecimator.GroupDelayInputSamples * coarseFactor +
                (fineFactor > 1 ? fineCicCompensation.GroupDelayOutputSamples * totalFactor : 0),
            request.AccelerationPreference == PluginChannelAccelerationPreference.GpuPreferred
                ? resampler.UsesSimd ? "cpu-simd (gpu-unavailable)" : "cpu-scalar (gpu-unavailable)"
                : resampler.UsesSimd ? "cpu-simd" : "cpu-scalar");
        configured = true;
        ConfigurationCount++;
    }

    private void ResetStream(IqBlockMetadata metadata)
    {
        decimator.Reset();
        fineDecimator.Reset();
        cicCompensation.Reset();
        fineCicCompensation.Reset();
        resampler.Reset();
        translator.ResetPhase();
        streamId = metadata.StreamId;
        generation = metadata.Generation;
        sourceSampleOrigin = metadata.AbsoluteSampleStart;
        nextOutputSample = 0;
        StreamResetCount++;
    }

    private void AppendOutput(float i, float q)
    {
        Complex32[] buffer = outputBuffer ?? throw new InvalidOperationException("No output block is active.");
        if (outputCount == buffer.Length)
        {
            Complex32[] replacement = ArrayPool<Complex32>.Shared.Rent(checked(buffer.Length * 2));
            buffer.AsSpan(0, outputCount).CopyTo(replacement);
            ArrayPool<Complex32>.Shared.Return(buffer);
            outputBuffer = buffer = replacement;
        }
        buffer[outputCount++] = new Complex32(i, q);
    }

    internal static int SelectCoarseDecimationFactor(
        int inputSampleRateHz,
        int maximumIntermediateSampleRateHz,
        int minimumIntermediateSampleRateHz)
    {
        if (inputSampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRateHz));
        if (maximumIntermediateSampleRateHz <= 0) return 1;
        int factor = Math.Max(1, (int)Math.Ceiling(
            inputSampleRateHz / (double)maximumIntermediateSampleRateHz));
        while (factor > 1 && minimumIntermediateSampleRateHz > 0 &&
               inputSampleRateHz / (double)factor < minimumIntermediateSampleRateHz)
            factor--;
        return factor;
    }

    internal static (int Coarse, int Fine) SelectDecimationFactors(
        int inputSampleRateHz,
        PluginChannelRequest request)
    {
        if (inputSampleRateHz <= request.OutputSampleRateHz) return (1, 1);
        if (request.MaximumFineDecimationFactor <= 1 ||
            request.CoarseOutputMinimumSampleRateHz <= 0 ||
            request.CoarseOutputMaximumSampleRateHz <= 0)
            return (SelectCoarseDecimationFactor(inputSampleRateHz,
                request.MaximumIntermediateSampleRateHz,
                request.MinimumIntermediateSampleRateHz), 1);

        (int Coarse, int Fine) best = default;
        bool found = false;
        int bestInterpolation = int.MaxValue;
        double bestFinalDistance = double.MaxValue;
        double bestCoarseDistance = double.MaxValue;
        double preferredFinal = (request.MinimumIntermediateSampleRateHz +
            request.MaximumIntermediateSampleRateHz) * 0.5;
        double preferredCoarse = (request.CoarseOutputMinimumSampleRateHz +
            request.CoarseOutputMaximumSampleRateHz) * 0.5;
        int maximumCoarse = Math.Max(1,
            inputSampleRateHz / request.CoarseOutputMinimumSampleRateHz + 1);
        for (int coarse = 1; coarse <= maximumCoarse; coarse++)
        {
            double coarseRate = inputSampleRateHz / (double)coarse;
            bool directLowRateStage = coarse == 1 &&
                inputSampleRateHz < request.CoarseOutputMinimumSampleRateHz;
            if (!directLowRateStage && (coarseRate < request.CoarseOutputMinimumSampleRateHz ||
                coarseRate > request.CoarseOutputMaximumSampleRateHz)) continue;
            for (int fine = 1; fine <= request.MaximumFineDecimationFactor; fine++)
            {
                int total = checked(coarse * fine);
                double finalRate = inputSampleRateHz / (double)total;
                if (finalRate < request.MinimumIntermediateSampleRateHz ||
                    finalRate > request.MaximumIntermediateSampleRateHz) continue;
                long numerator = checked((long)request.OutputSampleRateHz * total);
                long divisor = GreatestCommonDivisor(numerator, inputSampleRateHz);
                int interpolation = checked((int)(numerator / divisor));
                double finalDistance = Math.Abs(finalRate - preferredFinal);
                double coarseDistance = Math.Abs(coarseRate - preferredCoarse);
                if (found && (interpolation > bestInterpolation ||
                    interpolation == bestInterpolation && finalDistance > bestFinalDistance ||
                    interpolation == bestInterpolation && finalDistance == bestFinalDistance &&
                    coarseDistance >= bestCoarseDistance)) continue;
                best = (coarse, fine);
                bestInterpolation = interpolation;
                bestFinalDistance = finalDistance;
                bestCoarseDistance = coarseDistance;
                found = true;
            }
        }
        if (!found)
            throw new StandardChannelUnavailableException(
                $"Channel '{request.Id}' has no valid two-stage decimation plan.");
        return best;
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Abs(left);
    }

    private static void Validate(PluginChannelRequest value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Id);
        if (value.BandwidthHz <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (value.OutputSampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (value.MaximumIntermediateSampleRateHz < 0 || value.MinimumIntermediateSampleRateHz < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (value.MaximumIntermediateSampleRateHz > 0 && value.MinimumIntermediateSampleRateHz >
            value.MaximumIntermediateSampleRateHz)
            throw new ArgumentException("The minimum intermediate rate cannot exceed the maximum.", nameof(value));
        if (value.FirTaps < 2)
            throw new ArgumentException("The FIR tap count must be at least two.", nameof(value));
        if (value.CicStages <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (value.RequestedQueueCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (value.MaximumFineDecimationFactor <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (!Enum.IsDefined(value.AccelerationPreference))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (value.CoarseOutputMinimumSampleRateHz < 0 || value.CoarseOutputMaximumSampleRateHz < 0 ||
            value.CoarseOutputMaximumSampleRateHz > 0 && value.CoarseOutputMinimumSampleRateHz >
            value.CoarseOutputMaximumSampleRateHz)
            throw new ArgumentException("The coarse-stage output-rate range is invalid.", nameof(value));
    }

    internal sealed class SharedChannelBlock(
        ChannelIqBlockMetadata metadata,
        Complex32[] buffer,
        int count) : IDisposable
    {
        private Complex32[]? owner = buffer;
        private int referenceCount = 1;

        public IChannelIqBlockLease Acquire(string requestId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
            int current;
            do
            {
                current = Volatile.Read(ref referenceCount);
                if (current == 0) throw new ObjectDisposedException(nameof(SharedChannelBlock));
            }
            while (Interlocked.CompareExchange(ref referenceCount, current + 1, current) != current);
            ChannelIqBlockMetadata leaseMetadata = metadata with
            {
                Configuration = metadata.Configuration with { RequestId = requestId }
            };
            return new SharedLease(this, leaseMetadata, count);
        }

        private ReadOnlyMemory<Complex32> Samples => owner is null
            ? ReadOnlyMemory<Complex32>.Empty
            : owner.AsMemory(0, count);

        public void Dispose() => Release();

        private void Release()
        {
            if (Interlocked.Decrement(ref referenceCount) != 0) return;
            Complex32[]? released = Interlocked.Exchange(ref owner, null);
            if (released is not null) ArrayPool<Complex32>.Shared.Return(released);
        }

        private sealed class SharedLease(
            SharedChannelBlock shared,
            ChannelIqBlockMetadata metadata,
            int count) : IChannelIqBlockLease
        {
            private SharedChannelBlock? owner = shared;
            public ChannelIqBlockMetadata Metadata { get; } = metadata;
            public ReadOnlyMemory<Complex32> Samples => owner?.Samples[..count] ??
                ReadOnlyMemory<Complex32>.Empty;
            public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
        }
    }
}
