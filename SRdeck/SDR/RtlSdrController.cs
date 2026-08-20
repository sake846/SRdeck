using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models;

namespace SRdeck.SDR;

public class RtlSdrController : ISdrDevice
{
    private const int DeviceSampleRateHz = 2_000_000;
    private const int DefaultBufferLen = 16 * 16384;

    public SdrDeviceCapabilities Capabilities { get; } = new(SdrDeviceKind.RtlSdr);
    public int FsHz { get; set; } = DeviceSampleRateHz;
    public long CenterFreqHz { get; set; }
    public int MaxGainReduction { get; private set; } = 100;
    public int RfGainDb { get; set; } = 50;
    public bool RfAgcEnabled { get; set; }
    public float PpmAdjustment { get; set; }
    public float BiasPpm { get; set; }
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
    private List<int> _tunerGains = new();
    private readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;
    private readonly ArrayPool<short> _shortPool = ArrayPool<short>.Shared;

    public RtlSdrController(bool suppressErrors = false)
    {
        SuppressErrors = suppressErrors;
        _readCallback = OnReadAsync;
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

        _isStopping = false;
        _isStreaming = true;
        _readTask = Task.Run(() =>
        {
            int result = RtlSdrApi.rtlsdr_read_async(_device, _readCallback, IntPtr.Zero, 0, DefaultBufferLen);
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
        _isStreaming = false;
        _isStopping = false;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        Stop();
        CloseDevice();
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

    private void ApplyPpmCorrection()
    {
        if (_device == IntPtr.Zero) return;
        int ppm = (int)Math.Round(BiasPpm + PpmAdjustment);
        int result = RtlSdrApi.rtlsdr_set_freq_correction(_device, ppm);
        if (result != 0)
        {
            Debug.WriteLine($"rtlsdr_set_freq_correction failed: {result}");
        }
    }

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
        if (_isStopping || length < 2) return;

        int inputSampleCount = (int)(length / 2);
        int byteLen = (int)length;
        byte[] raw = _bytePool.Rent(byteLen);
        short[] iSamples = _shortPool.Rent(inputSampleCount);
        short[] qSamples = _shortPool.Rent(inputSampleCount);
        try
        {
            Marshal.Copy(buffer, raw, 0, byteLen);

            for (int n = 0; n < inputSampleCount; n++)
            {
                short i = (short)((raw[2 * n] - 127) << 8);
                short q = (short)((raw[2 * n + 1] - 127) << 8);
                iSamples[n] = i;
                qSamples[n] = q;
            }
            SamplesReceived?.Invoke(iSamples, qSamples, (uint)inputSampleCount);
        }
        finally
        {
            _shortPool.Return(iSamples, clearArray: false);
            _shortPool.Return(qSamples, clearArray: false);
            _bytePool.Return(raw, clearArray: false);
        }
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
        string model = "RTL2832U";
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

        WeakReferenceMessenger.Default.Send(new SdrDeviceInfoMessage(model, serial, RfGainDb));
    }

    private void ReportError(string message)
    {
        if (SuppressErrors) return;
        WeakReferenceMessenger.Default.Send(new SdrErrorMessage(message));
        Debug.WriteLine(message);
    }
}
