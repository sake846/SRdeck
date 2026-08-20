using System;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Models;
using SRdeck.Services.Plugins;

namespace SRdeck.ViewModels;

public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly ISdrEngine _engine;
    private readonly IPluginManager _pluginManager;
    private readonly IPluginIqDispatcher _pluginIqDispatcher;
    private const double DebugTextTop = 25.0;
    private const double DebugLineHeight = 12.5;

    public double DebugOverlayHeight { get; set; } = 300.0;

    [ObservableProperty] private Visibility _wfInfoVisible = Visibility.Hidden;
    [ObservableProperty] private string _wfInfoText = "";
    [ObservableProperty] private string _wfInfoTextRight = "";
    [ObservableProperty] private Visibility _debugTextVisible = Visibility.Hidden;
    [ObservableProperty] private string _debugText = "";
    [ObservableProperty] private double _timeProcCycle = 0.0;
    [ObservableProperty] private double _timeTotal = 0.0;
    [ObservableProperty] private double _timeMainFft = 0.0;
    [ObservableProperty] private double _fftFps = 0.0;
    [ObservableProperty] private double _wpfFps = 0.0;
    [ObservableProperty] private double _demodFps = 0.0;
    [ObservableProperty] private long _fftRequestCount = 0;
    [ObservableProperty] private long _fftCompletedCount = 0;
    [ObservableProperty] private long _fftDroppedCount = 0;
    [ObservableProperty] private long _fftLatestRequestId = 0;
    [ObservableProperty] private long _fftLatestCompletedId = 0;
    [ObservableProperty] private int _fftQueueDepth = 0;
    [ObservableProperty] private long _wpfFftFrameSerial = 0;
    [ObservableProperty] private long _wpfFftDroppedFrames = 0;
    [ObservableProperty] private double _inputLevelI = 0.0;
    [ObservableProperty] private double _inputLevelIDb = -100.0;
    [ObservableProperty] private double _inputLevelQ = 0.0;
    [ObservableProperty] private double _inputLevelQDb = -100.0;
    [ObservableProperty] private double _inputLevel = 0.0;
    [ObservableProperty] private double _inputLevelDb = -100.0;
    [ObservableProperty] private int _effectiveBitDepth = 16;
    [ObservableProperty] private double _invalidBitsWidth = 0.0;
    [ObservableProperty] private string _iqToolTip = "IQ Level";
    [ObservableProperty] private double _gpuAppUsagePercent = 0.0;
    [ObservableProperty] private double _gpuUsagePercent = 0.0;
    [ObservableProperty] private double _coreProcessingLoad = 0.0;
    [ObservableProperty] private double _pluginProcessingLoad = 0.0;

    private double _peakTimeMainFft = 0.0;
    private double _peakTimeCpuPrep = 0.0;
    private double _peakTimeCpuPost = 0.0;
    private double _peakTimeFftCore = 0.0;

    private double _peakTimeFftFullResCopy = 0.0;
    private double _peakTimeFftAggregate = 0.0;
    private double _peakTimeOsLag = 0.0;
    private double _peakTimeGpuPrep = 0.0;
    private double _peakTimeGpuUpload = 0.0;
    private double _peakTimeGpuShader = 0.0;
    private double _peakTimeGpuDownload = 0.0;
    private double _peakTimeGpuPost = 0.0;
    private double _peakTimeGpuPack = 0.0;
    private double _peakTimeGpuUploadNative = 0.0;
    private double _peakTimeGpuDispatch = 0.0;
    private double _peakTimeGpuReadback = 0.0;
    private double _peakTimeTot = 0.0;
    private long _lastPeakResetTicks = 0;

    public DiagnosticsViewModel(ISdrEngine engine, IPluginManager pluginManager, IPluginIqDispatcher pluginIqDispatcher)
    {
        _engine = engine;
        _pluginManager = pluginManager;
        _pluginIqDispatcher = pluginIqDispatcher;
    }

    public void SyncDiagnostics(RadioControl radioControl, string selectedLnaState)
    {
        var diagnostics = _engine.Diagnostics; 
        var radioState = _engine.State;

        // 10秒毎に最大値をリセット
        long nowTicks = Environment.TickCount64;
        if (_lastPeakResetTicks == 0) _lastPeakResetTicks = nowTicks; // 初回初期化
        if (nowTicks - _lastPeakResetTicks > 10000)
        {
            _peakTimeMainFft = 0;
            _peakTimeCpuPrep = 0;
            _peakTimeCpuPost = 0;
            _peakTimeFftCore = 0;

            _peakTimeFftFullResCopy = 0;
            _peakTimeFftAggregate = 0;
            _peakTimeOsLag = 0;
            _peakTimeGpuPrep = 0;
            _peakTimeGpuUpload = 0;
            _peakTimeGpuShader = 0;
            _peakTimeGpuDownload = 0;
            _peakTimeGpuPost = 0;
            _peakTimeGpuPack = 0;
            _peakTimeGpuUploadNative = 0;
            _peakTimeGpuDispatch = 0;
            _peakTimeGpuReadback = 0;
            _peakTimeTot = 0;
            _lastPeakResetTicks = nowTicks;
        }

        TimeProcCycle = diagnostics.TimeProcCycle;
        TimeTotal = diagnostics.TimeTotal;
        TimeMainFft = diagnostics.TimeMainFft;
        string? pluginId = _pluginManager.ActivePluginId;
        PluginIqDispatchSnapshot plugin = pluginId is null ? default : _pluginIqDispatcher.GetSnapshot(pluginId);
        // The core row is the critical path across the core processing cycle,
        // asynchronous FFT, and the sequential spectrum/waterfall UI render.
        // Using the longest lane avoids double-counting work that runs in parallel.
        double coreCriticalPathMs = Math.Max(
            diagnostics.TimeProcCycle,
            Math.Max(diagnostics.TimeMainFft,
                diagnostics.TimeWpfSpectrum + diagnostics.TimeWpfWaterfall));

        // Both PRC rows use the duration of the same 100 ms IQ block as their
        // denominator. The fallback is the engine's fixed Fs/10 cycle before
        // the dispatcher has received its first block.
        double blockDurationMs = plugin.CurrentBlockDurationMs > 0 ? plugin.CurrentBlockDurationMs : 100.0;
        CoreProcessingLoad = Math.Clamp(coreCriticalPathMs * 100.0 / blockDurationMs, 0, 100);
        double pluginInstantLoad = plugin.CurrentProcessingTimeMs * 100.0 / blockDurationMs;
        PluginProcessingLoad = Math.Clamp(pluginInstantLoad, 0, 100);
        FftFps = diagnostics.FftFps;
        WpfFps = diagnostics.WpfFps;
        DemodFps = diagnostics.DemodFps;
        FftRequestCount = diagnostics.FftRequestCount;
        FftCompletedCount = diagnostics.FftCompletedCount;
        FftDroppedCount = diagnostics.FftDroppedCount;
        FftLatestRequestId = diagnostics.FftLatestRequestId;
        FftLatestCompletedId = diagnostics.FftLatestCompletedId;
        FftQueueDepth = diagnostics.FftQueueDepth;
        WpfFftFrameSerial = diagnostics.WpfFftFrameSerial;
        WpfFftDroppedFrames = diagnostics.WpfFftDroppedFrames;
        GpuAppUsagePercent = diagnostics.GpuAppUsagePercent;
        GpuUsagePercent = diagnostics.GpuUsagePercent;
        int maxAbsI = Math.Max(Math.Abs((int)diagnostics.BufferIMaxValue), Math.Abs((int)diagnostics.BufferIMinValue));
        int maxAbsQ = Math.Max(Math.Abs((int)diagnostics.BufferQMaxValue), Math.Abs((int)diagnostics.BufferQMinValue));
        int maxAbs = Math.Max(maxAbsI, maxAbsQ);

        double decibelsI = -100.0;
        if (maxAbsI > 0)
        {
            decibelsI = 20.0 * Math.Log10(maxAbsI / 32768.0);
        }

        double decibelsQ = -100.0;
        if (maxAbsQ > 0)
        {
            decibelsQ = 20.0 * Math.Log10(maxAbsQ / 32768.0);
        }

        double decibels = -100.0;
        if (maxAbs > 0)
        {
            decibels = 20.0 * Math.Log10(maxAbs / 32768.0);
        }
        InputLevelI = Math.Clamp((decibelsI + 96.0) / 96.0 * 80.0, 0.0, 80.0);
        InputLevelIDb = decibelsI;
        InputLevelQ = Math.Clamp((decibelsQ + 96.0) / 96.0 * 80.0, 0.0, 80.0);
        InputLevelQDb = decibelsQ;
        InputLevel = Math.Clamp((decibels + 96.0) / 96.0 * 80.0, 0.0, 80.0);
        InputLevelDb = decibels;

        var (bitDepth, bitDepthDesc) = GetEffectiveBitDepth(_engine.SdrDevice, radioControl.FsHz);
        EffectiveBitDepth = bitDepth;
        InvalidBitsWidth = Math.Clamp((16.0 - bitDepth) / 16.0 * 80.0, 0.0, 80.0);
        double sampleRateMsps = (radioControl.FsHz > 0 ? radioControl.FsHz : AppConstants.FULL_BW) / 1_000_000.0;
        IqToolTip = $"IQ Level [{bitDepthDesc} @ {sampleRateMsps:F1} MSps]";

        if (!radioControl.IsDebugVisible)
        {
            DebugTextVisible = Visibility.Collapsed;
            WfInfoText = "";
            WfInfoTextRight = "";
            return;
        }
        DebugTextVisible = Visibility.Visible;
        DebugText = $"rfAgc:{_engine.RfAgcEnabled} sqDb:{radioControl.SquelchDb}\nsqOpen:{radioState.IsSquelchOpen} rxRssi:{radioState.RxRssi:0.0}\nsysDb:{_engine.SystemDb:0.0}";

        var leftTextBuilder = new StringBuilder();
        var rightTextBuilder = new StringBuilder();
        leftTextBuilder.AppendLine($"System Level    {radioControl.SystemDb - _engine.SystemGainOffset,5:0} dB");
        leftTextBuilder.AppendLine($"Gain Reduction  {diagnostics.GainReductionDb,5:0} dB");
        leftTextBuilder.AppendLine($"Lna State       {selectedLnaState,5}");
        leftTextBuilder.AppendLine();
        leftTextBuilder.AppendLine($"I Min / Max  {diagnostics.BufferIMinValue,7:N0} / {diagnostics.BufferIMaxValue,7:N0}  {GetAdcBar(diagnostics.BufferIMinValue, diagnostics.BufferIMaxValue)}");
        leftTextBuilder.AppendLine($"Q Min / Max  {diagnostics.BufferQMinValue,7:N0} / {diagnostics.BufferQMaxValue,7:N0}  {GetAdcBar(diagnostics.BufferQMinValue, diagnostics.BufferQMaxValue)}");
        leftTextBuilder.AppendLine();
        leftTextBuilder.AppendLine($"MaxPwr    {radioState.MaxDb,6:0.0} dB");
        leftTextBuilder.AppendLine($"MinPwr    {radioState.MinFftPwr,6:0.0} dB");
        leftTextBuilder.AppendLine($"SmMinPwr  {radioState.Min2FftPwr,6:0.0} dB  [{radioState.MinFftScanMinHz:N0} - {radioState.MinFftScanMaxHz:N0} Hz]");
        leftTextBuilder.AppendLine();
        leftTextBuilder.AppendLine($"CtrHz   {radioControl.CenterFreqHz,11:N0} Hz");
        leftTextBuilder.AppendLine($"R1Hz    {radioControl.TunedFreqHz,11:N0} Hz (Offset: {radioControl.FreqOffsetHz,10:N0} Hz)");
        leftTextBuilder.AppendLine($"CurHz   {radioControl.CursorFreqHz,11:N0} Hz");
        leftTextBuilder.AppendLine($"StepHz  {radioControl.StepHz,11:N0} Hz");
        leftTextBuilder.AppendLine($"SpanHz  {radioControl.SpanHz,11:N0} Hz");
        leftTextBuilder.AppendLine();
        
        _peakTimeMainFft = Math.Max(_peakTimeMainFft, diagnostics.TimeMainFft);
        _peakTimeCpuPrep = Math.Max(_peakTimeCpuPrep, diagnostics.TimeCpuPrep);
        _peakTimeCpuPost = Math.Max(_peakTimeCpuPost, diagnostics.TimeCpuPost);
        _peakTimeFftCore = Math.Max(_peakTimeFftCore, diagnostics.TimeFftCore);

        _peakTimeFftFullResCopy = Math.Max(_peakTimeFftFullResCopy, diagnostics.TimeFftFullResCopy);
        _peakTimeFftAggregate = Math.Max(_peakTimeFftAggregate, diagnostics.TimeFftAggregate);
        _peakTimeOsLag = Math.Max(_peakTimeOsLag, diagnostics.TimeOsLag);
        _peakTimeGpuPrep = Math.Max(_peakTimeGpuPrep, diagnostics.TimeGpuPrep);
        _peakTimeGpuUpload = Math.Max(_peakTimeGpuUpload, diagnostics.TimeGpuUpload);
        _peakTimeGpuShader = Math.Max(_peakTimeGpuShader, diagnostics.TimeGpuShader);
        _peakTimeGpuDownload = Math.Max(_peakTimeGpuDownload, diagnostics.TimeGpuDownload);
        _peakTimeGpuPost = Math.Max(_peakTimeGpuPost, diagnostics.TimeGpuPost);
        _peakTimeGpuPack = Math.Max(_peakTimeGpuPack, diagnostics.TimeGpuPack);
        _peakTimeGpuUploadNative = Math.Max(_peakTimeGpuUploadNative, diagnostics.TimeGpuUploadNative);
        _peakTimeGpuDispatch = Math.Max(_peakTimeGpuDispatch, diagnostics.TimeGpuDispatch);
        _peakTimeGpuReadback = Math.Max(_peakTimeGpuReadback, diagnostics.TimeGpuReadback);
        _peakTimeTot = Math.Max(_peakTimeTot, diagnostics.TimeTotal);

        // FFTの表示開始位置を 0 とする
        double startFftMs = 0;
        leftTextBuilder.AppendLine($"{"TimeFFT",-12} {diagnostics.TimeMainFft,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeMainFft)} [Pk:{_peakTimeMainFft,5:0.0}]");

        double currentSubMs = startFftMs;
        leftTextBuilder.AppendLine($"{"  -FFT Core",-12} {diagnostics.TimeFftCore,5:0.0} ms  {GetBar(currentSubMs, diagnostics.TimeFftCore, '*')} [Pk:{_peakTimeFftCore,5:0.0}]");
        currentSubMs += diagnostics.TimeFftCore;

        leftTextBuilder.AppendLine($"{"  -FullCopy",-12} {diagnostics.TimeFftFullResCopy,5:0.0} ms  {GetBar(currentSubMs, diagnostics.TimeFftFullResCopy, '*')} [Pk:{_peakTimeFftFullResCopy,5:0.0}]");
        currentSubMs += diagnostics.TimeFftFullResCopy;

        leftTextBuilder.AppendLine($"{"  -Aggregate",-12} {diagnostics.TimeFftAggregate,5:0.0} ms  {GetBar(currentSubMs, diagnostics.TimeFftAggregate, '*')} [Pk:{_peakTimeFftAggregate,5:0.0}]");
        currentSubMs += diagnostics.TimeFftAggregate;
        
        // Legacy detail values. CPU Prep is included in FFT Core; CPU Post is FullCopy + Aggregate.
        leftTextBuilder.AppendLine($"{"  -CPU Prep",-12} {diagnostics.TimeCpuPrep,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeCpuPrep, '*')} [Pk:{_peakTimeCpuPrep,5:0.0}]");

        leftTextBuilder.AppendLine($"{"  -GPU Prep",-12} {diagnostics.TimeGpuPrep,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeGpuPrep, '*')} [Pk:{_peakTimeGpuPrep,5:0.0}]");
        leftTextBuilder.AppendLine($"{"  -GPU Upld",-12} {diagnostics.TimeGpuUpload,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeGpuUpload, '*')} [Pk:{_peakTimeGpuUpload,5:0.0}]");
        leftTextBuilder.AppendLine($"{"  -GPU Shdr",-12} {diagnostics.TimeGpuShader,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeGpuShader, '*')} [Pk:{_peakTimeGpuShader,5:0.0}]");
        leftTextBuilder.AppendLine($"{"    -Pack",-12} {diagnostics.TimeGpuPack,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeGpuPack, '*')} [Pk:{_peakTimeGpuPack,5:0.0}]");
        leftTextBuilder.AppendLine($"{"    -Upload",-12} {diagnostics.TimeGpuUploadNative,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeGpuUploadNative, '*')} [Pk:{_peakTimeGpuUploadNative,5:0.0}]");
        leftTextBuilder.AppendLine($"{"    -Dispat",-12} {diagnostics.TimeGpuDispatch,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeGpuDispatch, '*')} [Pk:{_peakTimeGpuDispatch,5:0.0}]");
        leftTextBuilder.AppendLine($"{"    -Readbk",-12} {diagnostics.TimeGpuReadback,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeGpuReadback, '*')} [Pk:{_peakTimeGpuReadback,5:0.0}]");
        leftTextBuilder.AppendLine($"{"  -GPU Dnld",-12} {diagnostics.TimeGpuDownload,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeGpuDownload, '*')} [Pk:{_peakTimeGpuDownload,5:0.0}]");
        leftTextBuilder.AppendLine($"{"  -GPU Post",-12} {diagnostics.TimeGpuPost,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeGpuPost, '*')} [Pk:{_peakTimeGpuPost,5:0.0}]");
        
        leftTextBuilder.AppendLine($"{"  -CPU Post",-12} {diagnostics.TimeCpuPost,5:0.0} ms  {GetBar(startFftMs, diagnostics.TimeCpuPost, '*')} [Pk:{_peakTimeCpuPost,5:0.0}]");

        leftTextBuilder.AppendLine($"{"TimeTot",-12} {diagnostics.TimeTotal,5:0.0} ms  {GetBar(0, diagnostics.TimeTotal)} [Pk:{_peakTimeTot,5:0.0}]");
        leftTextBuilder.AppendLine($"{"  -OS Lag",-12} {diagnostics.TimeOsLag,5:0.0} ms  {GetBar(0, diagnostics.TimeOsLag, '*')} [Pk:{_peakTimeOsLag,5:0.0}]");
        leftTextBuilder.AppendLine($"{"Offset",-12}       ms  +---------+---------+---------+---------+");
        leftTextBuilder.AppendLine($"                       0        25        50        75      100ms");
        leftTextBuilder.AppendLine();
        leftTextBuilder.AppendLine($"{"AudGap",-12} {diagnostics.AudioWriteIntervalMs,5:0.0} ms");
        leftTextBuilder.AppendLine();
        double samplesPerMs = Math.Max(1.0, (radioControl.FsHz > 0 ? radioControl.FsHz : AppConstants.FULL_BW) / 1000.0);
        rightTextBuilder.AppendLine($"[FFT Frames]");
        rightTextBuilder.AppendLine($"Req : {diagnostics.FftRequestCount,11:N0}");
        rightTextBuilder.AppendLine($"Done: {diagnostics.FftCompletedCount,11:N0}  Q:{diagnostics.FftQueueDepth}");
        rightTextBuilder.AppendLine($"Drop: {diagnostics.FftDroppedCount,11:N0}  WPF:{diagnostics.WpfFftDroppedFrames:N0}");
        rightTextBuilder.AppendLine($"Seq : Rq:{diagnostics.FftLatestRequestId:N0} Dn:{diagnostics.FftLatestCompletedId:N0} Dr:{diagnostics.WpfFftFrameSerial:N0}");
        rightTextBuilder.AppendLine();
        rightTextBuilder.AppendLine($"[Buffer Pointers]");
        rightTextBuilder.AppendLine($"WP: {diagnostics.BufferWPtr,11:N0} ({diagnostics.BufferWPtr / samplesPerMs,9:F1} ms)");
        rightTextBuilder.AppendLine($"RP: {diagnostics.BufferRPtr,11:N0} ({diagnostics.BufferRPtr / samplesPerMs,9:F1} ms)");
        rightTextBuilder.AppendLine($"DF: {diagnostics.BufferPtrDiff,11:N0} ({diagnostics.BufferPtrDiff / samplesPerMs,9:F1} ms)");
        rightTextBuilder.AppendLine();
        rightTextBuilder.AppendLine("[SDR Stream]");
        rightTextBuilder.AppendLine($"Cb  : {diagnostics.SdrCallbackCount,11:N0}  Q:{diagnostics.SdrQueuedSampleBlockCount}");
        rightTextBuilder.AppendLine($"Drop: {diagnostics.SdrDroppedCallbackCount,11:N0}");
        rightTextBuilder.AppendLine($"Age : {diagnostics.SdrLastCallbackAgeSeconds,11:F3} sec");
        rightTextBuilder.AppendLine();
        rightTextBuilder.AppendLine($"{"FpsFFT",-12} {diagnostics.FftFps,5:0.0} fps");
        rightTextBuilder.AppendLine($"{"FpsWPF",-12} {diagnostics.WpfFps,5:0.0} fps");
        rightTextBuilder.AppendLine($"{"FpsDm",-12} {diagnostics.DemodFps,5:0.0} fps");

        rightTextBuilder.AppendLine($"{"GpuUse",-12} {diagnostics.GpuAppUsagePercent,5:0.0} / {diagnostics.GpuUsagePercent,5:0.0} % (App/Total)");
        rightTextBuilder.AppendLine($"{"WpfSp",-12} {diagnostics.TimeWpfSpectrum,5:0.0} ms");
        rightTextBuilder.AppendLine($"  P/L/D/U    {diagnostics.TimeWpfSpectrumPrepare:0.0}/{diagnostics.TimeWpfSpectrumLock:0.0}/{diagnostics.TimeWpfSpectrumDraw:0.0}/{diagnostics.TimeWpfSpectrumUnlock:0.0} ms");
        rightTextBuilder.AppendLine($"{"WpfWf",-12} {diagnostics.TimeWpfWaterfall,5:0.0} ms");
        rightTextBuilder.AppendLine($"{"WpfZm",-12} {diagnostics.TimeWpfZoom,5:0.0} ms");
        rightTextBuilder.AppendLine($"{"WpfDm",-12} {diagnostics.TimeWpfDemod,5:0.0} ms");
        rightTextBuilder.AppendLine($"{"FsEff",-12} {(diagnostics.EffectiveSampleRateHz / 1_000_000.0),5:0.000} Msps");

        int visibleLines = Math.Max(1, (int)Math.Floor((DebugOverlayHeight - DebugTextTop) / DebugLineHeight));
        WfInfoText = LimitLines(leftTextBuilder, visibleLines);
        WfInfoTextRight = LimitLines(rightTextBuilder, visibleLines);
    }

    private static string LimitLines(StringBuilder text, int maxLines)
    {
        if (maxLines <= 0 || text.Length == 0) return string.Empty;
        int lineCount = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lineCount++;
            if (lineCount >= maxLines) return text.ToString(0, i + 1);
        }
        return text.ToString();
    }

    private string GetBar(double startMs, double durationMs, char fillChar = '#')
    {
        const int MAX_CHARS = 40; const double MS_PER_CHAR = 2.5;
        var barBuilder = new StringBuilder(); barBuilder.Append("[");
        int startIndex = (int)Math.Max(0, Math.Round(startMs / MS_PER_CHAR)); int durationIndex = (int)Math.Max(0, Math.Round(durationMs / MS_PER_CHAR));
        for (int i = 0; i < MAX_CHARS; i++) { if (i >= startIndex && i < startIndex + durationIndex) barBuilder.Append(fillChar); else barBuilder.Append('-'); }
        barBuilder.Append("]"); return barBuilder.ToString();
    }

    private string GetAdcBar(short minValue, short maxValue)
    {
        const int MAX_CHARS = 40; const int CENTER = 20; const double SCALE = 32768.0;
        int minOffset = (int)Math.Max(-CENTER, Math.Min(CENTER, Math.Round((minValue / SCALE) * CENTER)));
        int maxOffset = (int)Math.Max(-CENTER, Math.Min(CENTER, Math.Round((maxValue / SCALE) * CENTER)));
        var barBuilder = new StringBuilder(); barBuilder.Append("[");
        for (int i = 0; i < MAX_CHARS; i++) { int relativePosition = i - CENTER; bool isFilled = (relativePosition >= minOffset && relativePosition <= maxOffset); if (i == CENTER) barBuilder.Append('|'); else if (isFilled) barBuilder.Append('#'); else barBuilder.Append('-'); }
        barBuilder.Append("]"); return barBuilder.ToString();
    }

    private string GetBufBar(double valueMs)
    {
        const int MAX_CHARS = 40; const double FULL_SCALE_MS = 500.0;
        int filledCount = (int)Math.Max(0, Math.Min(MAX_CHARS, Math.Round(valueMs / (FULL_SCALE_MS / MAX_CHARS))));
        var barBuilder = new StringBuilder(); barBuilder.Append("[");
        for (int i = 0; i < MAX_CHARS; i++) { if (i < filledCount) barBuilder.Append('#'); else barBuilder.Append('-'); }
        barBuilder.Append("]"); return barBuilder.ToString();
    }

    private static (int bitDepth, string description) GetEffectiveBitDepth(ISdrDevice? sdrDevice, int fsHz)
    {
        if (sdrDevice == null)
        {
            return (16, "16-bit");
        }

        if (sdrDevice.Capabilities.IsRtlSdr)
        {
            return (8, "8-bit ADC (RTL-SDR)");
        }

        if (sdrDevice is SRdeck.SDR.SdrController sdrPlayDevice)
        {
            string model = sdrPlayDevice.ModelName;
            int sampleRate = fsHz > 0 ? fsHz : sdrPlayDevice.FsHz;

            if (sdrPlayDevice.HdrEnabled && sampleRate <= 2_000_000)
            {
                return (14, $"14-bit ADC ({model} HDR)");
            }

            if (sampleRate < 2_000_000)
            {
                return (14, $"14-bit ADC ({model})");
            }
            else if (sampleRate < 6_000_000)
            {
                return (12, $"12-bit ADC ({model})");
            }
            else if (sampleRate <= 8_000_000)
            {
                return (10, $"10-bit ADC ({model})");
            }
            else
            {
                return (8, $"8-bit ADC ({model})");
            }
        }

        return (16, "16-bit");
    }

}
