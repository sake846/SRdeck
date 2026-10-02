using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models;

namespace SRdeck.SDR;

public sealed class HackRfController : ISdrDevice, ISdrStreamingDiagnostics, ISdrSampleBlockSource
{
    internal const int DefaultSampleRateHz = 8_000_000;
    private const int StallTimeoutMilliseconds = 2_000;

    private readonly object _sync = new();
    private readonly HackRfApi.SampleBlockCallback _rxCallback;
    private readonly RtlSdrSampleDispatcher _sampleDispatcher;
    private IntPtr _device;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private bool _libraryInitialized;
    private volatile bool _isStreaming;
    private volatile bool _isStopping;
    private bool _isDisposed;
    private float _ppmAdjustment;
    private float _biasPpm;

    public HackRfController(bool suppressErrors = false)
    {
        SuppressErrors = suppressErrors;
        _rxCallback = OnSamplesReceived;
        _sampleDispatcher = new RtlSdrSampleDispatcher(
            (samplesI, samplesQ, sampleCount) => SamplesReceived?.Invoke(samplesI, samplesQ, sampleCount),
            sampleBlockReceived: block => SampleBlockReceived?.Invoke(block),
            iqConverter: ConvertSignedIq);
    }

    public SdrDeviceCapabilities Capabilities { get; } = new(SdrDeviceKind.HackRf);
    public int FsHz { get; set; } = DefaultSampleRateHz;
    public long CenterFreqHz { get; set; }
    public int MaxGainReduction => 100;
    public int RfGainDb { get; set; } = 50;
    public bool RfAgcEnabled { get; set; }
    public int LnaState { get; set; }
    public int NotchFilterMode { get; set; }
    public string ModelName { get; private set; } = "HackRF One";
    public string SerialNumber { get; private set; } = string.Empty;
    internal bool SuppressErrors { get; set; }

    public float PpmAdjustment
    {
        get => _ppmAdjustment;
        set
        {
            if (_ppmAdjustment.Equals(value)) return;
            _ppmAdjustment = value;
            FreqChange();
        }
    }

    public float BiasPpm
    {
        get => _biasPpm;
        set
        {
            if (_biasPpm.Equals(value)) return;
            _biasPpm = value;
            FreqChange();
        }
    }

    public int QueuedSampleBlockCount => _sampleDispatcher.QueuedBlockCount;
    public long CallbackCount => _sampleDispatcher.CallbackCount;
    public long DroppedCallbackCount => _sampleDispatcher.DroppedCallbackCount;
    public double LastCallbackAgeSeconds => _sampleDispatcher.LastCallbackAgeSeconds;
    public int LastCallbackLengthBytes => _sampleDispatcher.LastCallbackLength;
    public long UnexpectedCallbackLengthCount => _sampleDispatcher.UnexpectedCallbackLengthCount;

    public event Action<short[], short[], uint>? SamplesReceived;
    public event Action<SdrSampleBlock>? SampleBlockReceived;
    public event Action<double, int>? GainHardwareChanged;
    public event Action? DeviceRemoved;
    public event Action? StreamStalled;

    public bool Open()
    {
        lock (_sync)
        {
            if (_isDisposed) return false;
            if (_device != IntPtr.Zero) return true;

            try
            {
                int result = HackRfApi.hackrf_init();
                if (result != HackRfApi.Success)
                {
                    ReportNativeError("hackrf_init", result);
                    return false;
                }
                _libraryInitialized = true;

                result = HackRfApi.hackrf_open(out _device);
                if (result != HackRfApi.Success || _device == IntPtr.Zero)
                {
                    ReportNativeError("hackrf_open", result);
                    CloseDeviceLocked();
                    return false;
                }

                if (!ConfigureDeviceLocked())
                {
                    CloseDeviceLocked();
                    return false;
                }

                ReadDeviceInfoLocked();
                NotifyDeviceInfo();
                return true;
            }
            catch (DllNotFoundException)
            {
                ReportError("hackrf.dll または依存 DLL が見つかりません。HackRF Tools の 64-bit DLL を SRdeck.exe と同じフォルダーに配置してください。");
            }
            catch (BadImageFormatException)
            {
                ReportError("hackrf.dll のアーキテクチャが一致しません。64-bit 版を使用してください。");
            }
            catch (EntryPointNotFoundException exception)
            {
                ReportError($"hackrf.dll に必要な関数がありません: {exception.Message}");
            }
            catch (Exception exception)
            {
                ReportError($"HackRF を開けませんでした: {exception.Message}");
            }

            CloseDeviceLocked();
            return false;
        }
    }

    public bool Start()
    {
        lock (_sync)
        {
            if (_isDisposed || _isStopping) return false;
            if (_isStreaming) return true;
            if (!Open()) return false;
            if (!ConfigureDeviceLocked()) return false;

            _sampleDispatcher.Start();
            _isStopping = false;
            // libhackrf can invoke the callback before hackrf_start_rx returns.
            _isStreaming = true;
            int result;
            try
            {
                result = HackRfApi.hackrf_start_rx(_device, _rxCallback, IntPtr.Zero);
            }
            catch (Exception exception)
            {
                _sampleDispatcher.Stop();
                _isStreaming = false;
                ReportError($"HackRF の受信開始に失敗しました: {exception.Message}");
                return false;
            }
            if (result != HackRfApi.Success)
            {
                _sampleDispatcher.Stop();
                _isStreaming = false;
                ReportNativeError("hackrf_start_rx", result);
                return false;
            }

            StartMonitorLocked(_device);
            return true;
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        Task? monitorTask;
        IntPtr device;
        lock (_sync)
        {
            if (_isStopping) return;
            _isStopping = true;
            cancellation = _monitorCancellation;
            monitorTask = _monitorTask;
            _monitorCancellation = null;
            _monitorTask = null;
            device = _device;
        }

        cancellation?.Cancel();
        if (device != IntPtr.Zero && _isStreaming)
        {
            try
            {
                int result = HackRfApi.hackrf_stop_rx(device);
                if (result != HackRfApi.Success) ReportNativeError("hackrf_stop_rx", result);
            }
            catch (Exception exception)
            {
                ReportError($"HackRF の停止に失敗しました: {exception.Message}");
            }
        }

        if (monitorTask != null && !monitorTask.IsCompleted)
        {
            try { monitorTask.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
        }
        cancellation?.Dispose();
        _sampleDispatcher.Stop();

        lock (_sync)
        {
            _isStreaming = false;
            _isStopping = false;
        }
    }

    public bool ApplySampleRate(int sampleRateHz)
    {
        int normalized = SdrSampleRatePolicy.Normalize(sampleRateHz, SdrDeviceKind.HackRf);
        if (normalized != sampleRateHz) return false;
        lock (_sync)
        {
            if (_isDisposed || _isStreaming) return false;
            if (_device == IntPtr.Zero)
            {
                FsHz = normalized;
                return true;
            }
            return ApplySampleRateLocked(normalized);
        }
    }

    public void GainChange()
    {
        lock (_sync)
        {
            if (_device == IntPtr.Zero || _isDisposed || !ApplyGainSettingsLocked()) return;
        }
        GainHardwareChanged?.Invoke(0, RfGainDb);
    }

    public void FreqChange()
    {
        lock (_sync)
        {
            if (_device == IntPtr.Zero || _isDisposed || CenterFreqHz <= 0) return;
            ulong frequencyHz = CalculateAdjustedFrequency(CenterFreqHz, BiasPpm, PpmAdjustment);
            int result = HackRfApi.hackrf_set_freq(_device, frequencyHz);
            if (result != HackRfApi.Success) ReportNativeError("hackrf_set_freq", result);
        }
    }

    public void ApplyLnaAndNotch()
    {
        // HackRF exposes LNA/VGA gain through the unified gain control above.
    }

    private bool ConfigureDeviceLocked()
    {
        int normalized = SdrSampleRatePolicy.Normalize(FsHz, SdrDeviceKind.HackRf);
        if (!ApplySampleRateLocked(normalized)) return false;

        if (CenterFreqHz > 0)
        {
            int result = HackRfApi.hackrf_set_freq(
                _device,
                CalculateAdjustedFrequency(CenterFreqHz, BiasPpm, PpmAdjustment));
            if (result != HackRfApi.Success)
            {
                ReportNativeError("hackrf_set_freq", result);
                return false;
            }
        }

        int ampResult = HackRfApi.hackrf_set_amp_enable(_device, 0);
        if (ampResult != HackRfApi.Success)
        {
            ReportNativeError("hackrf_set_amp_enable", ampResult);
            return false;
        }
        return ApplyGainSettingsLocked();
    }

    private bool ApplySampleRateLocked(int sampleRateHz)
    {
        int result = HackRfApi.hackrf_set_sample_rate_manual(_device, (uint)sampleRateHz, 1);
        if (result != HackRfApi.Success)
        {
            ReportNativeError("hackrf_set_sample_rate_manual", result);
            return false;
        }
        uint bandwidthHz = HackRfApi.hackrf_compute_baseband_filter_bw_round_down_lt((uint)sampleRateHz);
        result = HackRfApi.hackrf_set_baseband_filter_bandwidth(_device, bandwidthHz);
        if (result != HackRfApi.Success)
        {
            ReportNativeError("hackrf_set_baseband_filter_bandwidth", result);
            return false;
        }
        FsHz = sampleRateHz;
        return true;
    }

    private bool ApplyGainSettingsLocked()
    {
        HackRfGainStages stages = HackRfGainPolicy.Resolve(RfGainDb);
        int result = HackRfApi.hackrf_set_lna_gain(_device, stages.LnaGainDb);
        if (result != HackRfApi.Success)
        {
            ReportNativeError("hackrf_set_lna_gain", result);
            return false;
        }
        result = HackRfApi.hackrf_set_vga_gain(_device, stages.VgaGainDb);
        if (result != HackRfApi.Success)
        {
            ReportNativeError("hackrf_set_vga_gain", result);
            return false;
        }
        return true;
    }

    private int OnSamplesReceived(IntPtr transferPointer)
    {
        if (!_isStreaming || _isStopping || transferPointer == IntPtr.Zero) return 1;
        try
        {
            HackRfApi.HackRfTransfer transfer = Marshal.PtrToStructure<HackRfApi.HackRfTransfer>(transferPointer);
            int length = Math.Min(transfer.BufferLength, transfer.ValidLength) & ~1;
            if (transfer.Buffer == IntPtr.Zero || length < 2) return 0;
            _sampleDispatcher.TryEnqueue(transfer.Buffer, length);
            return _isStreaming && !_isStopping ? 0 : 1;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[HackRfController] RX callback failed: {exception}");
            return _isStopping ? 1 : 0;
        }
    }

    internal static short ConvertSignedSample(byte sample) =>
        (short)(unchecked((sbyte)sample) << 8);

    internal static void ConvertSignedIq(ReadOnlySpan<byte> raw, Span<short> samplesI, Span<short> samplesQ)
    {
        int sampleCount = Math.Min(raw.Length / 2, Math.Min(samplesI.Length, samplesQ.Length));
        for (int index = 0; index < sampleCount; index++)
        {
            samplesI[index] = ConvertSignedSample(raw[index * 2]);
            samplesQ[index] = ConvertSignedSample(raw[index * 2 + 1]);
        }
    }

    internal static ulong CalculateAdjustedFrequency(long centerFrequencyHz, float biasPpm, float adjustmentPpm)
    {
        double adjusted = centerFrequencyHz * (1.0 + (biasPpm + adjustmentPpm) / 1_000_000.0);
        return (ulong)Math.Clamp(adjusted, 1_000_000.0, 7_250_000_000.0);
    }

    private void StartMonitorLocked(IntPtr sessionDevice)
    {
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = new CancellationTokenSource();
        CancellationToken token = _monitorCancellation.Token;
        _monitorTask = Task.Run(() => MonitorStreamingAsync(sessionDevice, token), token);
    }

    private async Task MonitorStreamingAsync(IntPtr sessionDevice, CancellationToken token)
    {
        bool stallReported = false;
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                int status = HackRfApi.hackrf_is_streaming(sessionDevice);
                if (status != HackRfApi.Streaming)
                {
                    if (!token.IsCancellationRequested && !_isStopping) DeviceRemoved?.Invoke();
                    return;
                }

                double callbackAgeMilliseconds = CallbackCount > 0
                    ? LastCallbackAgeSeconds * 1000
                    : Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                if (callbackAgeMilliseconds >= StallTimeoutMilliseconds)
                {
                    if (!stallReported) StreamStalled?.Invoke();
                    stallReported = true;
                }
                else
                {
                    stallReported = false;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Debug.WriteLine($"[HackRfController] Stream monitor failed: {exception.Message}");
            if (!token.IsCancellationRequested && !_isStopping) DeviceRemoved?.Invoke();
        }
    }

    private void ReadDeviceInfoLocked()
    {
        try
        {
            if (HackRfApi.hackrf_board_id_read(_device, out byte boardId) == HackRfApi.Success)
            {
                string? boardName = Marshal.PtrToStringAnsi(HackRfApi.hackrf_board_id_name(boardId));
                if (!string.IsNullOrWhiteSpace(boardName))
                    ModelName = boardName.Contains("HackRF", StringComparison.OrdinalIgnoreCase)
                        ? boardName : $"HackRF {boardName}";
            }
        }
        catch { }

        try
        {
            var serial = new HackRfApi.ReadPartIdSerialNumber
            {
                PartId = new uint[2],
                SerialNumber = new uint[4]
            };
            if (HackRfApi.hackrf_board_partid_serialno_read(_device, ref serial) == HackRfApi.Success)
                SerialNumber = string.Concat(serial.SerialNumber.Select(part => part.ToString("X8")));
        }
        catch { }
    }

    private void NotifyDeviceInfo() =>
        WeakReferenceMessenger.Default.Send(new SdrDeviceInfoMessage(ModelName, SerialNumber, RfGainDb));

    private void CloseDeviceLocked()
    {
        if (_device != IntPtr.Zero)
        {
            try { HackRfApi.hackrf_close(_device); }
            catch { }
            _device = IntPtr.Zero;
        }
        if (_libraryInitialized)
        {
            try { HackRfApi.hackrf_exit(); }
            catch { }
            _libraryInitialized = false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }
        Stop();
        lock (_sync)
        {
            CloseDeviceLocked();
        }
        _sampleDispatcher.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ReportNativeError(string operation, int result) =>
        ReportError($"{operation} failed: {HackRfApi.GetErrorName(result)} ({result})");

    private void ReportError(string message)
    {
        Debug.WriteLine(message);
        if (!SuppressErrors) WeakReferenceMessenger.Default.Send(new SdrErrorMessage(message));
    }
}
