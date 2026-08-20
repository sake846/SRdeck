using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SRdeck.Models;
using SRdeck.Renderers;
using SRdeck.ViewModels.Components;

namespace SRdeck.ViewModels;

/// <summary>
/// レシーバー固有のプロパティを統一名で公開するコンテキストクラスです。
/// ReceiverView が DataContext として受け取り、レシーバー番号に依存しないバインディングを実現します。
/// </summary>
public class ReceiverContext : ObservableObject
{
    private readonly MainViewModel _mainVm;

    /// <summary>レシーバー番号 (常に 1)</summary>
    public int ReceiverIndex { get; }

    /// <summary>チューナーViewModel（統一名）</summary>
    public TunerViewModel Tuner { get; }

    /// <summary>ズームオーバーレイViewModel（統一名）</summary>
    public ZoomOverlayViewModel ZoomOverlay { get; }

    /// <summary>表示設定ViewModel（共通）</summary>
    public DisplayViewModel Display => _mainVm.Display;

    // --- コマンド（統一名） ---
    public ICommand ReceiverButtonClickCommand { get; }
    public ICommand OpenFrequencyInputDialogCommand { get; }
    public ICommand DemodWaveClickCommand { get; }
    public ICommand SetDemodWaveModeCommand { get; }

    // --- コレクション（統一名） ---
    public ObservableCollection<SignalMeterSegment> SignalMeterSegments { get; }
    public System.Collections.Generic.List<DemodWaveOverlayButton> DemodWaveButtons { get; }

    // --- 動的プロパティ（MainViewModelから転送） ---
    private double _squelchBarWidth;
    public double SquelchBarWidth
    {
        get => _squelchBarWidth;
        set => SetProperty(ref _squelchBarWidth, value);
    }

    private bool _isDelayActive;
    public bool IsDelayActive
    {
        get => _isDelayActive;
        set => SetProperty(ref _isDelayActive, value);
    }

    // --- Display 系プロパティ（レシーバー固有、統一名） ---
    private string _zoomDisplayToggleText = "";
    public string ZoomDisplayToggleText
    {
        get => _zoomDisplayToggleText;
        set => SetProperty(ref _zoomDisplayToggleText, value);
    }

    private bool _isZoomDisplayVisible;
    public bool IsZoomDisplayVisible
    {
        get => _isZoomDisplayVisible;
        set => SetProperty(ref _isZoomDisplayVisible, value);
    }

    public ICommand ToggleZoomDisplayCommand { get; }

    private string _subDisplayToggleText = "";
    public string SubDisplayToggleText
    {
        get => _subDisplayToggleText;
        set => SetProperty(ref _subDisplayToggleText, value);
    }

    private bool _isSubDisplayVisible;
    public bool IsSubDisplayVisible
    {
        get => _isSubDisplayVisible;
        set => SetProperty(ref _isSubDisplayVisible, value);
    }

    public ICommand ToggleSubDisplayCommand { get; }

    // --- 共有プロパティ（両レシーバーで共通） ---
    public MainViewModel MainViewModel => _mainVm;

    // DemodWaveDisplayMode は MainViewModel にあるが、ReceiverView.xaml から直接参照される
    private DemodWaveMode _demodWaveDisplayMode;
    public DemodWaveMode DemodWaveDisplayMode
    {
        get => _demodWaveDisplayMode;
        set => SetProperty(ref _demodWaveDisplayMode, value);
    }

    private string _demodWaveTimeLabel = "";
    public string DemodWaveTimeLabel
    {
        get => _demodWaveTimeLabel;
        set => SetProperty(ref _demodWaveTimeLabel, value);
    }

    // --- ZoomView 用コマンド（統一名） ---
    public ICommand ZoomWindowImageClickCommand { get; }
    public ICommand ZoomMouseUpCommand { get; }
    public ICommand ZoomMouseMoveCommand { get; }
    public ICommand MouseLeaveCommand { get; }
    public ICommand ZoomWindowClickCommand { get; }

    public ReceiverContext(MainViewModel mainVm, ISdrEngine engine, int receiverIndex)
    {
        _mainVm = mainVm;
        ReceiverIndex = receiverIndex;

        Tuner = new TunerViewModel(engine, receiverIndex);
        ZoomOverlay = new ZoomOverlayViewModel();
        SignalMeterSegments = new ObservableCollection<SignalMeterSegment>();
        DemodWaveButtons = MainViewModel.GetDefaultDemodWaveButtons();

        ReceiverButtonClickCommand = mainVm.ReceiverButtonClickCommand;
        OpenFrequencyInputDialogCommand = mainVm.OpenFrequencyInputDialogCommand;
        DemodWaveClickCommand = mainVm.DemodWaveClickCommand;
        ToggleZoomDisplayCommand = mainVm.Display.ToggleZoomDisplayCommand;
        ToggleSubDisplayCommand = mainVm.Display.ToggleSubDisplayCommand;
        ZoomWindowImageClickCommand = mainVm.ZoomWindowImageClickCommand;
        ZoomMouseUpCommand = mainVm.ZoomMouseUpCommand;
        ZoomMouseMoveCommand = mainVm.ZoomMouseMoveCommand;
        MouseLeaveCommand = mainVm.HandleMouseLeaveCommand;
        ZoomWindowClickCommand = mainVm.ZoomWindowClickCommand;

        SetDemodWaveModeCommand = new RelayCommand<DemodWaveMode>(mode =>
        {
            _mainVm.ApplyDemodWaveModeDirect(ReceiverIndex, mode);
        });

        // MainViewModel のプロパティ変更を転送
        mainVm.PropertyChanged += OnMainVmPropertyChanged;
        mainVm.Display.PropertyChanged += OnDisplayPropertyChanged;

        // 初期値の同期
        SyncFromMainVm();
        SyncFromDisplay();
    }

    private void OnMainVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SquelchBarWidth): SquelchBarWidth = _mainVm.SquelchBarWidth; break;
            case nameof(MainViewModel.IsDelayActive): IsDelayActive = _mainVm.IsDelayActive; break;
            case nameof(MainViewModel.DemodWaveDisplayMode): DemodWaveDisplayMode = _mainVm.DemodWaveDisplayMode; break;
            case nameof(MainViewModel.DemodWaveTimeLabel): DemodWaveTimeLabel = _mainVm.DemodWaveTimeLabel; break;
        }
    }

    private void OnDisplayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DisplayViewModel.ZoomDisplayToggleText): ZoomDisplayToggleText = _mainVm.Display.ZoomDisplayToggleText; break;
            case nameof(DisplayViewModel.IsZoomDisplayVisible): IsZoomDisplayVisible = _mainVm.Display.IsZoomDisplayVisible; break;
            case nameof(DisplayViewModel.SubDisplayToggleText): SubDisplayToggleText = _mainVm.Display.SubDisplayToggleText; break;
            case nameof(DisplayViewModel.IsSubDisplayVisible): IsSubDisplayVisible = _mainVm.Display.IsSubDisplayVisible; break;
        }
    }

    private void SyncFromMainVm()
    {
        SquelchBarWidth = _mainVm.SquelchBarWidth;
        IsDelayActive = _mainVm.IsDelayActive;
        DemodWaveDisplayMode = _mainVm.DemodWaveDisplayMode;
        DemodWaveTimeLabel = _mainVm.DemodWaveTimeLabel;
    }

    private void SyncFromDisplay()
    {
        ZoomDisplayToggleText = _mainVm.Display.ZoomDisplayToggleText;
        IsZoomDisplayVisible = _mainVm.Display.IsZoomDisplayVisible;
        SubDisplayToggleText = _mainVm.Display.SubDisplayToggleText;
        IsSubDisplayVisible = _mainVm.Display.IsSubDisplayVisible;
    }
}
