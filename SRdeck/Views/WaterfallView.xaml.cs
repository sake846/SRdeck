using SRdeck.Models;
using SRdeck.ViewModels;
using SRdeck.Renderers;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SRdeck.DSP;
using SRdeck.Messages;
using SRdeck.Helpers;
using CommunityToolkit.Mvvm.Messaging;
using SRdeckPlugin.Contracts;

namespace SRdeck.Views
{
    public partial class WaterfallView : UserControl, IRenderableView
    {
        public FrameworkElement ImageFore => _imgWaterfallFore;
        private WaterfallRenderer _renderer;
        private NativeWaterfallGpuPresenter? _gpuPresenter;
        private bool _useGpuPath;
        private long _lastGpuRetryTicks;
        private bool _hasRenderedLiveFrame;
        private bool _wasActiveSource;
        private bool _wasPointerInside;
        private Point _lastPointerPosition = new(double.NaN, double.NaN);
        private WaterfallTimeMode _lastTimeMode = WaterfallTimeMode.ThreeMinutes;
        private bool _hasTimeMode;

        public WaterfallView()
        {
            InitializeComponent();
            _renderer = new WaterfallRenderer();
            _renderer.OnImageUpdated = _ => { };
            _gpuPresenter = new NativeWaterfallGpuPresenter();
            WeakReferenceMessenger.Default.Register<ResetRenderersMessage>(this, (r, m) =>
            {
                _hasRenderedLiveFrame = false;
            });
            WeakReferenceMessenger.Default.Register<ResetWaterfallTimingMessage>(this, (r, m) =>
            {
                _renderer.ResetTiming();
                _gpuPresenter?.ResetTiming();
                _wasActiveSource = false;
            });

            _interactionSurface.IsManipulationEnabled = true;
            _interactionSurface.ManipulationDelta += WaterfallView_ManipulationDelta;
            _interactionSurface.ManipulationCompleted += WaterfallView_ManipulationCompleted;

            this.Loaded += (s, e) =>
            {
                if (_imgWaterfallFore.ActualWidth > 0 && _imgWaterfallFore.ActualHeight > 0)
                {
                    int centerFreq = 120000000;
                    if (DataContext is MainViewModel vm)
                    {
                        var w = Math.Round(_imgWaterfallFore.ActualWidth);
                        var h = Math.Round(_imgWaterfallFore.ActualHeight);
                        vm.WaterfallWidth = w;
                        vm.WaterfallHeight = h;
                        vm.WfActualWidth = w;
                        vm.Diagnostics.DebugOverlayHeight = h;
                        var engine = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ISdrEngine>();
                        if (engine != null) engine.RequestedSpectrumWidth = (int)w;
                        if (vm.GetEngineForSetup() != null) centerFreq = vm.GetEngineForSetup().Control.CenterFreqHz;
                    }
                    var (wpx, hpx) = GetRasterSize(_imgWaterfallFore, _imgWaterfallFore.RenderSize);
                    if (DataContext is MainViewModel loadedVm) loadedVm.WaterfallRasterHeight = hpx;
                    _useGpuPath = _gpuPresenter.Initialize(wpx, hpx);
                    if (_useGpuPath && _gpuPresenter.ImageSource != null) _imgWaterfallFore.Source = _gpuPresenter.ImageSource;
                    _renderer.SetImageSize(wpx, hpx, centerFreq);
                }
                else
                {
                    _renderer.SetImageSize(10, 10, 120000000);
                }
            };

            _imgWaterfallFore.SizeChanged += (s, e) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    var w = Math.Round(e.NewSize.Width);
                    var h = Math.Round(e.NewSize.Height);
                    vm.WaterfallWidth = w;
                    vm.WaterfallHeight = h;
                    vm.WfActualWidth = w;
                    vm.Diagnostics.DebugOverlayHeight = h;
                    var engine = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ISdrEngine>();
                    if (engine != null) engine.RequestedSpectrumWidth = (int)w;
                    int centerFreq = vm.GetEngineForSetup() != null ? vm.GetEngineForSetup().Control.CenterFreqHz : 120000000;
                    var (wpx, hpx) = GetRasterSize(_imgWaterfallFore, e.NewSize);
                    vm.WaterfallRasterHeight = hpx;
                    if (_gpuPresenter != null)
                    {
                        _gpuPresenter.Resize(wpx, hpx);
                        _useGpuPath = _gpuPresenter.IsReady;
                        if (_useGpuPath && _gpuPresenter.ImageSource != null) _imgWaterfallFore.Source = _gpuPresenter.ImageSource;
                    }
                    _renderer.SetImageSize(wpx, hpx, centerFreq);
                }
                else
                {
                    var (wpx, hpx) = GetRasterSize(_imgWaterfallFore, e.NewSize);
                    if (_gpuPresenter != null)
                    {
                        _gpuPresenter.Resize(wpx, hpx);
                        _useGpuPath = _gpuPresenter.IsReady;
                        if (_useGpuPath && _gpuPresenter.ImageSource != null) _imgWaterfallFore.Source = _gpuPresenter.ImageSource;
                    }
                    _renderer.SetImageSize(wpx, hpx, 120000000);
                }
            };

            this.IsVisibleChanged += (s, e) => { };
        }

        public void RenderFrame(IRadioRenderContext engine)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float displayBw = 7000000f;
            WaterfallTimeMode timeMode = WaterfallTimeMode.ThreeMinutes;
            if (DataContext is MainViewModel vm)
            {
                displayBw = (float)vm.Display.CurrentMainSpanHz;
                timeMode = vm.WaterfallDisplayTimeMode;
            }

            SyncTimeMode(timeMode);

            _renderer.RfCalOffset = engine.RfCalibrationOffset;
            _renderer.SetBias(engine.WaterfallBiasAdj);
            TryEnsureGpuReady();
            bool hasActiveSource = engine.IsSdrRunning || engine.IsPlaying;
            bool hasValidFftData = engine.HasValidMainFftData;
            if (hasActiveSource && !_wasActiveSource)
            {
                _renderer.ResetTiming();
                _gpuPresenter?.ResetTiming();
            }
            _wasActiveSource = hasActiveSource;
            if (_useGpuPath && _gpuPresenter != null && _gpuPresenter.IsReady)
            {
                if (hasActiveSource && hasValidFftData && engine.WaterfallFftData != null && engine.WaterfallFftData.Length > 0)
                {
                    _gpuPresenter.RfCalOffset = engine.RfCalibrationOffset;
                    _gpuPresenter.BiasDb = engine.WaterfallBiasAdj;
                    _gpuPresenter.Render(engine.WaterfallFftData, engine.WaterfallBlockSequence, engine.Control, engine.State, displayBw, engine.MainFftCenterFreqHz, timeMode);
                    _hasRenderedLiveFrame = true;
                }
                else if (!_hasRenderedLiveFrame)
                {
                    // 起動直後は疑似データを描かず、フレーム/目盛りのみを表示する。
                    _gpuPresenter.RenderBlank();
                }
            }
            else if (hasActiveSource && hasValidFftData && engine.WaterfallFftData != null && engine.WaterfallFftData.Length > 0)
            {
                _renderer.SetWaterfall(engine.WaterfallFftData, engine.WaterfallBlockSequence, engine.Control, engine.State, displayBw, engine.MainFftCenterFreqHz, timeMode);
                _hasRenderedLiveFrame = true;
            }
            sw.Stop();
            engine.UpdateDiagnostics((ref RadioDiagnostics d) =>
            {
                d.TimeWpfWaterfall = sw.Elapsed.TotalMilliseconds;
                if (_useGpuPath && _gpuPresenter != null && _gpuPresenter.IsReady) d.WpfGpuPathFlags |= 0x2;
                else d.WpfGpuPathFlags &= ~0x2;
                d.WpfGpuInitWf = _gpuPresenter?.LastInitStatus ?? -999;
            });
        }

        private void TryEnsureGpuReady()
        {
            // D3DImage has a fixed 96-DPI logical size.  Its backing surface must
            // therefore use device pixels; otherwise, at 125% (and other fractional
            // scales) WPF nearest-neighbor scales each waterfall row unevenly.
            var (wpx, hpx) = GetRasterSize(_imgWaterfallFore, _imgWaterfallFore.RenderSize);
            if (_useGpuPath && _gpuPresenter != null && _gpuPresenter.IsReady)
            {
                _gpuPresenter.Resize(wpx, hpx);
                if (_imgWaterfallFore.Source != _gpuPresenter.ImageSource)
                {
                    _imgWaterfallFore.Source = _gpuPresenter.ImageSource;
                }
                return;
            }
            long now = Environment.TickCount64;
            if (now - _lastGpuRetryTicks < 1000) return;
            _lastGpuRetryTicks = now;
            if (_gpuPresenter == null) return;
            _useGpuPath = _gpuPresenter.Initialize(wpx, hpx);
            if (_useGpuPath && _gpuPresenter.ImageSource != null)
            {
                _imgWaterfallFore.Source = _gpuPresenter.ImageSource;
            }
        }

        private void SyncTimeMode(WaterfallTimeMode timeMode)
        {
            if (!_hasTimeMode)
            {
                _lastTimeMode = timeMode;
                _hasTimeMode = true;
                return;
            }
            if (_lastTimeMode == timeMode) return;

            _lastTimeMode = timeMode;
            _renderer.ResetHistory();
            _gpuPresenter?.ResetHistory();
            _hasRenderedLiveFrame = false;
        }

        private static (int Width, int Height) GetRasterSize(Visual visual, Size logicalSize)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(visual);
            return (
                Math.Max(10, (int)Math.Round(logicalSize.Width * dpi.DpiScaleX)),
                Math.Max(10, (int)Math.Round(logicalSize.Height * dpi.DpiScaleY)));
        }

        private void WaterfallView_ManipulationDelta(object? sender, System.Windows.Input.ManipulationDeltaEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.WaterfallManipulationDeltaCommand.Execute(e.DeltaManipulation.Translation);
                e.Handled = true;
            }
        }

        private void WaterfallView_ManipulationCompleted(object? sender, System.Windows.Input.ManipulationCompletedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ManipulationCompletedCommand.Execute(null);
                e.Handled = true;
            }
        }

        public void DisposeRenderer()
        {
            WeakReferenceMessenger.Default.Unregister<ResetRenderersMessage>(this);
            WeakReferenceMessenger.Default.Unregister<ResetWaterfallTimingMessage>(this);
            _gpuPresenter?.Dispose();
            _renderer?.Dispose();
        }

        private void Border_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            FocusInteractionSurface();
        }

        public bool FocusInteractionSurface()
        {
            Mouse.Capture(null);
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(_interactionSurface), _interactionSurface);
            return Keyboard.Focus(_interactionSurface) == _interactionSurface;
        }

        public void RestorePointerInteraction()
        {
            Mouse.Capture(null);
            ReleaseMouseCapture();
            _interactionSurface.ReleaseMouseCapture();
            FocusInteractionSurface();

            if (IsMouseOver)
            {
                SyncCursorFromCurrentMousePosition();
            }
        }

        public void PollPointerInteraction()
        {
            if (!IsVisible || gridWaterfallArea.ActualWidth <= 0 || gridWaterfallArea.ActualHeight <= 0)
            {
                SyncPointerOutside();
                return;
            }

            Point position;
            try
            {
                position = gridWaterfallArea.PointFromScreen(Win32Api.GetCursorPosition());
            }
            catch (InvalidOperationException)
            {
                SyncPointerOutside();
                return;
            }

            bool isInside = position.X >= 0 && position.Y >= 0 &&
                            position.X <= gridWaterfallArea.ActualWidth &&
                            position.Y <= gridWaterfallArea.ActualHeight;
            if (!isInside)
            {
                SyncPointerOutside();
                return;
            }

            bool hasMoved = !_wasPointerInside ||
                            Math.Abs(position.X - _lastPointerPosition.X) >= 0.5 ||
                            Math.Abs(position.Y - _lastPointerPosition.Y) >= 0.5;
            _wasPointerInside = true;
            if (!hasMoved) return;

            _lastPointerPosition = position;
            ExecutePointerMove(position);
        }

        private void SyncPointerOutside()
        {
            if (!_wasPointerInside) return;

            _wasPointerInside = false;
            _lastPointerPosition = new Point(double.NaN, double.NaN);
            if (DataContext is MainViewModel vm && vm.HandleMouseLeaveCommand.CanExecute(null))
            {
                vm.HandleMouseLeaveCommand.Execute(null);
            }
        }

        private void WaterfallView_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            RestorePointerInteraction();
        }

        private void WaterfallView_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
                SyncCursorFromCurrentMousePosition();
        }

        private void SyncCursorFromCurrentMousePosition()
        {
            ExecutePointerMove(Mouse.GetPosition(gridWaterfallArea));
        }

        private void ExecutePointerMove(Point position)
        {
            if (DataContext is MainViewModel vm &&
                position.X >= 0 && position.Y >= 0 &&
                position.X <= gridWaterfallArea.ActualWidth &&
                position.Y <= gridWaterfallArea.ActualHeight &&
                vm.WaterfallMouseMoveCommand.CanExecute(position))
            {
                vm.WaterfallMouseMoveCommand.Execute(position);
            }
        }
    }
}
