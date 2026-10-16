using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.IO;
using SRdeck.Models;
using SRdeck.Models.Configuration;
using SRdeck.Renderers;
using SRdeck.Messages;
using SRdeck.Configuration;

namespace SRdeck.ViewModels;

/// <summary>
/// MainViewModel のコマンド（全般操作・ダイアログ表示など）を定義する部分クラスです。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [RelayCommand]
    private void Exit()
    {
        Application.Current.MainWindow.Close();
    }

    private SRdeck.Views.SettingsDialog? _settingsDialog;

    [RelayCommand]
    private void OpenSettingsDialog()
    {
        ApplyFftResolutionLimit();
        if (_settingsDialog != null && _settingsDialog.IsLoaded)
        {
            _settingsDialog.Activate();
            return;
        }

        SdrDeviceKind? connectedDeviceKind = null;
        if (IsSdrDetected && _engine?.SdrDevice != null)
        {
            connectedDeviceKind = _engine.SdrDevice.Capabilities.Kind;
        }
        else if (IsSdrDetected)
        {
            connectedDeviceKind = IsRtlDevice ? SdrDeviceKind.RtlSdr : SdrDeviceKind.SdrPlay;
        }

        _settingsDialog = new SRdeck.Views.SettingsDialog(connectedDeviceKind);
        
        var mainWin = Application.Current.MainWindow;
        if (mainWin != null && mainWin != _settingsDialog)
        {
            _settingsDialog.Owner = mainWin;
        }
        
        _settingsDialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _settingsDialog.DataContext = this;
        _settingsDialog.Closed += (sender, eventArgs) => _settingsDialog = null;
        _settingsDialog.Show();
    }

    [RelayCommand]
    private void ResetSettings()
    {
        SettingsResetConfirmTitle = Application.Current.TryFindResource("Header_ResetState") as string ?? "ラストステートのリセット";
        SettingsResetConfirmMessage = Application.Current.TryFindResource("Msg_ResetState") as string ?? "現在の動作状態（周波数、モード、ズーム等）を初期状態に戻します。";
        SettingsResetConfirmOkCommand = ExecuteResetSettingsCommand;
        SettingsResetConfirmVisibility = Visibility.Visible;
    }

    [RelayCommand]
    private void ResetAppSettings()
    {
        SettingsResetConfirmTitle = Application.Current.TryFindResource("Header_ResetApp") as string ?? "基本設定のリセット";
        SettingsResetConfirmMessage = Application.Current.TryFindResource("Msg_ResetApp") as string ?? "アプリの共通設定（appsettings.json）を初期状態に戻します。\n(Webサーバーのポート設定等が含まれます)";
        SettingsResetConfirmOkCommand = ExecuteResetAppSettingsCommand;
        SettingsResetConfirmVisibility = Visibility.Visible;
    }

    [RelayCommand]
    private void ResetHardwareSettings()
    {
        SettingsResetConfirmTitle = Application.Current.TryFindResource("Header_ResetHardware") as string ?? "ハードウェア設定のリセット";
        SettingsResetConfirmMessage = Application.Current.TryFindResource("Msg_ResetHardware") as string ?? "ハードウェア関連の設定（hardware.json）を初期状態に戻します。\n(SDRデバイスのゲイン範囲や補正値が含まれます)";
        SettingsResetConfirmOkCommand = ExecuteResetHardwareSettingsCommand;
        SettingsResetConfirmVisibility = Visibility.Visible;
    }

    [RelayCommand]
    private void ResetAll()
    {
        SettingsResetConfirmTitle = Application.Current.TryFindResource("Header_ResetAll") as string ?? "工場出荷状態にリセット";
        SettingsResetConfirmMessage = Application.Current.TryFindResource("Msg_ResetAll") as string ?? "すべての設定を完全に削除し、初期状態に戻します。\n(この操作は取り消せません)";
        SettingsResetConfirmOkCommand = ExecuteResetAllCommand;
        SettingsResetConfirmVisibility = Visibility.Visible;
    }

    [RelayCommand]
    private async Task ExecuteResetAppSettings()
    {
        SettingsResetConfirmVisibility = Visibility.Collapsed;
        _settingsService.BackupSettings();
        _settingsService.SaveSettings(new AppSettings());
        
        CommonOverlayTitle = Application.Current.TryFindResource("Title_ResetComplete") as string ?? "リセット完了";
        CommonOverlayMessageText = Application.Current.TryFindResource("Msg_ResetCompleteApp") as string ?? "基本設定をリセットしました。";
        CommonOverlayVisibility = Visibility.Visible;
    }

    [RelayCommand]
    private async Task ExecuteResetHardwareSettings()
    {
        SettingsResetConfirmVisibility = Visibility.Collapsed;
        _settingsService.BackupHardwareSettings();
        _settingsService.SaveHardwareSettings(new HardwareSettings(), GetEffectiveHardwareSettingsDeviceType());

        CommonOverlayTitle = Application.Current.TryFindResource("Title_ResetComplete") as string ?? "リセット完了";
        CommonOverlayMessageText = Application.Current.TryFindResource("Msg_ResetCompleteHw") as string ?? "ハードウェア設定をリセットしました。";
        CommonOverlayVisibility = Visibility.Visible;
    }

    [RelayCommand]
    private async Task ExecuteResetAll()
    {
        SettingsResetConfirmVisibility = Visibility.Collapsed;
        
        // すべてバックアップして削除/初期化
        _statePersistence.Backup();
        _settingsService.BackupSettings();
        _settingsService.BackupHardwareSettings();

        // LastState
        _statePersistence.Reset();
        
        // AppSettings & Hardware
        _settingsService.SaveSettings(new AppSettings());
        _settingsService.SaveHardwareSettings(new HardwareSettings(), GetEffectiveHardwareSettingsDeviceType());

        // 即座に反映
        await ExecuteResetSettings(); // これでUIに反映
        
        CommonOverlayTitle = Application.Current.TryFindResource("Title_ResetComplete") as string ?? "リセット完了";
        CommonOverlayMessageText = Application.Current.TryFindResource("Msg_ResetCompleteAll") as string ?? "すべての設定をリセットしました。";
        CommonOverlayVisibility = Visibility.Visible;
    }

    [RelayCommand]
    private async Task ExecuteResetSettings()
    {
        SettingsResetConfirmVisibility = Visibility.Collapsed;

        // リセット前にバックアップを作成
        _statePersistence.Backup();


        // LastState をリセット
        _statePersistence.Reset();

        // 現在の状態に即座に反映
        // RadioParameters (radioControl) を構築
        RadioControl radioControl = _engine.Control;
        
        radioControl.CenterFreqHz = _statePersistence.State.CenterFreqHz;
        radioControl.TunedFreqHz = _statePersistence.State.TunedFreqHz;
        radioControl.DemodMode = _statePersistence.State.DemodMode;
        radioControl.StepHz = _statePersistence.State.StepHz;
        radioControl.WaterfallColorMode = _statePersistence.State.WaterfallColorMode;
        radioControl.IsR1Visible = true;
        radioControl.IsPowerOn = _statePersistence.State.IsPowerOn;
        radioControl.IsSpeakerOn = _statePersistence.State.IsSpeakerOn;
        radioControl.IsSquelchOn = _statePersistence.State.IsSquelchOn;
        radioControl.SquelchDb = _statePersistence.State.SquelchDb;
        radioControl.IsZoomWindowVisible = _statePersistence.State.IsZoomWindowVisible;
        radioControl.SpanHz = _statePersistence.State.SpanHz;
        radioControl.IsAfcEnabled = false;
        radioControl.IsMonoMode = false;
        radioControl.IsBandPlanVisible = false;
        radioControl.IsStationNameVisible = false;

        _engine.Control = radioControl;

        // 表示バイアス等のリセット
        Display.SpectrumBiasAdj = _statePersistence.State.SpectrumBiasAdj;
        Display.WaterfallBiasAdj = _statePersistence.State.WaterfallBiasAdj;
        Display.SpectrumZoomBiasAdj = _statePersistence.State.SpectrumZoomBiasAdj;
        Display.WaterfallZoomBiasAdj = _statePersistence.State.WaterfallZoomBiasAdj;
        
        IsGpuFftEnabled = _engine.InitialAppSettings.Display.IsGpuFftEnabled;
        FftResolutionMode = _engine.InitialAppSettings.Display.FftResolutionMode;
        
        // UIプロパティへの同期
        ApplyFrequencyDisplayMode(FrequencyDisplayMode.None);
        IsReceiver1Visible = radioControl.IsR1Visible;

        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        WeakReferenceMessenger.Default.Send(new BiasUpdateMessage(Display.SpectrumBiasAdj, Display.WaterfallBiasAdj, Display.SpectrumZoomBiasAdj, Display.WaterfallZoomBiasAdj));
        
        CommonOverlayTitle = Application.Current.TryFindResource("Title_ResetComplete") as string ?? "設定リセット";
        CommonOverlayMessageText = Application.Current.TryFindResource("Msg_ResetCompleteAll") as string ?? "設定をリセットしました。";
        CommonOverlayVisibility = Visibility.Visible;
    }

    // --- UI Collections & Options ---
    public ObservableCollection<StepOption> StepOptions { get; } = GetDefaultStepOptions();

    private static ObservableCollection<StepOption> GetDefaultStepOptions()
    {
        var options = new ObservableCollection<StepOption>();
        for (int i = 0; i < AppConstants.STEP_LEVELS.Length; i++)
        {
            int hz = AppConstants.STEP_LEVELS[i];
            string label = (hz == 8333) ? "8.33k" : (hz >= 1000 ? $"{hz / 1000.0}k" : hz.ToString());
            options.Add(new StepOption { ValueHz = hz, Label = label, Index = i });
        }
        return options;
    }

    public class StepOption : ObservableObject
    {
        public int ValueHz { get; set; }
        public string Label { get; set; } = "";
        public int Index { get; set; }
    }
}
