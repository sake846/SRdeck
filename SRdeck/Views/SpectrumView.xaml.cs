using SRdeck.Models;
using SRdeck.ViewModels;
using SRdeck.Renderers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System;
using SRdeck.DSP;
using SRdeck.Messages;
using CommunityToolkit.Mvvm.Messaging;

namespace SRdeck.Views
{
    public partial class SpectrumView : UserControl, IRenderableView
    {
        public FrameworkElement ImageFore => _imgSpectrumFore;
        private SpectrumRenderer _renderer;
        private NativeSpectrumGpuPresenter? _gpuPresenter;
        private bool _useGpuPath;
        private long _lastGpuRetryTicks;
        private bool _hasRenderedLiveFrame;

        public SpectrumView()
        {
            InitializeComponent();
            _renderer = new SpectrumRenderer();
            _renderer.OnImageUpdated = _ => { };
            _gpuPresenter = new NativeSpectrumGpuPresenter();
            WeakReferenceMessenger.Default.Register<ResetRenderersMessage>(this, (r, m) =>
            {
                _hasRenderedLiveFrame = false;
            });
            
            _interactionSurface.IsManipulationEnabled = true;
            _interactionSurface.ManipulationDelta += SpectrumView_ManipulationDelta;
            _interactionSurface.ManipulationCompleted += SpectrumView_ManipulationCompleted;

            this.Loaded += (s, e) =>
            {
                if (_imgSpectrumFore.ActualWidth > 0 && _imgSpectrumFore.ActualHeight > 0)
                {
                    if (DataContext is MainViewModel vm)
                    {
                        var w = Math.Round(_imgSpectrumFore.ActualWidth);
                        var h = Math.Round(_imgSpectrumFore.ActualHeight);
                        vm.SpectrumWidth = w;
                        vm.SpectrumHeight = h;
                        vm.SpActualWidth = w;
                        var engine = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ISdrEngine>();
                        if (engine != null) engine.RequestedSpectrumWidth = (int)w;
                    }
                    int wpx = ((int)Math.Round(_imgSpectrumFore.ActualWidth) + 15) & ~15;
                    int hpx = ((int)Math.Round(_imgSpectrumFore.ActualHeight) + 15) & ~15;
                    _useGpuPath = _gpuPresenter.Initialize(wpx, hpx);
                    if (_useGpuPath && _gpuPresenter.ImageSource != null)
                    {
                        _imgSpectrumFore.Source = _gpuPresenter.ImageSource;
                        float initGridTopDb = (DataContext is MainViewModel vmInit) ? vmInit.SpectrumOverlay.GridTopDb : AppConstants.DEFAULT_GRID_TOP_DB;
                        float initSpanHz = (DataContext is MainViewModel vmInit2) ? (float)vmInit2.Display.CurrentMainSpanHz : AppConstants.FULL_BW;
                        _gpuPresenter.RenderGridOnly(initGridTopDb, initSpanHz);
                    }
                    _renderer.SetImageSize(wpx, hpx);
                }
                else
                {
                    _renderer.SetImageSize(10, 10);
                }
            };

            _imgSpectrumFore.SizeChanged += (s, e) =>
            {
                if (DataContext is MainViewModel vm)
                {
                    var w = Math.Round(e.NewSize.Width);
                    var h = Math.Round(e.NewSize.Height);
                    vm.SpectrumWidth = w;
                    vm.SpectrumHeight = h;
                    vm.SpActualWidth = w;
                    var engine = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<ISdrEngine>();
                    if (engine != null) engine.RequestedSpectrumWidth = (int)w;
                }
                int wpx = ((int)Math.Round(e.NewSize.Width) + 15) & ~15;
                int hpx = ((int)Math.Round(e.NewSize.Height) + 15) & ~15;
                if (_gpuPresenter != null)
                {
                    _gpuPresenter.Resize(wpx, hpx);
                    _useGpuPath = _gpuPresenter.IsReady;
                    if (_useGpuPath && _gpuPresenter.ImageSource != null)
                    {
                        _imgSpectrumFore.Source = _gpuPresenter.ImageSource;
                        float initGridTopDb = (DataContext is MainViewModel vmInit) ? vmInit.SpectrumOverlay.GridTopDb : AppConstants.DEFAULT_GRID_TOP_DB;
                        float initSpanHz = (DataContext is MainViewModel vmInit2) ? (float)vmInit2.Display.CurrentMainSpanHz : AppConstants.FULL_BW;
                        _gpuPresenter.RenderGridOnly(initGridTopDb, initSpanHz);
                    }
                }
                _renderer.SetImageSize(wpx, hpx);
            };

            this.IsVisibleChanged += (s, e) => { };
        }

        public void RenderFrame(IRadioRenderContext engine)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float displayBw = 7000000f;
            float roundingHz = 500000f;
            bool gridAnchorEnabled = false;
            double gridAnchorFrequencyHz = 0.0;
            double gridAnchorRatio = 0.5;
            if (DataContext is MainViewModel vm)
            {
                _renderer.GridTopDb = vm.SpectrumOverlay.GridTopDb;
                displayBw = (float)vm.Display.CurrentMainSpanHz;
                roundingHz = (float)vm.Display.CurrentMainRoundingHz;
                gridAnchorEnabled = vm.MainGridAnchorEnabled;
                gridAnchorFrequencyHz = vm.MainGridAnchorFrequencyHz;
                gridAnchorRatio = vm.MainGridAnchorRatio;
            }
            _renderer.RfCalOffset = engine.RfCalibrationOffset;
            _renderer.SetBias(engine.SpectrumBiasAdj);
            TryEnsureGpuReady();
            bool hasActiveSource = engine.IsSdrRunning || engine.IsPlaying;
            bool hasValidFftData = engine.HasValidMainFftData;
            if (_useGpuPath && _gpuPresenter != null && _gpuPresenter.IsReady)
            {
                if (hasActiveSource && hasValidFftData && engine.SpectrumFftData != null && engine.SpectrumFftData.Length > 0)
                {
                    double anchorRelHz = gridAnchorEnabled ? gridAnchorFrequencyHz - engine.Control.CenterFreqHz : 0.0;
                    _gpuPresenter.Render(engine.SpectrumFftData, engine.Control, _renderer.GridTopDb, _renderer.RfCalOffset, displayBw, gridAnchorEnabled, anchorRelHz, gridAnchorRatio, engine.MainFftCenterFreqHz);
                    _hasRenderedLiveFrame = true;
                }
                else if (!_hasRenderedLiveFrame)
                {
                    double anchorRelHz = gridAnchorEnabled ? gridAnchorFrequencyHz - engine.Control.CenterFreqHz : 0.0;
                    _gpuPresenter.RenderGridOnly(_renderer.GridTopDb, displayBw, gridAnchorEnabled, anchorRelHz, gridAnchorRatio, engine.Control.CenterFreqHz);
                }
            }
            sw.Stop();
            engine.UpdateDiagnostics((ref RadioDiagnostics d) =>
            {
                d.TimeWpfSpectrum = sw.Elapsed.TotalMilliseconds;
                d.TimeWpfSpectrumPrepare = _gpuPresenter?.LastPrepareMilliseconds ?? 0;
                d.TimeWpfSpectrumLock = _gpuPresenter?.LastLockMilliseconds ?? 0;
                d.TimeWpfSpectrumDraw = _gpuPresenter?.LastDrawMilliseconds ?? 0;
                d.TimeWpfSpectrumUnlock = _gpuPresenter?.LastUnlockMilliseconds ?? 0;
                if (_useGpuPath && _gpuPresenter != null && _gpuPresenter.IsReady) d.WpfGpuPathFlags |= 0x1;
                else d.WpfGpuPathFlags &= ~0x1;
                d.WpfGpuInitSp = _gpuPresenter?.LastInitStatus ?? -999;
            });
        }

        private void TryEnsureGpuReady()
        {
            if (_useGpuPath && _gpuPresenter != null && _gpuPresenter.IsReady)
            {
                if (_imgSpectrumFore.Source != _gpuPresenter.ImageSource)
                {
                    _imgSpectrumFore.Source = _gpuPresenter.ImageSource;
                }
                return;
            }
            long now = Environment.TickCount64;
            if (now - _lastGpuRetryTicks < 1000) return;
            _lastGpuRetryTicks = now;
            int wpx = (Math.Max(10, (int)Math.Round(_imgSpectrumFore.ActualWidth)) + 15) & ~15;
            int hpx = (Math.Max(10, (int)Math.Round(_imgSpectrumFore.ActualHeight)) + 15) & ~15;
            if (_gpuPresenter == null) return;
            _useGpuPath = _gpuPresenter.Initialize(wpx, hpx);
            if (_useGpuPath && _gpuPresenter.ImageSource != null)
            {
                _imgSpectrumFore.Source = _gpuPresenter.ImageSource;
                float initGridTopDb = (DataContext is MainViewModel vmInit) ? vmInit.SpectrumOverlay.GridTopDb : AppConstants.DEFAULT_GRID_TOP_DB;
                float initSpanHz = (DataContext is MainViewModel vmInit2) ? (float)vmInit2.Display.CurrentMainSpanHz : AppConstants.FULL_BW;
                _gpuPresenter.RenderGridOnly(initGridTopDb, initSpanHz);
            }
        }

        private void SpectrumView_ManipulationDelta(object? sender, System.Windows.Input.ManipulationDeltaEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SpectrumManipulationDeltaCommand.Execute(e.DeltaManipulation.Translation);
                e.Handled = true;
            }
        }

        private void SpectrumView_ManipulationCompleted(object? sender, System.Windows.Input.ManipulationCompletedEventArgs e)
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
            _gpuPresenter?.Dispose();
            _renderer?.Dispose();
        }

        private void Border_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            ((UIElement)sender).Focus();
        }
    }
}
