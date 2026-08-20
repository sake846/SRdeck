using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SRdeck.Models;
using SRdeck.Models.SDR;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;

namespace SRdeck.SDR;

public partial class SdrController : ISdrDevice, ISdrStreamingDiagnostics
{
    public SdrDeviceCapabilities Capabilities { get; } = new(SdrDeviceKind.SdrPlay);
    public int FsHz { get; set; }
    public long CenterFreqHz { get; set; }
    public int RfGainDb { get; set; }
    public bool RfAgcEnabled { get; set; }
    public float PpmAdjustment { get; set; }
    public float BiasPpm { get; set; }
    public int MaxGainReduction { get; private set; } = DefaultMaxGainReduction;
    public string ModelName { get; private set; } = "SDRplay";
    public string SerialNumber { get; private set; } = string.Empty;
    public int LnaState { get; set; } = 0;
    public int NotchFilterMode { get; set; } = 0; // 0: Off, 1: MW+FM, 2: DAB, 3: Both
    public bool BiasTEnabled { get; set; }
    public int AntennaIndex { get; set; }
    public int AmPortIndex { get; set; }
    public bool ExternalReferenceOutputEnabled { get; set; }
    public bool HdrEnabled { get; set; }
    public int HdrBandwidthIndex { get; set; }
    public SdrPlayDeviceFeatures DeviceFeatures => SdrPlayDeviceFeaturePolicy.GetFeatures(ModelName);

    private const float ExpectedApiVersion = SdrPlayApi.SDRPLAY_API_VERSION;
    private const float PpmScale = AppConstants.SDR_PPM_SCALE;
    private const int DefaultMaxGainReduction = SdrPlayApi.MAX_BB_GR;
    private const int RecoveryRetryDelayMs = 250;
    // At a 1 MHz stream, 16 typical callback blocks represent only a few
    // milliseconds. That is shorter than ordinary Windows scheduling pauses
    // and was dropping IQ blocks during otherwise healthy one-seg reception.
    private const int SampleQueueCapacity = 256;

    public event Action<short[], short[], uint>? SamplesReceived;
    public event Action<double, int>? GainHardwareChanged;
    public event Action? DeviceRemoved;
    public event Action? StreamStalled;

    private SdrPlayApi.DeviceT[] _devices = new SdrPlayApi.DeviceT[1];
    private nint _pdeviceParams = IntPtr.Zero;
    private nint _pdevParams = IntPtr.Zero;
    private nint _prxChannelParamsA = IntPtr.Zero;

    private SdrPlayApi.DeviceParamsT _deviceParams = default;
    private SdrPlayApi.DevParamsT _devParams = default;
    private SdrPlayApi.RxChannelParamsT _rxChannelParamsA = default;

    private SdrPlayApi.CallbackFnsT _cbFns;
    private bool _isStopping = false;
    private bool _isApiOpened = false;
    private bool _isDeviceSelected = false;
    private bool _isStreaming = false;
    private bool _isDisposed = false;
    private int _deviceRemovalCleanupPending;
    internal bool SuppressErrors { get; set; }
    private readonly object _lifecycleLock = new();
    private readonly object _streamCallbackLock = new();
    private Channel<QueuedSampleBlock>? _sampleQueue;
    private CancellationTokenSource? _sampleQueueCancellation;
    private Task? _sampleDispatchTask;
    private long _callbackCount;
    private long _droppedCallbackCount;
    private long _enqueuedSampleBlocks;
    private long _dequeuedSampleBlocks;

    private SdrPlayApi.StreamCallbackT? _streamACallback;
    private SdrPlayApi.StreamCallbackT? _streamBCallback;
    private SdrPlayApi.EventCallbackT? _eventCallback;

    public int QueuedSampleBlockCount => Math.Max(
        0,
        (int)Math.Min(
            int.MaxValue,
            Interlocked.Read(ref _enqueuedSampleBlocks) -
            Interlocked.Read(ref _dequeuedSampleBlocks)));

    public long CallbackCount => Interlocked.Read(ref _callbackCount);

    public long DroppedCallbackCount => Interlocked.Read(ref _droppedCallbackCount);

    private readonly record struct QueuedSampleBlock(
        short[] SamplesI,
        short[] SamplesQ,
        uint SampleCount);

    public SdrController(bool suppressErrors = false)
    {
        SuppressErrors = suppressErrors;
        _streamACallback = OnStreamACallback;
        _streamBCallback = OnStreamBCallback;
        _eventCallback = OnEventCallback;
    }

    public bool Open()
    {
        lock (_lifecycleLock)
        {
            if (_isDisposed) return false;
            if (!SdrPlayServiceRecovery.PrepareForApiOpen()) return false;
            return OpenWithRecovery();
        }
    }

    public bool Start()
    {
        lock (_lifecycleLock)
        {
            if (_isDisposed) return false;
            if (_isStreaming) return true;
            if (!SdrPlayServiceRecovery.PrepareForApiOpen()) return false;

            bool originalSuppressErrors = SuppressErrors;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                SuppressErrors = originalSuppressErrors || attempt == 0;
                bool opened = _isDeviceSelected || TryOpenDevice();
                if (opened)
                {
                    SyncDeviceParameters();
                    _isStreaming = StartStreaming();
                    if (_isStreaming)
                    {
                        StartStreamWatchdog();
                        SdrPlayDiagnosticLog.Write(
                            "stream-started",
                            $"model={ModelName} serial={SerialNumber} sampleRate={FsHz} frequency={CenterFreqHz} gr={RfGainDb} lna={LnaState}");
                        SuppressErrors = originalSuppressErrors;
                        return true;
                    }
                }

                ResetApiState(reportErrors: false);
                if (attempt == 0) Thread.Sleep(RecoveryRetryDelayMs);
            }

            SuppressErrors = originalSuppressErrors;
            return false;
        }
    }

    private bool OpenWithRecovery()
    {
        if (_isDeviceSelected) return true;

        bool originalSuppressErrors = SuppressErrors;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            SuppressErrors = originalSuppressErrors || attempt == 0;
            if (TryOpenDevice())
            {
                SuppressErrors = originalSuppressErrors;
                return true;
            }

            ResetApiState(reportErrors: false);
            if (attempt == 0) Thread.Sleep(RecoveryRetryDelayMs);
        }

        SuppressErrors = originalSuppressErrors;
        return false;
    }

    private bool TryOpenDevice()
    {
        if (Interlocked.Exchange(ref _deviceRemovalCleanupPending, 0) != 0)
        {
            ResetApiState(reportErrors: false);
        }

        _isDeviceSelected = InitializeSdrApi() && EnumerateSelectAndConfigureDevice();
        return _isDeviceSelected;
    }

    private bool InitializeSdrApi()
    {
        float apiVersion = 0f;
        if (!_isApiOpened)
        {
            SdrPlayApi.ErrT err = SdrPlayApi.sdrplay_api_Open();
            SdrPlayDiagnosticLog.Write("api-open", $"result={err}");
            if (err != SdrPlayApi.ErrT.Success)
            {
                HandleSdrError("SdrPlayApi.sdrplay_api_Open failed", err);
                SdrPlayApi.sdrplay_api_Close();
                _isApiOpened = false;
                return false;
            }
            _isApiOpened = true;
        }

        SdrPlayApi.ErrT errApiVersion = SdrPlayApi.sdrplay_api_ApiVersion(ref apiVersion);
        SdrPlayDiagnosticLog.Write(
            "api-version",
            $"result={errApiVersion} expected={ExpectedApiVersion:F2} actual={apiVersion:F2}");
        if (errApiVersion != SdrPlayApi.ErrT.Success)
        {
            HandleSdrError("SdrPlayApi.sdrplay_api_ApiVersion failed", errApiVersion);
            CleanupApiOnFailure();
            return false;
        }

        if (apiVersion != ExpectedApiVersion)
        {
            if (!SuppressErrors) WeakReferenceMessenger.Default.Send(new SdrErrorMessage($"API version mismatch: Local={ExpectedApiVersion}, DLL={apiVersion}"));
            CleanupApiOnFailure();
            return false;
        }

        return true;
    }

    private void CleanupApiOnFailure()
    {
        if (_isApiOpened)
        {
            SdrPlayApi.sdrplay_api_Close();
            _isApiOpened = false;
        }
    }

    private string GetModelName(byte hwVer) => hwVer switch
    {
        SdrPlayApi.SDRPLAY_RSP1_ID => "RSP1",
        SdrPlayApi.SDRPLAY_RSP1A_ID => "RSP1A",
        SdrPlayApi.SDRPLAY_RSP1B_ID => "RSP1B",
        SdrPlayApi.SDRPLAY_RSPduo_ID => "RSPduo",
        SdrPlayApi.SDRPLAY_RSP2_ID => "RSP2",
        SdrPlayApi.SDRPLAY_RSPdx_ID => "RSPdx",
        SdrPlayApi.SDRPLAY_RSPdxR2_ID => "RSPdxR2",
        _ => $"Unknown({hwVer})"
    };

    private void SyncDeviceInfoToUi()
    {
        SerialNumber = _devices[0].SerNo ?? string.Empty;
        WeakReferenceMessenger.Default.Send(new SdrDeviceInfoMessage(ModelName, SerialNumber, RfGainDb));
    }

    private bool EnumerateSelectAndConfigureDevice()
    {
        uint numDevices = 0;
        SdrPlayApi.ErrT lockErr = SdrPlayApi.sdrplay_api_LockDeviceApi();
        if (lockErr != SdrPlayApi.ErrT.Success)
        {
            HandleSdrError("SdrPlayApi.sdrplay_api_LockDeviceApi failed", lockErr);
            return false;
        }

        try
        {
            SdrPlayApi.ErrT getDevicesErr = SdrPlayApi.sdrplay_api_GetDevices(ref _devices[0], ref numDevices, 1u);
            if (getDevicesErr != SdrPlayApi.ErrT.Success)
            {
                HandleSdrError("SdrPlayApi.sdrplay_api_GetDevices failed", getDevicesErr);
                return false;
            }

            if (numDevices == 0)
            {
                if (!SuppressErrors) WeakReferenceMessenger.Default.Send(new SdrErrorMessage("SDRplayデバイスが見つかりません。接続を確認して再度「検出」を押してください。"));
                return false;
            }

            SdrPlayApi.ErrT selectErr = SdrPlayApi.sdrplay_api_SelectDevice(ref _devices[0]);
            if (selectErr != SdrPlayApi.ErrT.Success)
            {
                HandleSdrError("SdrPlayApi.sdrplay_api_SelectDevice failed", selectErr);
                return false;
            }
        }
        catch (Exception ex)
        {
            HandleSdrError($"SDRplayデバイス選択中に例外が発生しました: {ex.Message}", SdrPlayApi.ErrT.Fail);
            return false;
        }
        finally
        {
            SdrPlayApi.ErrT unlockErr = SdrPlayApi.sdrplay_api_UnlockDeviceApi();
            if (unlockErr != SdrPlayApi.ErrT.Success)
            {
                HandleSdrError("SdrPlayApi.sdrplay_api_UnlockDeviceApi failed", unlockErr);
            }
        }

        MaxGainReduction = DefaultMaxGainReduction;
        ModelName = GetModelName(_devices[0].HwVer);
        RfGainDb = 50;
        LnaState = 0;
        SyncDeviceInfoToUi();
        SdrPlayDiagnosticLog.Write(
            "device-selected",
            $"model={ModelName} serial={SerialNumber} hwVersion={_devices[0].HwVer} handle=0x{_devices[0].Dev:X}");
        return RefreshDeviceParameters();
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            StopStreaming(reportErrors: true);
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_isDisposed) return;
            ResetApiState(reportErrors: true);
            _isDisposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private bool StopStreaming(bool reportErrors)
    {
        StopStreamWatchdog();
        if (!_isStreaming)
        {
            StopSampleDispatcher();
            return true;
        }

        _isStopping = true;
        // Wait for an in-flight update to finish. New updates will observe
        // _isStopping and be skipped before entering the native API.
        lock (_apiUpdateLock)
        {
        }
        bool uninitCompleted = true;
        try
        {
            if (_devices[0].Dev != 0)
            {
                nint deviceHandle = _devices[0].Dev;
                Task<SdrPlayApi.ErrT> uninitTask = Task.Run(() =>
                    SdrPlayApi.sdrplay_api_Uninit(deviceHandle));
                try
                {
                    if (!uninitTask.Wait(TimeSpan.FromSeconds(3)))
                    {
                        uninitCompleted = false;
                        SdrPlayDiagnosticLog.Write("api-uninit", "result=timeout elapsedMs=3000");
                        Debug.Print("[SdrController] sdrplay_api_Uninit timed out after 3 s; continuing shutdown.");
                    }
                    else if (uninitTask.IsCompletedSuccessfully)
                    {
                        SdrPlayApi.ErrT err = uninitTask.Result;
                        SdrPlayDiagnosticLog.Write("api-uninit", $"result={err}");
                        // The API may already have stopped the stream (for
                        // example after a device-removal/USB fault) before the
                        // host reaches its normal stop path. Treat that
                        // idempotent state as a successful shutdown rather
                        // than surfacing a misleading error dialog.
                        if (err != SdrPlayApi.ErrT.Success &&
                            err != SdrPlayApi.ErrT.NotInitialised &&
                            reportErrors)
                        {
                            HandleSdrError("SdrPlayApi.sdrplay_api_Uninit failed", err);
                        }
                    }
                }
                catch (AggregateException ex)
                {
                    uninitCompleted = false;
                    Debug.Print($"[SdrController] sdrplay_api_Uninit threw: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }
        finally
        {
            _isStreaming = false;
            StopSampleDispatcher();
            _isStopping = false;
            SdrPlayDiagnosticLog.Write(
                "stream-stopped",
                $"callbacks={CallbackCount} dropped={DroppedCallbackCount} uninitCompleted={uninitCompleted}");
        }

        return uninitCompleted;
    }

    private void ResetApiState(bool reportErrors)
    {
        bool uninitOk = StopStreaming(reportErrors);

        if (_devices[0].Dev != 0 && uninitOk)
        {
            SdrPlayApi.ErrT lockErr = SdrPlayApi.sdrplay_api_LockDeviceApi();
            if (lockErr == SdrPlayApi.ErrT.Success)
            {
                try
                {
                    SdrPlayApi.ErrT releaseErr = SdrPlayApi.sdrplay_api_ReleaseDevice(ref _devices[0]);
                    if (releaseErr != SdrPlayApi.ErrT.Success && reportErrors)
                    {
                        HandleSdrError("SdrPlayApi.sdrplay_api_ReleaseDevice failed", releaseErr);
                    }
                }
                finally
                {
                    SdrPlayApi.ErrT unlockErr = SdrPlayApi.sdrplay_api_UnlockDeviceApi();
                    if (unlockErr != SdrPlayApi.ErrT.Success && reportErrors)
                    {
                        HandleSdrError("SdrPlayApi.sdrplay_api_UnlockDeviceApi failed", unlockErr);
                    }
                }
            }
            else if (reportErrors)
            {
                HandleSdrError("SdrPlayApi.sdrplay_api_LockDeviceApi failed during recovery", lockErr);
            }
        }

        if (_isApiOpened)
        {
            SdrPlayApi.ErrT closeErr = SdrPlayApi.sdrplay_api_Close();
            if (closeErr != SdrPlayApi.ErrT.Success && reportErrors)
            {
                HandleSdrError("SdrPlayApi.sdrplay_api_Close failed", closeErr);
            }
        }

        ClearLocalApiState();
    }

    private void ClearLocalApiState()
    {
        _isStreaming = false;
        _isDeviceSelected = false;
        _isApiOpened = false;
        _devices[0] = default;
        _pdeviceParams = IntPtr.Zero;
        _pdevParams = IntPtr.Zero;
        _prxChannelParamsA = IntPtr.Zero;
        _deviceParams = default;
        _devParams = default;
        _rxChannelParamsA = default;
    }
}
