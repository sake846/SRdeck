using System.Diagnostics;
using System.Runtime.InteropServices;
using SRdeck.Models;

namespace SRdeck.DSP;

[StructLayout(LayoutKind.Sequential)]
internal struct GpuSpectrumRequest
{
    public int SpectrumWidth, NoiseWidth, StartBin, EndBin, CenterBin;
    public int SampleRateHz, FrequencyOffsetHz, SpanHz;
}

public sealed partial class GpuFftRunner
{
    private static partial class NativeMethods
    {
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_get_last_readback_timings")]
        public static extern int GetReadbackTimings(
            IntPtr handle, out double collectMs, out double copyQueueMs, out double flushMs);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_process_spectrum")]
        public static extern int ProcessSpectrum(
            IntPtr handle, short[] inputI, short[] inputQ, int inputLength, int[] offsets,
            float offset, long submissionTag, out long completedTag, out int inputAccepted,
            [Out] float[] output, int outputCapacity,
            in GpuSpectrumRequest request, out GpuSpectrumRequest completedRequest);
    }

    private float[] _spectrumOutput = [];
    private bool _spectrumUnavailable;
    internal bool SupportsSpectrumAggregation => IsAvailable && !_spectrumUnavailable;

    internal (double Collect, double CopyQueue, double Flush) GetReadbackTimings()
    {
        int rc = NativeMethods.GetReadbackTimings(_nativeHandle, out double collect, out double copyQueue, out double flush);
        if (rc != 0) throw new InvalidOperationException($"GPU timing query failed (code={rc}).");
        return (collect, copyQueue, flush);
    }

    internal bool ProcessSpectrumPacked(
        short[] inputI, short[] inputQ, int[] offsets, float bias,
        GpuSpectrumRequest request, long submissionTag, out long completedTag,
        out bool inputAccepted, ref float[] spectrum, ref float[] noise,
        out FftPowerSummary? powerSummary)
    {
        completedTag = 0;
        inputAccepted = false;
        powerSummary = null;
        if (!SupportsSpectrumAggregation) return false;
        // Retain the largest layout so pending frames fit after a width change.
        int capacity = request.SpectrumWidth + request.NoiseWidth + (_fftSize + 4095) / 4096 + 1;
        if (_spectrumOutput.Length < capacity) _spectrumOutput = new float[capacity];
        int rc;
        int accepted;
        GpuSpectrumRequest completed;
        long started = Stopwatch.GetTimestamp();
        try
        {
            rc = NativeMethods.ProcessSpectrum(_nativeHandle, inputI, inputQ, inputI.Length, offsets,
                bias, submissionTag, out completedTag, out accepted,
                _spectrumOutput, _spectrumOutput.Length, request, out completed);
        }
        catch (EntryPointNotFoundException)
        {
            // Older native DLLs keep the full-bin path until they are rebuilt.
            _spectrumUnavailable = true;
            return false;
        }
        inputAccepted = accepted != 0;
        LastTimeShader = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LastTimePrep = LastTimeCopyFrom = LastTimeCopyTo = LastTimePost = 0;
        ReadNativeTimings();
        if (rc < 0) throw new InvalidOperationException($"GPU spectrum reduction failed (code={rc}).");
        if (rc != 0) return false;

        started = Stopwatch.GetTimestamp();
        if (spectrum.Length != completed.SpectrumWidth) spectrum = new float[completed.SpectrumWidth];
        if (noise.Length != completed.NoiseWidth) noise = new float[completed.NoiseWidth];
        Array.Copy(_spectrumOutput, spectrum, spectrum.Length);
        Array.Copy(_spectrumOutput, spectrum.Length, noise, 0, noise.Length);
        int count = completed.EndBin - completed.StartBin;
        int partialCount = (count + 4095) / 4096;
        int firstPartial = spectrum.Length + noise.Length;
        double sum = 0;
        for (int i = 0; i < partialCount; i++) sum += _spectrumOutput[firstPartial + i];
        // Sum unscaled FFT powers in double to avoid loss from a small dB bias.
        float bandDb = (float)(10 * Math.Log10(Math.Max(sum * Math.Pow(10, bias * 0.1), 1e-30)));
        float centerDb = completed.CenterBin >= 0 && completed.CenterBin < _fftSize
            ? _spectrumOutput[firstPartial + partialCount] : AppConstants.MIN_RSSI_DB;
        powerSummary = new FftPowerSummary(bandDb, centerDb, count,
            completed.SampleRateHz, completed.FrequencyOffsetHz, completed.SpanHz);
        LastTimePost = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return true;
    }
}
