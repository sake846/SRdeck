using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Messages;
using SRdeck.Models;
using SRdeck.Services;
using SRdeck.SDR;

namespace SRdeck.ViewModels
{
    public partial class SdrControlViewModel : ObservableObject
    {
        private readonly ISdrEngine _engine;
        private readonly IRadioSessionController _sessions;
        private readonly IDialogService _dialogService;
        private readonly Action<string> _setWindowTitle;
        private readonly TimeSpan _streamRecoveryDelay;
        private bool _isStarting;
        private int _streamRecoveryInProgress;
        private int _automaticStreamRecoveryAttempts;
        private const int MaxAutomaticStreamRecoveryAttempts = 2;

        public SdrControlViewModel(
            ISdrEngine engine,
            IRadioSessionController sessions,
            IDialogService dialogService,
            Action<string> setWindowTitle,
            TimeSpan? streamRecoveryDelay = null)
        {
            _engine = engine;
            _sessions = sessions;
            _sessions.SdrConfigurationChanged += HandleSdrConfigurationChanged;
            _sessions.PlaybackEnded += HandlePlaybackEnded;
            _dialogService = dialogService;
            _setWindowTitle = setWindowTitle;
            _streamRecoveryDelay = streamRecoveryDelay ?? TimeSpan.FromMilliseconds(750);
            _engine.DeviceRemoved += HandleDeviceRemoved;
            _engine.StreamStalled += HandleStreamStalled;
        }

        public ObservableCollection<FrequencyDisplayOption> FrequencyDisplayOptions { get; set; } = new();
        public ICommand? OpenStationCatalogCommand { get; set; }
        public ICommand? OpenBandPlanCatalogCommand { get; set; }
        internal Action<FrequencyDisplayOption?>? FrequencyDisplayOptionChanged { get; set; }

        [ObservableProperty]
        private FrequencyDisplayOption? _selectedFrequencyDisplayOption;

        partial void OnSelectedFrequencyDisplayOptionChanged(FrequencyDisplayOption? value) =>
            FrequencyDisplayOptionChanged?.Invoke(value);

        [ObservableProperty]
        private string _startButtonText = "開始";

        [ObservableProperty]
        private bool _isStarted = false;

        [ObservableProperty]
        private bool _isStopped = true;

        [ObservableProperty]
        private string _iqPlaybackButtonText = "IQ WAV を再生";

        [ObservableProperty]
        private string _iqPlaybackStatus = "IQ再生: 待機";

        [RelayCommand]
        public Task Start() => StartCore(resetRecoveryAttempts: true);

        private async Task StartCore(bool resetRecoveryAttempts)
        {
            if (_isStarting)
            {
                return;
            }

            _isStarting = true;
            try
            {
                if (resetRecoveryAttempts)
                {
                    Interlocked.Exchange(ref _automaticStreamRecoveryAttempts, 0);
                }

                // 状態設定のバックアップ
                bool prevStarted = IsStarted;
                bool prevStopped = IsStopped;

                IsStopped = false;
                IsStarted = true;

                RadioControl radioControl = _engine.Control;
                _engine.Control = radioControl;
                WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
                
                RadioSessionStartResult result = await _sessions.StartSdrAsync();
                if (!result.Success)
                {
                    // 失敗した場合は状態を戻す
                    IsStarted = prevStarted;
                    IsStopped = prevStopped;
                    StartButtonText = "開始";
                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        WeakReferenceMessenger.Default.Send(new SdrErrorMessage(result.Error));
                    }
                }
            }
            finally
            {
                _isStarting = false;
            }
        }

        private void HandleSdrConfigurationChanged(object? sender, EventArgs args)
        {
            void SyncState()
            {
                IsStarted = _engine.IsSdrRunning || _engine.IsPlaying;
                IsStopped = !IsStarted;
                StartButtonText = _engine.IsSdrRunning ? "動作中" : _engine.IsPlaying ? "再生中" : "開始";
                if (IsStopped) _setWindowTitle("SRdeck");
            }

            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                _ = dispatcher.InvokeAsync(SyncState);
            else
                SyncState();
        }

        [RelayCommand]
        public async Task IqPlayback()
        {
            if (_isStarting) return;
            if (_engine.IsPlaying)
            {
                await Stop();
                return;
            }

            IqPlaybackSelection? selection = _dialogService.ShowIqPlaybackDialog();
            if (selection is null) return;

            _isStarting = true;
            try
            {
                if (IsStarted || _engine.SessionState != InputSessionState.Stopped)
                    await Stop();

                RadioControl control = _engine.Control;
                control.CenterFreqHz = selection.CenterFrequencyHz;
                control.TunedFreqHz = selection.CenterFrequencyHz;
                control.FreqOffsetHz = 0;
                _engine.Control = control;

                RadioSessionStartResult result = await _sessions.StartPlaybackAsync(
                    selection.FilePath, selection.StartSeconds);
                if (!result.Success)
                {
                    ShowPlaybackError(result.Error ?? "ファイルのオープンに失敗しました。");
                    return;
                }

                StartButtonText = "再生中";
                IqPlaybackButtonText = "再生を停止";
                IqPlaybackStatus = $"IQ再生中: {Path.GetFileName(selection.FilePath)}";
                _setWindowTitle($"SRdeck [ {FormatFilePath(selection.FilePath)} ]");
                IsStarted = true;
                IsStopped = false;
                WeakReferenceMessenger.Default.Send(new ResetWaterfallTimingMessage());
                WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(_engine.Control));
            }
            finally
            {
                _isStarting = false;
            }
        }

        private void ShowPlaybackError(string message)
        {
            IsStarted = false;
            IsStopped = true;
            StartButtonText = "開始";
            IqPlaybackButtonText = "IQ WAV を再生";
            IqPlaybackStatus = "IQ再生: 開始失敗";
            _setWindowTitle("SRdeck");
            WeakReferenceMessenger.Default.Send(new SdrErrorMessage(message));
        }

        private void HandlePlaybackEnded(object? sender, EventArgs args)
        {
            void SyncState()
            {
                IsStarted = false;
                IsStopped = true;
                StartButtonText = "開始";
                IqPlaybackButtonText = "IQ WAV を再生";
                IqPlaybackStatus = "IQ再生: 終了";
                _setWindowTitle("SRdeck");
            }

            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                _ = dispatcher.InvokeAsync(SyncState);
            else
                SyncState();
        }

        private void HandleDeviceRemoved()
        {
            void StopOnUiThread()
            {
                WeakReferenceMessenger.Default.Send(new SdrErrorMessage(
                    "SDRplayデバイスとの接続が失われました。USB接続を確認してから再度開始してください。"));
                _ = Stop();
            }

            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                _ = dispatcher.InvokeAsync(StopOnUiThread);
                return;
            }

            StopOnUiThread();
        }

        private void HandleStreamStalled()
        {
            void RecoverOnUiThread()
            {
                _ = RecoverStreamAsync();
            }

            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                _ = dispatcher.InvokeAsync(RecoverOnUiThread);
                return;
            }

            RecoverOnUiThread();
        }

        private async Task RecoverStreamAsync()
        {
            if (Interlocked.Exchange(ref _streamRecoveryInProgress, 1) != 0)
            {
                return;
            }

            int attempt = Interlocked.Increment(ref _automaticStreamRecoveryAttempts);
            try
            {
                SdrPlayDiagnosticLog.Write(
                    "stream-recovery-start",
                    $"attempt={attempt} maxAttempts={MaxAutomaticStreamRecoveryAttempts}");

                await Stop();
                if (attempt > MaxAutomaticStreamRecoveryAttempts)
                {
                    SdrPlayDiagnosticLog.Write("stream-recovery-abandoned", $"attempt={attempt}");
                    string message = "SDRplayの受信停止が繰り返されたため、自動再接続を中止しました。\n" +
                        "USB接続を確認してから再度開始してください。";
                    if (SdrPlayDiagnosticLog.IsEnabled)
                    {
                        message += "\n診断ログ: %LOCALAPPDATA%\\SRdeck\\logs\\sdrplay-diagnostics.log";
                    }
                    WeakReferenceMessenger.Default.Send(new SdrErrorMessage(message));
                    return;
                }

                await Task.Delay(_streamRecoveryDelay);
                await StartCore(resetRecoveryAttempts: false);
                if (IsStarted)
                {
                    StartButtonText = "動作中";
                    SdrPlayDiagnosticLog.Write("stream-recovery-success", $"attempt={attempt}");
                }
                else
                {
                    SdrPlayDiagnosticLog.Write("stream-recovery-failed", $"attempt={attempt}");
                }
            }
            catch (Exception exception)
            {
                SdrPlayDiagnosticLog.Write("stream-recovery-error", exception.ToString());
                WeakReferenceMessenger.Default.Send(new SdrErrorMessage(
                    $"SDRplayの自動再接続に失敗しました。\n{exception.Message}"));
            }
            finally
            {
                Interlocked.Exchange(ref _streamRecoveryInProgress, 0);
            }
        }



        [RelayCommand]
        public async Task SdrToggle()
        {
            if (StartButtonText == "動作中")
            {
                await Stop();
                return;
            }

            if (IsStarted)
            {
                await Stop();
            }

            await Start();
            if (_engine.IsSdrRunning) StartButtonText = "動作中";
        }

        [RelayCommand]
        public async Task Stop()
        {
            bool wasPlaying = _engine.IsPlaying;
            IsStopped = true;
            IsStarted = false;
            _setWindowTitle("SRdeck");
            StartButtonText = "開始";
            
            RadioControl radioControl = _engine.Control;
            _engine.Control = radioControl;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
            
            await _sessions.StopAsync();
            if (wasPlaying)
            {
                IqPlaybackButtonText = "IQ WAV を再生";
                IqPlaybackStatus = "IQ再生: 停止";
            }
        }

        private string FormatFilePath(string filePath)
        {
            return filePath.Length > 200 ? "..." + filePath.Substring(filePath.Length - 197) : filePath;
        }
    }
}
