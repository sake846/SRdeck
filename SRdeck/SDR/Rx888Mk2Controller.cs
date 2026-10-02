using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Configuration;
using SRdeck.Messages;
using SRdeck.Models;
using SRdeck.Models.SDR;

namespace SRdeck.SDR;

/// <summary>RX-888 MK2 controller backed by sr_rx888.dll.</summary>
public sealed class Rx888Mk2Controller :
    ISdrDevice,
    ISdrStreamingDiagnostics,
    ISdrSampleBlockSource
{
    private static readonly object GainDiagnosticLogSync = new();
    private const int NormalAdcFrequencyHz = 64_000_000;
    private const int OverclockAdcFrequencyHz = 128_000_000;
    private const double HfMaxRfAttenuationDb = 31.5;
    private const double HfMinIfGainDb = -10.0;
    private const double HfMaxIfGainDb = 33.0;
    private const double VhfMaxRfGainDb = 49.6;
    private const double VhfMaxIfGainDb = 40.8;
    private static readonly int[] SupportedSampleRates = [8_000_000, 32_000_000, 64_000_000];

    private readonly object _sync = new();
    private readonly Rx888Api.Rx888ReadAsyncCb _callback;
    private readonly Rx888SampleDispatcher _sampleDispatcher;
    private readonly IGainUpdateWorker _gainUpdateWorker;
    private IntPtr _handle;
    private Task? _eventLoopTask;
    private Task? _shutdownTask;
    private GCHandle _callbackRoot;
    private Rx888Api.RfMode _currentRfMode = Rx888Api.RfMode.NoRfMode;
    private bool _isStreaming;
    private bool _isStopping;
    private bool _isDisposed;
    private int _eventLoopThreadId;
    private Rx888Api.RfMode _lastGainRfMode = Rx888Api.RfMode.NoRfMode;
    private double? _lastGainRfSettingDb;
    private double? _lastGainIfSettingDb;

    public Rx888Mk2Controller(bool suppressErrors = false)
    {
        SuppressErrors = suppressErrors;
        _callback = OnAsyncData;
        _sampleDispatcher = new Rx888SampleDispatcher(
            (samplesI, samplesQ, sampleCount) =>
                SamplesReceived?.Invoke(samplesI, samplesQ, sampleCount),
            GetExpectedCallbackLengthBytes(),
            sampleBlockReceived: block => SampleBlockReceived?.Invoke(block));
        _gainUpdateWorker = new GainUpdateWorker(ApplyGainChange);
    }

    public SdrDeviceCapabilities Capabilities { get; } = new(SdrDeviceKind.Rx888);
    public int FsHz { get; set; } = 8_000_000;
    public long CenterFreqHz { get; set; }
    public int MaxGainReduction { get; } = 100;
    public int RfGainDb { get; set; } = 100;
    public bool RfAgcEnabled { get; set; }
    public float PpmAdjustment { get; set; }
    public float BiasPpm { get; set; }
    public int LnaState { get; set; }
    public int NotchFilterMode { get; set; }
    public bool EnableAdcDither { get; set; } = true;
    public bool EnableAdcRandom { get; set; } = true;
    public string ModelName => "RX-888 MK2";
    public string SerialNumber { get; private set; } = string.Empty;
    public string CurrentBackendName { get; private set; } = "unknown";
    internal bool SuppressErrors { get; set; }

    public int QueuedSampleBlockCount => _sampleDispatcher.QueuedBlockCount;
    public long CallbackCount => _sampleDispatcher.CallbackCount;
    public long DroppedCallbackCount => _sampleDispatcher.DroppedCallbackCount;
    public double LastCallbackAgeSeconds => _sampleDispatcher.LastCallbackAgeSeconds;
    public int LastCallbackLengthBytes => _sampleDispatcher.LastCallbackLengthBytes;
    public long UnexpectedCallbackLengthCount => _sampleDispatcher.UnexpectedCallbackLengthCount;

    public event Action<short[], short[], uint>? SamplesReceived;
    public event Action<SdrSampleBlock>? SampleBlockReceived;
    public event Action<double, int>? GainHardwareChanged;
    public event Action? DeviceRemoved;
    public event Action? StreamStalled { add { } remove { } }

    public bool Open()
    {
        lock (_sync)
        {
            if (_isDisposed) return false;
            if (_isStopping || _shutdownTask is { IsCompleted: false })
            {
                ReportError("RX-888 の停止処理が完了していません。完了後に再試行してください。");
                return false;
            }
            if (_handle != IntPtr.Zero) return true;
            if (!IsSupportedSampleRate(FsHz))
            {
                ReportError($"RX-888 は {FsHz / 1_000_000.0:0.#} MS/s をサポートしていません。");
                return false;
            }

            try
            {
                string imageFile = ResolveImageFilePath();
                _handle = OpenWithRetry(imageFile);
                if (_handle == IntPtr.Zero)
                {
                    string firmwareHint = File.Exists(imageFile)
                        ? $"使用ファームウェア: {imageFile}"
                        : $"SDDC_FX3.img がありません。bootloader から復旧する場合は {imageFile} に配置してください。";
                    ReportError(
                        "rx888_open に失敗しました。RX-888 MK2 の接続、WinUSB/libusb ドライバー、" +
                        $"他アプリによる占有を確認してください。\n{firmwareHint}");
                    return false;
                }

                CurrentBackendName = GetBackendName(_handle);
                SerialNumber = GetDeviceSerialNumber();
                if (!ConfigureDeviceLocked())
                {
                    CloseHandleLocked();
                    return false;
                }

                WeakReferenceMessenger.Default.Send(
                    new SdrDeviceInfoMessage(ModelName, SerialNumber, RfGainDb));
                return true;
            }
            catch (DllNotFoundException exception)
            {
                CloseHandleLocked();
                ReportError(
                    "sr_rx888.dll または libusb-1.0.dll が見つかりません。" +
                    $"SRdeck.exe と同じフォルダーに配置してください。\n{exception.Message}");
                return false;
            }
            catch (EntryPointNotFoundException exception)
            {
                CloseHandleLocked();
                ReportError($"sr_rx888.dll の API が対応していません: {exception.Message}");
                return false;
            }
            catch (Exception exception)
            {
                CloseHandleLocked();
                ReportError($"RX-888 MK2 を開けませんでした: {exception.Message}");
                return false;
            }
        }
    }

    public bool Start()
    {
        lock (_sync)
        {
            if (_isDisposed) return false;
            if (_isStopping || _shutdownTask is { IsCompleted: false })
            {
                ReportError("RX-888 の停止処理が完了していません。完了後に再試行してください。");
                return false;
            }
            if (_isStreaming) return true;
            if (!Open()) return false;

            try
            {
                if (!ConfigureDeviceLocked()) return false;
                if (!_callbackRoot.IsAllocated) _callbackRoot = GCHandle.Alloc(_callback);
                if (Rx888Api.SetAsyncParams(_handle, 0, 0, _callback, IntPtr.Zero) < 0)
                {
                    ReportError("rx888_set_async_params に失敗しました。");
                    return false;
                }

                _sampleDispatcher.Start();
                IntPtr sessionHandle = _handle;
                if (Rx888Api.StartStreaming(sessionHandle) < 0)
                {
                    ReportError("rx888_start_streaming に失敗しました。USB 帯域とドライバーを確認してください。");
                    _sampleDispatcher.Stop();
                    _ = BeginStopLocked();
                    return false;
                }
                _isStreaming = true;
                _eventLoopTask = Task.Run(() => EventLoop(sessionHandle));
                return true;
            }
            catch (Exception exception)
            {
                ReportError($"RX-888 MK2 の受信を開始できませんでした: {exception.Message}");
                _isStreaming = false;
                _ = BeginStopLocked();
                return false;
            }
        }
    }

    public void Stop()
    {
        Task? shutdown;
        lock (_sync) shutdown = BeginStopLocked();
        _sampleDispatcher.Stop();
        if (shutdown is null ||
            Environment.CurrentManagedThreadId == Volatile.Read(ref _eventLoopThreadId))
        {
            return;
        }

        if (!shutdown.Wait(TimeSpan.FromSeconds(3)))
        {
            Debug.WriteLine(
                "[Rx888Mk2Controller] Stop timed out; native handle retained until the event loop exits.");
        }
    }

    private Task? BeginStopLocked()
    {
        if (_handle == IntPtr.Zero) return null;
        if (_shutdownTask is { IsCompleted: false }) return _shutdownTask;

        _isStopping = true;
        bool wasStreaming = _isStreaming;
        _isStreaming = false;
        IntPtr sessionHandle = _handle;
        Task? eventLoop = _eventLoopTask;
        _shutdownTask = NativeStreamShutdown.Begin(
            () =>
            {
                if (!wasStreaming) return;
                int result = Rx888Api.StopStreaming(sessionHandle);
                if (result < 0)
                    throw new InvalidOperationException($"rx888_stop_streaming failed: {result}");
            },
            eventLoop,
            () =>
            {
                lock (_sync)
                {
                    if (_handle != sessionHandle) return;
                    Rx888Api.Close(sessionHandle);
                    if (_callbackRoot.IsAllocated) _callbackRoot.Free();
                    _handle = IntPtr.Zero;
                    _eventLoopTask = null;
                    _shutdownTask = null;
                    _currentRfMode = Rx888Api.RfMode.NoRfMode;
                    ResetGainSettingCache();
                    _isStopping = false;
                }
                _sampleDispatcher.Stop();
            },
            exception => ReportError(
                $"RX-888 の停止処理に失敗しました。ハンドルは保持しています: {exception.Message}"));
        return _shutdownTask;
    }

    public bool ApplySampleRate(int sampleRateHz)
    {
        if (!IsSupportedSampleRate(sampleRateHz)) return false;

        int previousRate;
        bool restart;
        lock (_sync)
        {
            if (_isDisposed || _isStopping) return false;
            if (FsHz == sampleRateHz) return true;
            previousRate = FsHz;
            restart = _isStreaming;
        }

        if (restart) Stop();
        bool configured;
        lock (_sync)
        {
            if (_shutdownTask is { IsCompleted: false }) return false;
            FsHz = sampleRateHz;
            configured = _handle == IntPtr.Zero || ConfigureDeviceLocked();
        }

        bool applied = configured && Open() && (!restart || Start());
        if (applied) return true;

        lock (_sync)
        {
            FsHz = previousRate;
            if (_handle != IntPtr.Zero) _ = ConfigureDeviceLocked();
        }
        _ = Open();
        if (restart) _ = Start();
        return false;
    }

    public void FreqChange()
    {
        lock (_sync)
        {
            if (_isStopping || _isDisposed || _handle == IntPtr.Zero || CenterFreqHz <= 0) return;

            Rx888Api.RfMode desiredMode = CenterFreqHz < Math.Max(1, FsHz)
                ? Rx888Api.RfMode.HfMode
                : Rx888Api.RfMode.VhfMode;
            if (desiredMode != _currentRfMode)
            {
                int modeResult = Rx888Api.SetRfMode(_handle, desiredMode);
                if (modeResult >= 0)
                {
                    _currentRfMode = desiredMode;
                    ResetGainSettingCache();
                }
                else Debug.WriteLine($"rx888_set_rf_mode({desiredMode}) failed: {modeResult}");
            }

            double ppm = BiasPpm + PpmAdjustment;
            double correctedFrequency = CenterFreqHz * (1.0 + ppm / 1_000_000.0);
            int result = Rx888Api.SetTunerFrequency(_handle, correctedFrequency);
            if (result < 0)
                Debug.WriteLine($"rx888_set_tuner_frequency failed: {result}");
        }
    }

    public void GainChange()
    {
        if (!_isDisposed) _gainUpdateWorker.RequestUpdate();
    }

    private void ApplyGainChange()
    {
        var updateTimer = Stopwatch.StartNew();
        double lockWaitMs;
        double controlTransferMs = 0;
        int rfResult = 0;
        int ifResult = 0;
        bool rfCommandSent = false;
        bool ifCommandSent = false;
        bool sentToDevice = false;
        bool hf = false;
        double appliedSystemDb;

        lock (_sync)
        {
            if (_isStopping || _isDisposed) return;
            lockWaitMs = updateTimer.Elapsed.TotalMilliseconds;
            double ratio = Math.Clamp(RfGainDb, 0, MaxGainReduction) /
                (double)Math.Max(1, MaxGainReduction);
            hf = CenterFreqHz > 0 && CenterFreqHz < Math.Max(1, FsHz);
            Rx888Api.RfMode gainRfMode = hf ? Rx888Api.RfMode.HfMode : Rx888Api.RfMode.VhfMode;
            bool modeChanged = _lastGainRfMode != gainRfMode;
            if (hf)
            {
                double targetGainDb = HfMinIfGainDb - HfMaxRfAttenuationDb +
                    ratio * (HfMaxIfGainDb - HfMinIfGainDb + HfMaxRfAttenuationDb);
                double rfAttenuationDb = Math.Clamp(
                    HfMinIfGainDb - targetGainDb, 0, HfMaxRfAttenuationDb);
                double ifGainDb = targetGainDb + rfAttenuationDb;
                appliedSystemDb = targetGainDb;
                if (_handle != IntPtr.Zero)
                {
                    sentToDevice = true;
                    var controlTimer = Stopwatch.StartNew();
                    if (modeChanged || _lastGainRfSettingDb != rfAttenuationDb)
                    {
                        rfCommandSent = true;
                        rfResult = Rx888Api.SetHfAttenuation(_handle, rfAttenuationDb);
                        if (rfResult >= 0) _lastGainRfSettingDb = rfAttenuationDb;
                    }
                    if (modeChanged || _lastGainIfSettingDb != ifGainDb)
                    {
                        ifCommandSent = true;
                        ifResult = Rx888Api.SetTunerIfAttenuation(_handle, ifGainDb);
                        if (ifResult >= 0) _lastGainIfSettingDb = ifGainDb;
                    }
                    controlTransferMs = controlTimer.Elapsed.TotalMilliseconds;
                }
            }
            else
            {
                double rfGainDb = ratio * VhfMaxRfGainDb;
                double ifGainDb = ratio * VhfMaxIfGainDb;
                appliedSystemDb = rfGainDb + ifGainDb;
                if (_handle != IntPtr.Zero)
                {
                    sentToDevice = true;
                    var controlTimer = Stopwatch.StartNew();
                    if (modeChanged || _lastGainRfSettingDb != rfGainDb)
                    {
                        rfCommandSent = true;
                        rfResult = Rx888Api.SetTunerRfAttenuation(_handle, rfGainDb);
                        if (rfResult >= 0) _lastGainRfSettingDb = rfGainDb;
                    }
                    if (modeChanged || _lastGainIfSettingDb != ifGainDb)
                    {
                        ifCommandSent = true;
                        ifResult = Rx888Api.SetTunerIfAttenuation(_handle, ifGainDb);
                        if (ifResult >= 0) _lastGainIfSettingDb = ifGainDb;
                    }
                    controlTransferMs = controlTimer.Elapsed.TotalMilliseconds;
                }
            }

            if (sentToDevice && rfResult >= 0 && ifResult >= 0)
                _lastGainRfMode = gainRfMode;
        }

        long totalMs = updateTimer.ElapsedMilliseconds;
        if (sentToDevice && (totalMs >= 250 || rfResult < 0 || ifResult < 0))
        {
            WriteGainDiagnostic(
                $"[Rx888Mk2Controller] Gain update backend={CurrentBackendName} band={(hf ? "HF" : "VHF")} " +
                $"lockWait={lockWaitMs:F0}ms control={controlTransferMs:F0}ms total={totalMs}ms " +
                $"requests={(rfCommandSent ? 1 : 0)}/{(ifCommandSent ? 1 : 0)} results={rfResult}/{ifResult}");
        }

        GainHardwareChanged?.Invoke(appliedSystemDb, RfGainDb);
    }

    private void ResetGainSettingCache()
    {
        _lastGainRfMode = Rx888Api.RfMode.NoRfMode;
        _lastGainRfSettingDb = null;
        _lastGainIfSettingDb = null;
    }

    private static void WriteGainDiagnostic(string message)
    {
        try
        {
            string path = Path.Combine(UserDataPaths.UserDataDirectory, "logs", "rx888-gain-diagnostics.log");
            lock (GainDiagnosticLogSync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.Now:O}\t{message}{Environment.NewLine}",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Diagnostic logging must not interfere with gain control.
        }
    }

    public void ApplyLnaAndNotch()
    {
        lock (_sync)
        {
            if (_isStopping || _isDisposed || _handle == IntPtr.Zero) return;
            bool useModeBits = NotchFilterMode != 0;
            bool dither = useModeBits ? (NotchFilterMode & 0x1) != 0 : EnableAdcDither;
            bool random = useModeBits ? (NotchFilterMode & 0x2) != 0 : EnableAdcRandom;
            _ = Rx888Api.SetVhfBias(_handle, LnaState == 0 ? 0 : 1);
            _ = Rx888Api.SetAdcDither(_handle, dither ? 1 : 0);
            _ = Rx888Api.SetAdcRandom(_handle, random ? 1 : 0);
        }
    }

    private bool ConfigureDeviceLocked()
    {
        int adcFrequency = FsHz > 32_000_000 ? OverclockAdcFrequencyHz : NormalAdcFrequencyHz;
        if (Rx888Api.SetAdcFrequency(_handle, adcFrequency) < 0)
        {
            ReportError($"rx888_set_adc_frequency({adcFrequency}) に失敗しました。");
            return false;
        }
        if (Rx888Api.SetSampleRate(_handle, FsHz) < 0)
        {
            ReportError($"rx888_set_sample_rate({FsHz}) に失敗しました。");
            return false;
        }
        FreqChange();
        ApplyLnaAndNotch();
        GainChange();
        return true;
    }

    private void EventLoop(IntPtr sessionHandle)
    {
        Volatile.Write(ref _eventLoopThreadId, Environment.CurrentManagedThreadId);
        try
        {
            while (Volatile.Read(ref _isStreaming))
            {
                int result;
                try { result = Rx888Api.HandleEvents(sessionHandle); }
                catch
                {
                    if (Volatile.Read(ref _isStreaming)) DeviceRemoved?.Invoke();
                    break;
                }
                if (result < 0)
                {
                    if (Volatile.Read(ref _isStreaming)) DeviceRemoved?.Invoke();
                    break;
                }
                if (result == 0) Thread.Yield();
            }
        }
        finally
        {
            Volatile.Write(ref _eventLoopThreadId, 0);
        }
    }

    private void OnAsyncData(uint dataSize, IntPtr data, IntPtr context)
    {
        if (!Volatile.Read(ref _isStreaming) || dataSize > int.MaxValue) return;
        _sampleDispatcher.TryEnqueue(data, (int)dataSize);
    }

    private IntPtr OpenWithRetry(string imageFile)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr handle = Rx888Api.Open(0, imageFile);
            if (handle != IntPtr.Zero) return handle;
            if (attempt < 2) Thread.Sleep(120);
        }
        return IntPtr.Zero;
    }

    private void CloseHandleLocked()
    {
        if (_handle == IntPtr.Zero) return;
        try { Rx888Api.Close(_handle); }
        catch { }
        if (_callbackRoot.IsAllocated) _callbackRoot.Free();
        _handle = IntPtr.Zero;
        _currentRfMode = Rx888Api.RfMode.NoRfMode;
        ResetGainSettingCache();
    }

    private static string ResolveImageFilePath()
    {
        string? configured = Environment.GetEnvironmentVariable("SRDECK_RX888_IMAGE");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        string outputPath = Path.Combine(AppContext.BaseDirectory, "SDDC_FX3.img");
        if (File.Exists(outputPath)) return outputPath;
        string workingPath = Path.Combine(Environment.CurrentDirectory, "SDDC_FX3.img");
        return File.Exists(workingPath) ? workingPath : outputPath;
    }

    private static string GetBackendName(IntPtr handle)
    {
        try
        {
            string? value = Marshal.PtrToStringAnsi(Rx888Api.GetBackendName(handle));
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        }
        catch { return "unknown"; }
    }

    private static string GetDeviceSerialNumber()
    {
        IntPtr infos = IntPtr.Zero;
        try
        {
            int count = Rx888Api.GetDeviceInfo(out infos);
            if (count <= 0 || infos == IntPtr.Zero) return string.Empty;
            var info = Marshal.PtrToStructure<Rx888Api.Rx888DeviceInfo>(infos);
            string serial = (Marshal.PtrToStringAnsi(info.SerialNumber) ?? string.Empty).Trim();
            return string.Equals(serial, "TODO", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : serial;
        }
        catch { return string.Empty; }
        finally
        {
            if (infos != IntPtr.Zero)
            {
                try { Rx888Api.FreeDeviceInfo(infos); }
                catch { }
            }
        }
    }

    private static int GetExpectedCallbackLengthBytes()
    {
        const int fallbackComplexSamples = 32_768;
        string? text = Environment.GetEnvironmentVariable("SRDECK_SDDC_CALLBACK_COMPLEX");
        int complexSamples = int.TryParse(text, out int value)
            ? Math.Clamp(value, 1_024, 262_144)
            : fallbackComplexSamples;
        return complexSamples * 2 * sizeof(float);
    }

    internal static bool IsSupportedSampleRate(int sampleRateHz) =>
        Array.IndexOf(SupportedSampleRates, sampleRateHz) >= 0;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_isDisposed) return;
            _isDisposed = true;
        }
        _gainUpdateWorker.Dispose();
        Stop();
        _sampleDispatcher.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ReportError(string message)
    {
        Debug.WriteLine(message);
        if (!SuppressErrors) WeakReferenceMessenger.Default.Send(new SdrErrorMessage(message));
    }
}
