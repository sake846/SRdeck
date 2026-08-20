using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

/// <summary>
/// Direct3D 11 compute implementation of the complete standard channel transform.
/// CIC stages and the polyphase FIR are collapsed into an equivalent per-phase FIR,
/// while frequency translation and output filtering execute on the GPU.
/// </summary>
internal sealed partial class NativeStandardChannelGpuBackend : IStandardChannelGpuBackend, IDisposable
{
    private readonly Dictionary<ChannelProcessingKey, ChannelState> states = [];
    private volatile GpuChannelCalibrationProfile? calibrationProfile;
    private bool available = ProbeAvailability();
    private bool disposed;

    public bool IsAvailable => !disposed && available;

    internal GpuChannelTimings LastTimings { get; private set; }

    public bool Supports(PluginChannelRequest request, IqBlockMetadata metadata, int inputSampleCount, PluginChannelAccelerationPreference? preferenceOverride = null)
    {
        if (!IsAvailable || inputSampleCount <= 0 || metadata.SampleRateHz <= 0) return false;
        if (string.IsNullOrWhiteSpace(request.Id) || request.BandwidthHz <= 0 ||
            request.OutputSampleRateHz <= 0 || request.FirTaps < 2 || request.CicStages <= 0 ||
            request.MaximumFineDecimationFactor <= 0) return false;
        if (Math.Abs(request.CenterFrequencyHz - metadata.CenterFrequencyHz) + request.BandwidthHz * 0.5 >
            metadata.SampleRateHz * 0.5) return false;
        PluginChannelAccelerationPreference preference = preferenceOverride ?? request.AccelerationPreference;
        if (ShouldUseGpu(preference)) return true;
        return preference == PluginChannelAccelerationPreference.Auto &&
            calibrationProfile?.ShouldUseGpu(request, metadata.SampleRateHz, inputSampleCount) == true;
    }

    public bool ShouldUseGpuForAutomaticBatch(
        IReadOnlyList<PluginChannelRequest> requests,
        IqBlockMetadata metadata,
        int inputSampleCount,
        int cpuParallelism) =>
        IsAvailable && requests.Count > 0 &&
        calibrationProfile?.ShouldUseGpuForBatch(
            requests, metadata.SampleRateHz, inputSampleCount, cpuParallelism) == true;

    internal static bool ShouldUseGpu(PluginChannelAccelerationPreference preference) =>
        preference is PluginChannelAccelerationPreference.GpuPreferred or
            PluginChannelAccelerationPreference.GpuRequired;

    internal void ApplyCalibration(GpuChannelCalibrationProfile? profile) =>
        calibrationProfile = profile;

    internal static bool TryGetAdapterIdentity(out GpuChannelAdapterIdentity identity)
    {
        identity = default;
        try
        {
            int result = NativeMethods.GetAdapterIdentity(
                out uint vendorId, out uint deviceId, out uint subsystemId, out uint revision,
                out ulong adapterLuid, out long driverVersion);
            if (result != 0) return false;
            identity = new(vendorId, deviceId, subsystemId, revision, adapterLuid, driverVersion);
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or SEHException)
        {
            return false;
        }
    }

    public StandardChannelProcessor.SharedChannelBlock Process(
        PluginChannelRequest request,
        IqBlockMetadata metadata,
        ReadOnlySpan<Complex32> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!available)
            throw new StandardChannelUnavailableException("The Direct3D channel backend is unavailable.");
        if (metadata.SampleCount != samples.Length)
            throw new ArgumentException("IQ metadata sample count does not match the supplied block.", nameof(metadata));

        try
        {
            ChannelState state = GetOrCreateState(request, metadata);

            int capacity = NativeMethods.GetOutputCapacity(state.Handle, samples.Length);
            if (capacity < 0)
                throw new ExternalException($"GPU channel output sizing failed ({capacity}).");
            Complex32[] output = ArrayPool<Complex32>.Shared.Rent(Math.Max(16, capacity));
            try
            {
                int result;
                int outputCount;
                unsafe
                {
                    fixed (Complex32* inputPointer = samples)
                    fixed (Complex32* outputPointer = output)
                        result = NativeMethods.Process(
                            state.Handle, inputPointer, samples.Length, outputPointer, output.Length, out outputCount);
                }
                if (result != 0)
                    throw new ExternalException($"GPU channel processing failed ({result}).");
                if (outputCount < 0 || outputCount > output.Length)
                    throw new ExternalException($"GPU channel returned an invalid output count ({outputCount}).");

                _ = NativeMethods.GetLastTimings(
                    state.Handle, out double uploadMs, out double dispatchMs, out double readbackMs);
                LastTimings = new(uploadMs, dispatchMs, readbackMs);
                return CreateSharedBlock(state, metadata, output, outputCount);
            }
            catch
            {
                ArrayPool<Complex32>.Shared.Return(output);
                throw;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or ExternalException or SEHException)
        {
            Disable();
            throw new StandardChannelUnavailableException(
                $"Direct3D channel processing became unavailable: {exception.Message}");
        }
    }

    public IReadOnlyList<StandardChannelProcessor.SharedChannelBlock> ProcessBatch(
        IReadOnlyList<PluginChannelRequest> requests,
        IqBlockMetadata metadata,
        ReadOnlyMemory<Complex32> samples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!available)
            throw new StandardChannelUnavailableException("The Direct3D channel backend is unavailable.");
        if (metadata.SampleCount != samples.Length)
            throw new ArgumentException(
                "IQ metadata sample count does not match the supplied block.", nameof(metadata));
        if (requests.Count == 0) return [];

        var states = new ChannelState[requests.Count];
        var outputs = new Complex32[]?[requests.Count];
        var expectedCounts = new int[requests.Count];
        var blocks = new StandardChannelProcessor.SharedChannelBlock[requests.Count];
        try
        {
            for (int index = 0; index < requests.Count; index++)
            {
                PluginChannelRequest request = requests[index];
                ChannelState state = GetOrCreateState(request, metadata);
                states[index] = state;
                int capacity = NativeMethods.GetOutputCapacity(state.Handle, samples.Length);
                if (capacity < 0)
                    throw new ExternalException($"GPU channel output sizing failed ({capacity}).");
                Complex32[] output = ArrayPool<Complex32>.Shared.Rent(Math.Max(16, capacity));
                outputs[index] = output;
                int result;
                unsafe
                {
                    fixed (Complex32* inputPointer = samples.Span)
                        result = NativeMethods.Submit(
                            state.Handle, inputPointer, samples.Length, output.Length,
                            out expectedCounts[index]);
                }
                if (result != 0)
                    throw new ExternalException($"GPU channel submission failed ({result}).");
                if (expectedCounts[index] < 0 || expectedCounts[index] > output.Length)
                    throw new ExternalException(
                        $"GPU channel returned an invalid output count ({expectedCounts[index]}).");
            }

            for (int index = 0; index < requests.Count; index++)
            {
                int outputCount = expectedCounts[index];
                if (outputCount > 0)
                {
                    int result;
                    unsafe
                    {
                        fixed (Complex32* outputPointer = outputs[index]!)
                            result = NativeMethods.Collect(
                                states[index].Handle, outputPointer, outputs[index]!.Length,
                                out outputCount);
                    }
                    if (result != 0)
                        throw new ExternalException($"GPU channel collection failed ({result}).");
                    if (outputCount != expectedCounts[index])
                        throw new ExternalException(
                            $"GPU channel collected {outputCount} samples; " +
                            $"expected {expectedCounts[index]}.");
                }
                blocks[index] = CreateSharedBlock(
                    states[index], metadata, outputs[index]!, outputCount);
                outputs[index] = null;
            }
            _ = NativeMethods.GetLastTimings(
                states[^1].Handle, out double uploadMs, out double dispatchMs,
                out double readbackMs);
            LastTimings = new(uploadMs, dispatchMs, readbackMs);
            return blocks;
        }
        catch (Exception exception) when (exception is DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException or ExternalException or
            SEHException)
        {
            foreach (StandardChannelProcessor.SharedChannelBlock? block in blocks)
                block?.Dispose();
            Disable();
            throw new StandardChannelUnavailableException(
                $"Direct3D channel batch processing became unavailable: {exception.Message}");
        }
        finally
        {
            foreach (Complex32[]? output in outputs)
                if (output is not null) ArrayPool<Complex32>.Shared.Return(output);
        }
    }

    public void Reset()
    {
        foreach (ChannelState state in states.Values) state.Dispose();
        states.Clear();
        LastTimings = default;
    }

    void IStandardChannelGpuBackend.Reset()
    {
        foreach (ChannelState state in states.Values) state.MarkStreamReset();
        LastTimings = default;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Reset();
    }

    private void Disable()
    {
        available = false;
        Reset();
    }

    private ChannelState GetOrCreateState(
        PluginChannelRequest request,
        IqBlockMetadata metadata)
    {
        ChannelProcessingKey key = ChannelProcessingKey.From(request);
        if (!states.TryGetValue(key, out ChannelState? state) || !state.Matches(request, metadata))
        {
            state?.Dispose();
            state = new ChannelState(request, metadata);
            states[key] = state;
        }
        else if (state.RequiresReset(metadata))
        {
            state.Reset(metadata);
        }
        return state;
    }

    private static StandardChannelProcessor.SharedChannelBlock CreateSharedBlock(
        ChannelState state,
        IqBlockMetadata metadata,
        Complex32[] output,
        int outputCount)
    {
        long outputStart = state.NextOutputSample;
        state.NextOutputSample += outputCount;
        var channelMetadata = new ChannelIqBlockMetadata(
            metadata,
            outputStart,
            state.SourceSampleOrigin,
            outputCount,
            state.Configuration);
        return new StandardChannelProcessor.SharedChannelBlock(
            channelMetadata, output, outputCount);
    }

    private static bool ProbeAvailability()
    {
        try { return NativeMethods.IsAvailable() == 1; }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            BadImageFormatException or SEHException) { return false; }
    }

    private sealed class ChannelState : IDisposable
    {
        private Guid streamId;
        private long generation;
        private bool streamResetPending;

        public ChannelState(PluginChannelRequest request, IqBlockMetadata metadata)
        {
            double offset = request.CenterFrequencyHz - metadata.CenterFrequencyHz;
            if (Math.Abs(offset) + request.BandwidthHz * 0.5 > metadata.SampleRateHz * 0.5)
                throw new StandardChannelUnavailableException(
                    $"Channel '{request.Id}' is outside the input Nyquist bandwidth.");
            (int coarse, int fine) = StandardChannelProcessor.SelectDecimationFactors(
                metadata.SampleRateHz, request);
            int total = checked(coarse * fine);
            double intermediateRate = metadata.SampleRateHz / (double)total;
            if (request.BandwidthHz * 0.5 >= Math.Min(intermediateRate, request.OutputSampleRateHz) * 0.5)
                throw new StandardChannelUnavailableException(
                    $"Channel '{request.Id}' bandwidth leaves no transition band at the selected rates.");
            long numerator = checked((long)request.OutputSampleRateHz * total);
            long divisor = GreatestCommonDivisor(numerator, metadata.SampleRateHz);
            int interpolation = checked((int)(numerator / divisor));
            int resamplerDecimation = checked((int)(metadata.SampleRateHz / divisor));
            int result = NativeMethods.Create(
                metadata.SampleRateHz,
                request.OutputSampleRateHz,
                offset,
                request.BandwidthHz,
                coarse,
                fine,
                request.FirTaps,
                request.CicStages,
                out IntPtr nativeHandle);
            var handle = new GpuChannelSafeHandle(nativeHandle);
            if (result != 0 || handle.IsInvalid)
            {
                handle.Dispose();
                if (result is -201 or -203)
                    throw new StandardChannelUnavailableException(
                        $"GPU channel '{request.Id}' cannot represent the requested conversion ({result}).");
                throw new ExternalException($"GPU channel creation failed ({result}).");
            }
            Handle = handle;
            Request = request;
            InputSampleRateHz = metadata.SampleRateHz;
            InputCenterFrequencyHz = metadata.CenterFrequencyHz;
            streamId = metadata.StreamId;
            generation = metadata.Generation;
            SourceSampleOrigin = metadata.AbsoluteSampleStart;
            Configuration = new AppliedChannelConfiguration(
                request.Id,
                request.CenterFrequencyHz,
                metadata.CenterFrequencyHz,
                metadata.SampleRateHz,
                request.OutputSampleRateHz,
                request.BandwidthHz,
                coarse,
                fine,
                interpolation,
                resamplerDecimation,
                request.FirTaps,
                request.CicStages,
                (request.FirTaps - 1) * 0.5 * total +
                    request.CicStages * (coarse - 1) * 0.5 +
                    (coarse > 1 ? 8d * coarse : 0d) +
                    request.CicStages * (fine - 1) * 0.5 * coarse +
                    (fine > 1 ? 8d * total : 0d),
                "gpu-d3d11-compute");
        }

        public GpuChannelSafeHandle Handle { get; }
        public PluginChannelRequest Request { get; }
        public int InputSampleRateHz { get; }
        public long InputCenterFrequencyHz { get; }
        public long SourceSampleOrigin { get; private set; }
        public long NextOutputSample { get; set; }
        public AppliedChannelConfiguration Configuration { get; }

        public bool Matches(PluginChannelRequest request, IqBlockMetadata metadata) =>
            Request == request && InputSampleRateHz == metadata.SampleRateHz &&
            InputCenterFrequencyHz == metadata.CenterFrequencyHz;

        public bool RequiresReset(IqBlockMetadata metadata) =>
            streamResetPending || streamId != metadata.StreamId || generation != metadata.Generation ||
            metadata.Discontinuity != IqDiscontinuity.None;

        public void MarkStreamReset() => streamResetPending = true;

        public void Reset(IqBlockMetadata metadata)
        {
            int result = NativeMethods.Reset(Handle);
            if (result != 0) throw new ExternalException($"GPU channel reset failed ({result}).");
            streamId = metadata.StreamId;
            generation = metadata.Generation;
            SourceSampleOrigin = metadata.AbsoluteSampleStart;
            NextOutputSample = 0;
            streamResetPending = false;
        }

        public void Dispose() => Handle.Dispose();

        private static long GreatestCommonDivisor(long left, long right)
        {
            while (right != 0) (left, right) = (right, left % right);
            return Math.Abs(left);
        }
    }

    private sealed class GpuChannelSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal GpuChannelSafeHandle(IntPtr nativeHandle) : base(true) => SetHandle(nativeHandle);
        protected override bool ReleaseHandle()
        {
            NativeMethods.Destroy(handle);
            return true;
        }
    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "sr_gpu";

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_is_available")]
        internal static partial int IsAvailable();

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_get_adapter_identity")]
        internal static partial int GetAdapterIdentity(
            out uint vendorId,
            out uint deviceId,
            out uint subsystemId,
            out uint revision,
            out ulong adapterLuid,
            out long driverVersion);

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_create")]
        internal static partial int Create(
            int inputSampleRate,
            int outputSampleRate,
            double frequencyOffsetHz,
            int bandwidthHz,
            int coarseFactor,
            int fineFactor,
            int firTaps,
            int cicStages,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_destroy")]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_reset")]
        internal static partial int Reset(GpuChannelSafeHandle handle);

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_get_output_capacity")]
        internal static partial int GetOutputCapacity(GpuChannelSafeHandle handle, int inputCount);

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_process")]
        internal static unsafe partial int Process(
            GpuChannelSafeHandle handle,
            Complex32* input,
            int inputCount,
            Complex32* output,
            int outputCapacity,
            out int outputCount);

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_submit")]
        internal static unsafe partial int Submit(
            GpuChannelSafeHandle handle,
            Complex32* input,
            int inputCount,
            int outputCapacity,
            out int outputCount);

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_collect")]
        internal static unsafe partial int Collect(
            GpuChannelSafeHandle handle,
            Complex32* output,
            int outputCapacity,
            out int outputCount);

        [LibraryImport(LibraryName, EntryPoint = "gpuchannel_get_last_timings")]
        internal static partial int GetLastTimings(
            GpuChannelSafeHandle handle,
            out double uploadMs,
            out double dispatchMs,
            out double readbackMs);
    }
}

internal readonly record struct GpuChannelTimings(
    double UploadMilliseconds,
    double DispatchMilliseconds,
    double ReadbackMilliseconds);

internal readonly record struct GpuChannelAdapterIdentity(
    uint VendorId,
    uint DeviceId,
    uint SubsystemId,
    uint Revision,
    ulong AdapterLuid,
    long DriverVersion);
