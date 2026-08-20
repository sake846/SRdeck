using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Collections.Generic;
using SRdeck.ViewModels;
using SRdeck.Models;

namespace SRdeck.Views;

public partial class MainWindow : Window
{
    private bool _isInit = false;
    private bool _isClosing = false;
    private readonly MainViewModel _viewModel;
    private readonly List<IRenderableView> _renderables = new List<IRenderableView>();
    private bool _isCompactMode = false;
    private long _wpfFpsWindowStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    private int _wpfFrameCount = 0;
    private double _wpfFps = 0.0;
    private int _lastRenderedFftSerial = 0;
    private long _wpfDroppedFftFrames = 0;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        CompositionTarget.Rendering += OnRendering;
        _viewModel.UiTick += OnUiTick;
        InitializeComponent();
        base.DataContext = _viewModel;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_isInit) return;

        IRadioRenderContext? engine = _viewModel.GetEngineForSetup();
        if (engine == null) return;

        _waterfallView.PollPointerInteraction();
        RefreshWpfFrameRate(engine);
        SyncWindowLayoutMode(engine);
        bool hasNewRenderData = engine.HasNewRenderData;
        bool hasNewDemodRenderData = engine.HasNewDemodRenderData;
        bool needsBackgroundRedraw = engine.NeedsBackgroundRedraw;



        _viewModel.OnRendering(); // Flags need to be cleared, or any internal VM processing

        if (hasNewRenderData || needsBackgroundRedraw)
        {
            foreach (var r in _renderables)
            {
                if (r is not UIElement ui || !ui.IsVisible) continue;
                r.RenderFrame(engine);
            }
            if (hasNewRenderData)
            {
                int serial = engine.RenderFrameSerial;
                if (_lastRenderedFftSerial > 0 && serial > _lastRenderedFftSerial + 1)
                {
                    _wpfDroppedFftFrames += serial - _lastRenderedFftSerial - 1;
                }
                _lastRenderedFftSerial = serial;
                engine.UpdateDiagnostics((ref RadioDiagnostics diagnostics) =>
                {
                    diagnostics.WpfFftFrameSerial = serial;
                    diagnostics.WpfFftDroppedFrames = _wpfDroppedFftFrames;
                });
            }
            engine.HasNewRenderData = false;
            engine.HasNewDemodRenderData = false;
        }
        else if (hasNewDemodRenderData)
        {
            engine.HasNewDemodRenderData = false;
        }
    }

    private void RefreshWpfFrameRate(IRadioRenderContext engine)
    {
        _wpfFrameCount++;
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double elapsedSeconds = (nowTicks - _wpfFpsWindowStartTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsedSeconds < 1.0) return;

        _wpfFps = _wpfFrameCount / elapsedSeconds;
        _wpfFrameCount = 0;
        _wpfFpsWindowStartTicks = nowTicks;

        engine.UpdateDiagnostics((ref RadioDiagnostics diagnostics) => diagnostics.WpfFps = _wpfFps);
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        IRadioRenderContext? engine = _viewModel.GetEngineForSetup();
        if (engine == null) return;

        SyncWindowLayoutMode(engine);

    }

    private void SyncWindowLayoutMode(IRadioRenderContext engine)
    {
        bool targetCompact = false;

        // コンパクトモード時の動的幅計算
        // (700 px の固定右ペインと 280 px の最小左ペインを保護)
        int expectedWidth = targetCompact ? 982 : 1280;
        int expectedHeight = targetCompact ? 240 : 700;

        if (_isCompactMode == targetCompact) return;

        _isCompactMode = targetCompact;

        if (_isCompactMode)
        {
            // コンパクトモード移行時は、強制固定ロックを解除する
            if (_viewModel.ZoomOverlay != null)
            {
                _viewModel.ZoomOverlay.IsEmbeddedLocked = false;
            }

            // 埋め込み（Embedded）状態になっている場合は、自動的にフローティング状態（Embedded = false）に変更する
            if (_viewModel.Receiver1.ZoomOverlay.IsEmbedded)
            {
                _viewModel.Receiver1.ZoomOverlay.IsEmbedded = false;
            }

            // WPFメイン表示領域（Spectrum/Waterfall）を折りたたむ
            _mainDisplayBorder.Visibility = Visibility.Collapsed;

            // ウィンドウサイズを動的コンパクトサイズに設定
            MinWidth = 982;
            Width = expectedWidth;
            Height = expectedHeight;
        }
        else
        {
            // WPFメイン表示領域を元に戻す
            _mainDisplayBorder.Visibility = Visibility.Visible;

            // ウィンドウサイズを通常サイズに戻す
            MinWidth = 982;
            Width = expectedWidth;
            Height = expectedHeight;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
    }

    public void FocusWaterfall()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
        System.Windows.Input.Mouse.Capture(null);
        _waterfallView.RestorePointerInteraction();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isInit)
        {
            _isInit = true;
            _viewModel.UiTick += _viewModel_UiTick;
            
            _renderables.Clear();
            _renderables.AddRange(FindVisualChildren<IRenderableView>(this));
        }
        
        if (_isInit)
        {
            _viewModel_UiTick(null, EventArgs.Empty);

            // メインウィンドウの縦高さが600px未満になったら、拡大画面を固定モードに固定する
            bool isLocked = !_isCompactMode && e.NewSize.Height < 600;
            if (_viewModel.ZoomOverlay != null)
            {
                _viewModel.ZoomOverlay.IsEmbeddedLocked = isLocked;
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : class
    {
        if (depObj == null) yield break;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t)
            {
                yield return t;
            }
            foreach (T childOfChild in FindVisualChildren<T>(child))
            {
                yield return childOfChild;
            }
        }
    }

    private void _viewModel_UiTick(object? sender, EventArgs e)
    {
        var p = _viewModel.GetEngineForSetup().Control;
        _viewModel.SyncViewModelData(p);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        
        e.Cancel = true;
        _isClosing = true;
        // Native SDR/GPU drivers are outside managed cancellation. Keep a final
        // backstop so a driver that never returns cannot leave an invisible,
        // unkillable-looking application process behind.
        _ = ForceExitIfShutdownStallsAsync();

        // Realtime/high process priority is useful while receiving samples, but it
        // can starve Explorer and the desktop while shutdown waits for native
        // callbacks and worker threads. Cleanup never needs elevated priority.
        NormalizeProcessPriorityForShutdown();

        CompositionTarget.Rendering -= OnRendering;
        _viewModel.UiTick -= OnUiTick;
        _viewModel.UiTick -= _viewModel_UiTick;

        _viewModel.ShuttingDownOverlayVisibility = Visibility.Visible;

        // UI描画を確実に完了させるための待機
        await Task.Delay(200);

        try
        {
            var engine = _viewModel.GetEngineForSetup();
            Task? disposeTask = null;
            if (engine != null)
            {
                _viewModel.StopAudioOutputForShutdown();
                disposeTask = Task.Run(() => engine.Dispose());
            }
            Task closeTask = _viewModel.ClosingAsync();

            Task cleanupTask = disposeTask != null
                ? Task.WhenAll(disposeTask, closeTask)
                : closeTask;
            Task completedTask = await Task.WhenAny(
                cleanupTask, Task.Delay(TimeSpan.FromSeconds(4)));
            if (ReferenceEquals(completedTask, cleanupTask))
                await cleanupTask;
            else
                System.Diagnostics.Debug.Print(
                    "Shutdown cleanup exceeded four seconds; continuing with UI shutdown.");


            // 描画リソースの破棄
            foreach (var r in _renderables)
            {
                r.DisposeRenderer();
            }

            try { SRdeck.Renderers.D3DImageInterop.ForceReleaseSharedDevice(); } catch { }
            try { SRdeck.Renderers.NativeGpuDrawApi.Shutdown(); } catch { }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.Print($"Shutdown Error: {ex.Message}");
        }
        finally
        {
            // 全てのクリーンアップが完了したら、正常終了させる
            Application.Current.Shutdown();
        }
    }

    private static async Task ForceExitIfShutdownStallsAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        Environment.Exit(0);
    }

    private static void NormalizeProcessPriorityForShutdown()
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
            process.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
        }
        catch (Exception ex)
        {
            // Priority changes can be rejected by policy. Shutdown must still proceed.
            System.Diagnostics.Debug.Print($"Failed to normalize process priority during shutdown: {ex.Message}");
        }
    }
}
