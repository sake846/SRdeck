using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SRdeck.Models;
using SRdeck.Messages;
using SRdeck.ViewModels.Components;
using SRdeck.Renderers;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private bool _isApplyingAtomicMainViewUpdate;
    private long _lastMainViewZoomTicks;
    private double _mainViewZoomAnchorFrequencyHz;
    private double _mainViewZoomAnchorPositionRatio;

    internal void SyncMainSpanForAtomicViewUpdate(int spanHz)
    {
        _isApplyingAtomicMainViewUpdate = true;
        try
        {
            Display.SyncMainZoomSpanHz(spanHz);
        }
        finally
        {
            _isApplyingAtomicMainViewUpdate = false;
        }
    }

    [RelayCommand]
    private void SpectrumManipulationDelta(System.Windows.Vector translation)
    {
        StopSdrCenterSnap();
        RadioControl radioControl = _engine.Control;
        bool isControlChanged = false;
        if (radioControl.CursorFreqHz != -1) { radioControl.CursorFreqHz = -1; isControlChanged = true; }
        if (PanMainView(ref radioControl, translation.X, SpectrumWidth)) isControlChanged = true;
        if (isControlChanged)
        {
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
            RestartCenterSnapDelayTimer();
        }
    }

    [RelayCommand]
    private void WaterfallManipulationDelta(System.Windows.Vector translation)
    {
        StopSdrCenterSnap();
        RadioControl radioControl = _engine.Control;
        bool isControlChanged = false;
        if (radioControl.CursorFreqHz != -1) { radioControl.CursorFreqHz = -1; isControlChanged = true; }
        double horizontalDelta = translation.X;
        if (PanMainView(ref radioControl, horizontalDelta, WaterfallWidth)) isControlChanged = true;
        horizontalDelta = 0;
        if (_inputService.ProcessWaterfallSwipe(horizontalDelta, translation.Y, (Keyboard.Modifiers & ModifierKeys.Shift) != 0, ref radioControl, GetMaxHistorySec())) isControlChanged = true;
        if (isControlChanged)
        {
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
            RestartCenterSnapDelayTimer();
        }
    }

    [RelayCommand]
    private void MainViewZoomWheel(object argument)
    {
        StopSdrCenterSnap();

        Vector delta;
        Point pointer;
        double sourceWidth;
        if (argument is Tuple<Vector, Point, double> tupleWithWidth)
        {
            delta = tupleWithWidth.Item1;
            pointer = tupleWithWidth.Item2;
            sourceWidth = tupleWithWidth.Item3;
        }
        else if (argument is Tuple<Vector, Point> tuple)
        {
            delta = tuple.Item1;
            pointer = tuple.Item2;
            sourceWidth = Math.Max(SpectrumWidth, WaterfallWidth);
        }
        else
        {
            return;
        }

        double deltaY = delta.Y;
        if (Math.Abs(deltaY) <= double.Epsilon) return;

        double width = Math.Max(1.0, sourceWidth);
        double x = Math.Clamp(pointer.X, 0.0, width);

        RadioControl radioControl = _engine.Control;
        int currentSpanHz = Math.Max(1, Display.CurrentMainSpanHz);
        int sampleRateHz = radioControl.FsHz > 0 ? radioControl.FsHz : (_engine.SdrDevice?.FsHz ?? (int)AppConstants.FULL_BW);
        sampleRateHz = Math.Max(currentSpanHz, sampleRateHz);

        int fftMode = Math.Clamp(FftResolutionMode, 0, _fftResolutionSizes.Length - 1);
        int fftBinCount = Math.Max(1, _fftResolutionSizes[fftMode]);
        int maxSpanHz = Display.BaseMainSpanHz;
        int minSpanHz = Math.Min(maxSpanHz, Math.Max(1, (int)Math.Ceiling(width * ((double)sampleRateHz / fftBinCount))));
        double zoomFactor = Math.Pow(1.2, Math.Abs(deltaY) / 100.0);
        int nextSpanHz = (int)Math.Round(Math.Clamp(deltaY < 0 ? currentSpanHz / zoomFactor : currentSpanHz * zoomFactor, minSpanHz, maxSpanHz));

        if (nextSpanHz == currentSpanHz)
        {
            return;
        }

        if (!Display.IsMainViewZoomed && nextSpanHz < maxSpanHz)
        {
            CompleteSdrCenterSnapBeforeZoom();
            radioControl = _engine.Control;
        }

        double pointerRatio = Math.Clamp(x / width, 0.0, 1.0);
        long nowTicks = Environment.TickCount64;
        bool continueGesture = nowTicks - _lastMainViewZoomTicks <= 400 &&
            Math.Abs(pointerRatio - _mainViewZoomAnchorPositionRatio) <= 0.5 / width;
        double anchorFreq = continueGesture
            ? _mainViewZoomAnchorFrequencyHz
            : radioControl.CenterFreqHz + (pointerRatio - 0.5) * currentSpanHz;
        double anchorRatio = pointerRatio - 0.5;
        int nextCenterHz = (int)Math.Clamp(Math.Round(anchorFreq - anchorRatio * nextSpanHz), 1.0, 2_000_000_000.0);
        if (nextSpanHz < maxSpanHz)
        {
            double sdrCenter = _engine.MainFftCenterFreqHz;
            double maxOffset = Math.Max(0.0, (maxSpanHz - nextSpanHz) / 2.0);
            double minCenter = sdrCenter - maxOffset;
            double maxCenter = sdrCenter + maxOffset;
            nextCenterHz = (int)Math.Clamp(nextCenterHz, minCenter, maxCenter);
        }

        _lastMainViewZoomTicks = nowTicks;
        _mainViewZoomAnchorFrequencyHz = anchorFreq;
        _mainViewZoomAnchorPositionRatio = pointerRatio;

        MainGridAnchorEnabled = true;
        MainGridAnchorFrequencyHz = anchorFreq;
        MainGridAnchorRatio = pointerRatio;

        SyncMainSpanForAtomicViewUpdate(nextSpanHz);
        radioControl = _engine.Control;
        radioControl.CenterFreqHz = (int)nextCenterHz;
        radioControl.MainSpanHz = nextSpanHz;
        radioControl.BaseMainSpanHz = Display.BaseMainSpanHz;
        radioControl.FreqOffsetHz = radioControl.TunedFreqHz - radioControl.CenterFreqHz;
        radioControl.CursorFreqHz = (int)Math.Round(anchorFreq);
        radioControl.CursorFreqOffsetHz = radioControl.CursorFreqHz - radioControl.CenterFreqHz;
        radioControl.CursorPoint = pointer;
        radioControl.ApplyPrimaryReceiverTuning();

        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
    }

    [RelayCommand]
    private void SpectrumMouseDown(object argument)
    {
        StopSdrCenterSnap();
        if (ExtractMouseInfo(argument, out Point mousePosition, out MouseButton mouseButton) && mouseButton == MouseButton.Left)
        {
            if (mousePosition.X <= 50 || mousePosition.X >= (SpectrumWidth - 50)) return;

            _isSpectrumDragging = true;
            _spectrumDragStartPoint = mousePosition;
            _spectrumDragStartCenterFreq = _engine.Control.CenterFreqHz;
        }
    }

    private bool ExtractMouseInfo(object argument, out Point position, out MouseButton button)
    {
        position = default;
        button = MouseButton.Left;
        if (argument is Tuple<Point, MouseButton> mouseInfoTuple) { position = mouseInfoTuple.Item1; button = mouseInfoTuple.Item2; return true; }
        if (argument is Point mousePoint) { position = mousePoint; return true; }
        return false;
    }

    [RelayCommand]
    private void SpectrumMouseUp(object argument)
    {
        if (!ExtractMouseInfo(argument, out Point mousePosition, out MouseButton mouseButton)) return;
        if (_isSpectrumDragging && mouseButton == MouseButton.Left)
        {
            _isSpectrumDragging = false;
            if (Math.Abs(mousePosition.X - _spectrumDragStartPoint.X) < 3)
            {
                if (_spectrumClickHandler != null) _spectrumClickHandler.OnClick(mousePosition, 1);
            }
        }
        _isSpectrumDragging = false;
        StartSdrCenterSnap();
    }

    [RelayCommand]
    private void SpectrumMouseMove(System.Windows.Point position)
    {
        if (_isSpectrumDragging) StopSdrCenterSnap();
        ResetCursorTimer();
        RadioControl radioControl = _engine.Control;
        int roundingHz = Display.CurrentMainRoundingHz;
        bool isDragging = false;
        if (_isSpectrumDragging)
        {
            double dragDeltaX = position.X - _spectrumDragStartPoint.X;
            if (Math.Abs(dragDeltaX) >= 3 || _spectrumDragStartCenterFreq != radioControl.CenterFreqHz)
            {
                int newCenter = GetDraggedMainCenterHz(_spectrumDragStartCenterFreq, dragDeltaX, SpectrumWidth, roundingHz);
                if (newCenter != radioControl.CenterFreqHz)
                {
                    radioControl.CenterFreqHz = Math.Clamp(newCenter, 0, 2000000000);
                    radioControl.FreqOffsetHz = radioControl.TunedFreqHz - radioControl.CenterFreqHz;
                    radioControl.ApplyPrimaryReceiverTuning();
                    isDragging = true;
                }
            }
        }
        radioControl.CursorFreqOffsetHz = RenderUtils.XToFreqOffset((float)position.X, SpectrumWidth, Display.CurrentMainSpanHz);
        radioControl.CursorFreqHz = radioControl.CenterFreqHz + radioControl.CursorFreqOffsetHz;
        radioControl.CursorHistorySec = -1;
        radioControl.CursorPowerDb = (int)(100.0 * position.Y / SpectrumHeight);
        radioControl.CursorPoint = position;
        _engine.Control = radioControl;
        if (isDragging)
        {
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
            RestartCenterSnapDelayTimer();
        }
        else
            QueueCursorUpdate(radioControl);
    }

    [RelayCommand]
    private void WaterfallMouseDown(object argument)
    {
        StopSdrCenterSnap();
        if (!ExtractMouseInfo(argument, out Point mousePosition, out MouseButton mouseButton) || mouseButton != MouseButton.Left) return;
        if (mousePosition.X <= 50 || mousePosition.X >= (WaterfallWidth - 50)) return;

        bool isInsidePrimaryReceiverFrame = IsInsideZoomFrame(mousePosition, WaterfallOverlay.WfZoomLeft, WaterfallOverlay.WfZoomWidth, WaterfallOverlay.WfRpY, WaterfallOverlay.WfRpH);

        if (isInsidePrimaryReceiverFrame && WaterfallOverlay.ZwBandVisible == Visibility.Visible)
        {
            _isWaterfallDraggingFrame = true;
            if (_waterfallClickHandler != null) _waterfallClickHandler.OnClick(mousePosition);
        }
        else
        {
            _isWaterfallDraggingCenter = true;
            _waterfallDragStartPoint = mousePosition;
            _waterfallDragStartCenterFreq = _engine.Control.CenterFreqHz;
        }
    }

    private bool IsInsideZoomFrame(Point point, double left, double width, double top, double height)
    {
        const double MARGIN = 15;
        return point.X >= (left - MARGIN) && point.X <= (left + width + MARGIN) &&
               point.Y >= (top - MARGIN) && point.Y <= (top + height + MARGIN);
    }

    [RelayCommand]
    private void WaterfallMouseUp(object argument)
    {
        if (!ExtractMouseInfo(argument, out Point mousePosition, out MouseButton mouseButton)) return;
        if (_isWaterfallDraggingCenter && mouseButton == MouseButton.Left)
        {
            _isWaterfallDraggingCenter = false;
            if (Math.Abs(mousePosition.X - _waterfallDragStartPoint.X) < 3 && Math.Abs(mousePosition.Y - _waterfallDragStartPoint.Y) < 3)
            {
                if (_waterfallClickHandler != null) _waterfallClickHandler.OnClick(mousePosition, 1);
            }
        }
        _isWaterfallDraggingCenter = false;
        _isWaterfallDraggingFrame = false;
        StartSdrCenterSnap();
    }

    [RelayCommand]
    private void WaterfallMouseMove(System.Windows.Point position)
    {
        if (_isWaterfallDraggingCenter || _isWaterfallDraggingFrame) StopSdrCenterSnap();
        ResetCursorTimer();
        RadioControl radioControl = _engine.Control;
        int roundingHz = Display.CurrentMainRoundingHz;
        int mainSpanHz = Display.CurrentMainSpanHz;
        radioControl.CursorFreqOffsetHz = RenderUtils.XToFreqOffset((float)position.X, WaterfallWidth, mainSpanHz);
        radioControl.CursorFreqHz = radioControl.CenterFreqHz + radioControl.CursorFreqOffsetHz;
        double height = Math.Max(1.0, WaterfallHeight);
        int maxHistorySec = GetMaxHistorySec();
        // Keep the cursor mapped to the full waterfall depth so it can move past the IQ buffer limit.
        radioControl.CursorHistorySec = Math.Clamp(RenderUtils.YToSec((float)(position.Y - WaterfallTimeModel.TopLabelHeightPx), height, CurrentWaterfallHistorySeconds), 0, (int)CurrentWaterfallHistorySeconds);
        radioControl.CursorPowerDb = -1;
        radioControl.CursorPoint = position;
        
        bool isDragging = false;
        if (_isWaterfallDraggingFrame) 
        { 
            double halfFullSpan = mainSpanHz / 2.0;
            double halfBoxSpan = radioControl.SpanHz / 2.0;
            double minLimit = -halfFullSpan + halfBoxSpan;
            double maxLimit = Math.Max(minLimit, halfFullSpan - halfBoxSpan);
            radioControl.FreqOffsetHz = (int)Math.Clamp(radioControl.CursorFreqOffsetHz, minLimit, maxLimit);
            radioControl.HistorySec = Math.Clamp(radioControl.CursorHistorySec, 0, maxHistorySec); 
            radioControl.ApplyPrimaryReceiverTuning();
            isDragging = true;
        }
        else if (_isWaterfallDraggingCenter)
        {
            double dragDeltaX = position.X - _waterfallDragStartPoint.X;
            if (Math.Abs(dragDeltaX) >= 3 || _waterfallDragStartCenterFreq != radioControl.CenterFreqHz)
            {
                int newCenter = GetDraggedMainCenterHz(_waterfallDragStartCenterFreq, dragDeltaX, WaterfallWidth, roundingHz);
                if (newCenter != radioControl.CenterFreqHz)
                {
                    radioControl.CenterFreqHz = Math.Clamp(newCenter, 0, 2000000000);
                    radioControl.FreqOffsetHz = radioControl.TunedFreqHz - radioControl.CenterFreqHz;
                    radioControl.ApplyPrimaryReceiverTuning();
                    isDragging = true;
                }
            }
        }
        _engine.Control = radioControl;
        if (isDragging)
        {
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
            RestartCenterSnapDelayTimer();
        }
        else
            QueueCursorUpdate(radioControl);
    }

    [RelayCommand]
    private void HandleMouseLeave()
    {
        ClearHoverCursor();
        _isZoomDragging = false;
        _isSpectrumDragging = false;
        _isWaterfallDraggingCenter = false;
        StartSdrCenterSnap();
    }

    private int GetDraggedMainCenterHz(int startCenterHz, double dragDeltaX, double viewWidth, int roundingHz)
    {
        double hzPerPixel = (double)Display.CurrentMainSpanHz / Math.Max(1.0, viewWidth);
        int centerHz = (int)Math.Round(startCenterHz - dragDeltaX * hzPerPixel);

        if (Display.IsMainViewZoomed)
        {
            double sdrCenter = _engine.MainFftCenterFreqHz;
            double baseSpan = Display.BaseMainSpanHz;
            double currentSpan = Display.CurrentMainSpanHz;
            double maxOffset = Math.Max(0.0, (baseSpan - currentSpan) / 2.0);
            double minCenter = sdrCenter - maxOffset;
            double maxCenter = sdrCenter + maxOffset;
            return (int)Math.Clamp(centerHz, minCenter, maxCenter);
        }
        else
        {
            return (int)Math.Clamp(centerHz, 0.0, 2_000_000_000.0);
        }
    }

    private bool PanMainView(ref RadioControl radioControl, double dragDeltaX, double viewWidth)
    {
        if (Math.Abs(dragDeltaX) <= double.Epsilon) return false;
        double hzPerPixel = (double)Display.CurrentMainSpanHz / Math.Max(1.0, viewWidth);
        int centerHz = (int)Math.Round(radioControl.CenterFreqHz - dragDeltaX * hzPerPixel);

        if (Display.IsMainViewZoomed)
        {
            double sdrCenter = _engine.MainFftCenterFreqHz;
            double baseSpan = Display.BaseMainSpanHz;
            double currentSpan = Display.CurrentMainSpanHz;
            double maxOffset = Math.Max(0.0, (baseSpan - currentSpan) / 2.0);
            double minCenter = sdrCenter - maxOffset;
            double maxCenter = sdrCenter + maxOffset;

            centerHz = (int)Math.Clamp(centerHz, minCenter, maxCenter);
        }
        else
        {
            centerHz = (int)Math.Clamp(centerHz, 0.0, 2_000_000_000.0);
        }

        if (centerHz == radioControl.CenterFreqHz) return false;

        radioControl.CenterFreqHz = centerHz;
        radioControl.FreqOffsetHz = radioControl.TunedFreqHz - centerHz;
        radioControl.ApplyPrimaryReceiverTuning();
        return true;
    }

    private void ClearHoverCursor()
    {
        CancelPendingCursorUpdate();
        RadioControl radioControl = _engine.Control;
        radioControl.CursorFreqHz = -1; radioControl.CursorFreqOffsetHz = -1; radioControl.CursorHistorySec = -1; radioControl.CursorPowerDb = -1;
        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl, IsCursorOnly: true));
    }

}
