using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text.Json;
using SRdeck.Configuration;

namespace SRdeck.DSP;

internal enum FftCalibrationSource { Pending, Cached, Measured, Failed, Unavailable }

internal sealed record FftCalibrationEntry(string Key, int Profile, int Samples,
    double MedianMs, double P95Ms, double CpuMedianMs, double GpuMedianMs)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(Key) && Profile is >= 0 and <= 8 && Samples >= 5 &&
        double.IsFinite(MedianMs) && MedianMs > 0 && double.IsFinite(P95Ms) && P95Ms >= MedianMs &&
        double.IsFinite(CpuMedianMs) && CpuMedianMs >= 0 && double.IsFinite(GpuMedianMs) && GpuMedianMs >= 0;
}

internal sealed record FftCalibrationReport(FftCalibrationSource Source, FftCalibrationEntry? Entry = null, int ErrorCode = 0);

internal sealed class FftCalibrationCache
{
    public List<FftCalibrationEntry> Entries { get; set; } = [];
}

internal static class FftCalibrationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static JsonSettingsFile<FftCalibrationCache> Open(string path) => new(path, Options, Options,
        issue => Debug.WriteLine($"[FFT calibration] {issue.Message}"), cache =>
        {
            cache.Entries = (cache.Entries ?? []).Where(entry => entry is { IsValid: true }).TakeLast(64).ToList();
            return cache;
        });

    internal static FftCalibrationEntry? Load(string path, string key) =>
        Open(path).Load(createIfMissing: false).Entries.LastOrDefault(entry => entry.Key == key);

    internal static void Save(string path, FftCalibrationEntry entry)
    {
        if (!entry.IsValid) return;
        Open(path).Update(cache =>
        {
            cache.Entries.RemoveAll(previous => previous.Key == entry.Key);
            cache.Entries.Add(entry);
        });
    }
}

public sealed partial class GpuFftRunner
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCalibrationResult
    {
        public int Profile, Samples;
        public double MedianMs, P95Ms, CpuMedianMs, GpuMedianMs;
    }

    private static partial class NativeMethods
    {
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_set_profile")]
        public static extern int SetProfile(IntPtr handle, int profile);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_get_adapter_identity")]
        public static extern int GetIdentity(IntPtr handle, out uint vendor, out uint device,
            out uint subsystem, out uint revision, out long driver);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "gpufft_calibrate")]
        public static extern int Calibrate(IntPtr handle, int batch, int step, float bias, IntPtr request,
            int timeLimitMs, out NativeCalibrationResult result);
    }

    private readonly record struct CalibrationShape(int Batch, int Step, int Width, int NoiseWidth, int BandBins);
    private CalibrationShape? _calibrationShape;
    private FftCalibrationReport _calibrationReport = new(FftCalibrationSource.Pending);
    private readonly string _windowSignature;
    private static readonly Lazy<string?> NativeSignature = new(() =>
    {
        try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "sr_gpu.dll")))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    });

    internal unsafe FftCalibrationReport ConfigureCalibration(int batch, int step,
        GpuSpectrumRequest? request, bool allowMeasurement, string? cachePath = null, int timeLimitMs = 15_000)
    {
        var shape = new CalibrationShape(batch, step, request?.SpectrumWidth ?? 0,
            request?.NoiseWidth ?? 0, request is { } r ? r.EndBin - r.StartBin : 0);
        if (_calibrationShape == shape && (_calibrationReport.Source != FftCalibrationSource.Pending || !allowMeasurement))
            return _calibrationReport;
        _calibrationShape = shape;
        if (!IsAvailable) return _calibrationReport = new(FftCalibrationSource.Unavailable);
        cachePath ??= UserDataPaths.FftCalibrationPath;
        try
        {
            if (NativeMethods.GetIdentity(_nativeHandle, out uint vendor, out uint device,
                out uint subsystem, out uint revision, out long driver) != 0)
                return _calibrationReport = new(FftCalibrationSource.Unavailable);
            string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
            if (X86Base.IsSupported) cpu += $"/{X86Base.CpuId(1, 0).Eax:X8}";
            if (X86Base.IsSupported && (uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax >= 0x80000004)
                for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
                {
                    var registers = X86Base.CpuId(unchecked((int)leaf), 0);
                    cpu += $"/{registers.Eax:X8}{registers.Ebx:X8}{registers.Ecx:X8}{registers.Edx:X8}";
                }
            string key = BuildCalibrationKey($"{cpu}/{Environment.ProcessorCount}/{vendor:X}/{device:X}/{subsystem:X}/{revision:X}/{driver:X}",
                $"fft-calibration-v2/{NativeSignature.Value}",
                _windowSignature, _fftSize, batch, step, shape.Width, shape.NoiseWidth, shape.BandBins);
            var cached = NativeSignature.Value is null ? null : FftCalibrationStore.Load(cachePath, key);
            if (cached is not null && NativeMethods.SetProfile(_nativeHandle, cached.Profile) == 0)
                return _calibrationReport = new(FftCalibrationSource.Cached, cached);
            if (!allowMeasurement)
            {
                NativeMethods.SetProfile(_nativeHandle, 0);
                return _calibrationReport = new(FftCalibrationSource.Pending);
            }
            GpuSpectrumRequest nativeRequest = request.GetValueOrDefault();
            float bias = Models.SDR.FftProcessor.BASE_FFT_BIAS - 20 * MathF.Log10(_fftSize / 4096f);
            int result = NativeMethods.Calibrate(_nativeHandle, batch, step, bias,
                request.HasValue ? (IntPtr)(&nativeRequest) : IntPtr.Zero, timeLimitMs, out var measured);
            if (result != 0)
            {
                Debug.WriteLine($"[FFT calibration] Measurement skipped (code={result})");
                return _calibrationReport = new(FftCalibrationSource.Failed, ErrorCode: result);
            }
            var entry = new FftCalibrationEntry(key, measured.Profile, measured.Samples, measured.MedianMs,
                measured.P95Ms, measured.CpuMedianMs, measured.GpuMedianMs);
            if (!entry.IsValid)
            {
                NativeMethods.SetProfile(_nativeHandle, 0);
                return _calibrationReport = new(FftCalibrationSource.Failed);
            }
            if (NativeSignature.Value is not null) FftCalibrationStore.Save(cachePath, entry);
            return _calibrationReport = new(FftCalibrationSource.Measured, entry);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            return _calibrationReport = new(FftCalibrationSource.Unavailable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Debug.WriteLine($"[FFT calibration] {ex.Message}");
            return _calibrationReport = new(FftCalibrationSource.Failed);
        }
    }

    internal static string BuildCalibrationKey(string hardware, string implementation, string window,
        int size, int batch, int step, int width, int noiseWidth, int bandBins) =>
        // ponytail: Share the qualified FFT plan across reduction layouts; compare layouts
        // separately only if real measurements show a material change in the winning plan.
        $"fft-v2/{hardware}/{implementation}/{window}/{size}/{batch}/{step}/{(width > 0 ? "reduced" : "full")}";
}
