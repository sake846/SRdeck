using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models;

namespace SRdeck.SDR;

public class RtlSdrController : ISdrDevice, ISdrStreamingDiagnostics
{
    private const int DeviceSampleRateHz = 2_000_000;
    private const int DefaultBufferLen = 16 * 16384;
    // The librtlsdr default is 15 transfers (about 0.98 s at 2 MS/s). Keep
    // twice that coverage so a long Windows GC/scheduler pause does not exhaust
    // every outstanding USB transfer before managed callbacks can resume.
    private const uint AsyncTransferBufferCount = 32;

    public SdrDeviceCapabilities Capabilities { get; } = new(SdrDeviceKind.RtlSdr);
    public int FsHz { get; set; } = DeviceSampleRateHz;
    public long CenterFreqHz { get; set; }
    public int MaxGainReduction { get; private set; } = 100;
    public int RfGainDb { get; set; } = 50;
    public bool RfAgcEnabled { get; set; }
    public string ModelName { get; private set; } = "RTL-SDR";
    private float _ppmAdjustment;
    private float _biasPpm;
    private int? _appliedPpm;

    public float PpmAdjustment
    {
        get => _ppmAdjustment;
        set
        {
            if (_ppmAdjustment.Equals(value)) return;
            _ppmAdjustment = value;
            ApplyPpmCorrection();
        }
    }

    public float BiasPpm
    {
        get => _biasPpm;
        set
        {
            if (_biasPpm.Equals(value)) return;
            _biasPpm = value;
            ApplyPpmCorrection();
        }
    }
    public int LnaState { get; set; }
    public int NotchFilterMode { get; set; }

    public event Action<short[], short[], uint>? SamplesReceived;
    public event Action<double, int>? GainHardwareChanged;
    public event Action? DeviceRemoved;
    public event Action? StreamStalled { add { } remove { } }

    private IntPtr _device = IntPtr.Zero;
    private readonly object _sync = new();
    private readonly object _gainSync = new();
    private Task? _readTask;
    private bool _isStreaming;
    private bool _isStopping;
    private bool _isDisposed;
    internal bool SuppressErrors { get; set; }
    private readonly RtlSdrApi.RtlSdrReadAsyncCbT _readCallback;
    private readonly RtlSdrSampleDispatcher _sampleDispatcher;
    private List<int> _tunerGains = new();

    public int QueuedSampleBlockCount => _sampleDispatcher.QueuedBlockCount;
    public long CallbackCount => _sampleDispatcher.CallbackCount;
    public long DroppedCallbackCount => _sampleDispatcher.DroppedCallbackCount;
    public double LastCallbackAgeSeconds => _sampleDispatcher.LastCallbackAgeSeconds;
    public int LastCallbackLengthBytes => _sampleDispatcher.LastCallbackLength;
    public long UnexpectedCallbackLengthCount => _sampleDispatcher.UnexpectedCallbackLengthCount;

    public RtlSdrController(bool suppressErrors = false)
    {
        SuppressErrors = suppressErrors;
        _readCallback = OnReadAsync;
        _sampleDispatcher = new RtlSdrSampleDispatcher(
            (samplesI, samplesQ, sampleCount) =>
                SamplesReceived?.Invoke(samplesI, samplesQ, sampleCount),
            DefaultBufferLen);
    }

    public bool Open()
    {
        if (_device != IntPtr.Zero) return true;
        try
        {
            uint count = RtlSdrApi.rtlsdr_get_device_count();
            if (count == 0)
            {
                ReportError("RTL-SDR(rtllsdr)デバイスが見つかりません。WinUSBドライバと接続、または使用中アプリ(HDSDR等)を確認してください。");
                return false;
            }

            var openResult = RtlSdrApi.rtlsdr_open(ref _device, 0);
            if (openResult != 0 || _device == IntPtr.Zero)
            {
                ReportError($"RTL-SDR open失敗 (rtlsdr_open={openResult})。他アプリがデバイスを占有していないか確認してください。");
                return false;
            }

            if (RtlSdrApi.rtlsdr_set_sample_rate(_device, (uint)DeviceSampleRateHz) != 0)
            {
                ReportError("rtlsdr_set_sample_rate failed");
                CloseDevice();
                return false;
            }
            FsHz = DeviceSampleRateHz;

            // Keep VHF/FM operation stable regardless of prior app state.
            TrySetDirectSamplingOff();
            TrySetOffsetTuningOn();

            FreqChange();
            ApplyPpmCorrection();
            LoadSupportedTunerGains();
            ApplyGainSettings();

            NotifyDeviceInfo();
            return true;
        }
        catch (DllNotFoundException)
        {
            CloseDevice();
            ReportError("rtlsdr.dll または依存DLLが見つかりません。SRdeck.exe と同じフォルダに rtlsdr.dll/libusb-1.0.dll を配置してください。");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            CloseDevice();
            ReportError($"rtlsdr.dll の関数が不足しています: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            CloseDevice();
            ReportError($"RTL-SDR開始時に例外が発生しました: {ex.Message}");
            return false;
        }
    }

    public bool Start()
    {
        if (_isStreaming) return true;
        if (!Open()) return false;
        if (RtlSdrApi.rtlsdr_reset_buffer(_device) != 0)
        {
            ReportError("rtlsdr_reset_buffer failed");
            return false;
        }

        // Detection opens the device before the session starts. Re-apply the
        // current center frequency here as the hardware may still be tuned to
        // the frequency used during detection. A later UI retune also calls
        // FreqChange(), which is why this was previously corrected by swiping.
        FreqChange();

        _isStopping = false;
        _sampleDispatcher.Start();
        _isStreaming = true;
        _readTask = Task.Run(() =>
        {
            int result = RtlSdrApi.rtlsdr_read_async(
                _device,
                _readCallback,
                IntPtr.Zero,
                AsyncTransferBufferCount,
                DefaultBufferLen);
            if (!_isStopping && result != 0)
            {
                DeviceRemoved?.Invoke();
            }
        });
        return true;
    }

    public void Stop()
    {
        Task? readTask;
        lock (_sync)
        {
            if (_isStopping) return;
            _isStopping = true;
            readTask = _readTask;
        }

        if (_device != IntPtr.Zero && _isStreaming)
        {
            try
            {
                RtlSdrApi.rtlsdr_cancel_async(_device);
            }
            catch
            {
                // ignore
            }
        }

        if (readTask != null)
        {
            try
            {
                if (!readTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    Debug.Print("[RtlSdrController] rtlsdr_read_async task wait timed out.");
                }
            }
            catch
            {
                // ignore
            }
        }

        _readTask = null;
        _sampleDispatcher.Stop();
        _isStreaming = false;
        _isStopping = false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        Stop();
        CloseDevice();
        _sampleDispatcher.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void CloseDevice()
    {
        if (_device == IntPtr.Zero) return;
        if (_readTask != null && !_readTask.IsCompleted)
        {
            Debug.Print("[RtlSdrController] CloseDevice skipped because read async task is still running.");
            return;
        }

        try { RtlSdrApi.rtlsdr_close(_device); }
        catch { /* best effort during probing/cleanup */ }
        _device = IntPtr.Zero;
        _appliedPpm = null;
    }

    public void GainChange()
    {
        ApplyGainSettings();
        GainHardwareChanged?.Invoke(0.0, RfGainDb);
    }

    public void FreqChange()
    {
        if (_device == IntPtr.Zero) return;
        if (CenterFreqHz <= 0) return;

        int result = RtlSdrApi.rtlsdr_set_center_freq(_device, (uint)Math.Clamp(CenterFreqHz, 1, int.MaxValue));
        if (result != 0)
        {
            Debug.WriteLine($"rtlsdr_set_center_freq failed: {result}");
        }
    }

    public void ApplyLnaAndNotch()
    {
        // RTL2832U does not support SDRplay-style LNA/notch controls.
    }

    protected virtual void ApplyPpmCorrection()
    {
        if (_device == IntPtr.Zero) return;
        int ppm = CalculatePpmCorrection(BiasPpm, PpmAdjustment);
        if (_appliedPpm == ppm) return;
        int result = RtlSdrApi.rtlsdr_set_freq_correction(_device, ppm);
        // librtlsdr returns -2 when this exact correction is already active.
        if (result is 0 or -2)
        {
            _appliedPpm = ppm;
        }
        else
        {
            Debug.WriteLine($"rtlsdr_set_freq_correction failed: {result}");
        }
    }

    internal static int CalculatePpmCorrection(float biasPpm, float adjustmentPpm) =>
        (int)Math.Round(biasPpm + adjustmentPpm);

    private void LoadSupportedTunerGains()
    {
        _tunerGains.Clear();
        if (_device == IntPtr.Zero) return;

        int count = RtlSdrApi.rtlsdr_get_tuner_gains(_device, IntPtr.Zero);
        if (count <= 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int) * count);
        try
        {
            int read = RtlSdrApi.rtlsdr_get_tuner_gains(_device, buffer);
            if (read <= 0) return;
            var gains = new int[read];
            Marshal.Copy(buffer, gains, 0, read);
            _tunerGains.AddRange(gains);
            MaxGainReduction = 100;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ApplyGainSettings()
    {
        if (_device == IntPtr.Zero) return;

        lock (_gainSync)
        {
            // Gain is controlled by the host-side AGC. Never enable RTL2832 or tuner AGC.
            TrySetRtlAgcMode(on: false);
            int manualRc = RtlSdrApi.rtlsdr_set_tuner_gain_mode(_device, 1);
            if (manualRc != 0)
            {
                Debug.WriteLine($"rtlsdr_set_tuner_gain_mode(manual) failed: {manualRc}");
                return;
            }

            if (_tunerGains.Count == 0) return;

            int gainIndex = (int)Math.Round(Math.Clamp(RfGainDb, 0, MaxGainReduction) / (double)MaxGainReduction * (_tunerGains.Count - 1));
            gainIndex = Math.Clamp(gainIndex, 0, _tunerGains.Count - 1);
            int gainTenthdB = _tunerGains[gainIndex];

            int appliedTenthdB = int.MinValue;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int gainRc = RtlSdrApi.rtlsdr_set_tuner_gain(_device, gainTenthdB);
                if (gainRc != 0)
                {
                    Debug.WriteLine($"rtlsdr_set_tuner_gain failed: {gainRc}");
                    return;
                }

                appliedTenthdB = RtlSdrApi.rtlsdr_get_tuner_gain(_device);
                if (appliedTenthdB == gainTenthdB) break;

                // Reassert manual path before one retry when driver/device state drifts.
                TrySetRtlAgcMode(on: false);
                manualRc = RtlSdrApi.rtlsdr_set_tuner_gain_mode(_device, 1);
                if (manualRc != 0) break;
            }

        }
    }

    private void TrySetRtlAgcMode(bool on)
    {
        if (_device == IntPtr.Zero) return;
        try
        {
            int result = RtlSdrApi.rtlsdr_set_agc_mode(_device, on ? 1 : 0);
            if (result != 0) Debug.WriteLine($"rtlsdr_set_agc_mode({(on ? 1 : 0)}) failed: {result}");
        }
        catch (EntryPointNotFoundException)
        {
            // Older rtlsdr builds may not expose this symbol.
        }
    }

    private void OnReadAsync(IntPtr buffer, uint length, IntPtr context)
    {
        if (_isStopping || length > int.MaxValue) return;
        _sampleDispatcher.TryEnqueue(buffer, (int)length);
    }

    /// <summary>
    /// Converts the RTL2832U's unsigned 8-bit I/Q sample to the signed
    /// 16-bit representation used by the signal pipeline.  Clamp the scaled
    /// value before narrowing: ADC code 255 would otherwise produce +32768,
    /// which wraps to <see cref="short.MinValue"/>.
    /// </summary>
    internal static short ConvertUnsignedSample(byte sample)
    {
        int scaled = (sample - 127) << 8;
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    private void TrySetDirectSamplingOff()
    {
        if (_device == IntPtr.Zero) return;
        try
        {
            int result = RtlSdrApi.rtlsdr_set_direct_sampling(_device, 0);
            if (result != 0) Debug.WriteLine($"rtlsdr_set_direct_sampling(0) failed: {result}");
        }
        catch (EntryPointNotFoundException)
        {
            // Older rtlsdr builds may not expose this symbol.
        }
    }

    private void TrySetOffsetTuningOn()
    {
        if (_device == IntPtr.Zero) return;
        try
        {
            int result = RtlSdrApi.rtlsdr_set_offset_tuning(_device, 1);
            if (result != 0) Debug.WriteLine($"rtlsdr_set_offset_tuning(1) failed: {result}");
        }
        catch (EntryPointNotFoundException)
        {
            // Older rtlsdr builds may not expose this symbol.
        }
    }

    private void NotifyDeviceInfo()
    {
        string model = "RTL-SDR";
        string serial = string.Empty;

        try
        {
            IntPtr namePtr = RtlSdrApi.rtlsdr_get_device_name(0);
            if (namePtr != IntPtr.Zero)
            {
                string? name = Marshal.PtrToStringAnsi(namePtr);
                if (!string.IsNullOrWhiteSpace(name)) model = name;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            int tunerType = RtlSdrApi.rtlsdr_get_tuner_type(_device);
            Debug.WriteLine($"RTL-SDR tuner type id: {tunerType}");
        }
        catch
        {
            // ignore
        }

        ModelName = model;
        WeakReferenceMessenger.Default.Send(new SdrDeviceInfoMessage(model, serial, RfGainDb));
    }

    private void ReportError(string message)
    {
        if (SuppressErrors) return;
        WeakReferenceMessenger.Default.Send(new SdrErrorMessage(message));
        Debug.WriteLine(message);
    }
}
