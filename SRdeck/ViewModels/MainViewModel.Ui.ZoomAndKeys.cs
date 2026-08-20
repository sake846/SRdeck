using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    private static readonly DemodWaveMode[] AvailableDemodWaveModes =
    {
        DemodWaveMode.Wave,
        DemodWaveMode.FFT,
        DemodWaveMode.Lissajous,
        DemodWaveMode.Vector,
        DemodWaveMode.Compare
    };

    private static DemodWaveMode GetNextDemodWaveMode(DemodWaveMode currentMode)
    {
        int currentIndex = Array.IndexOf(AvailableDemodWaveModes, currentMode);
        if (currentIndex < 0)
        {
            return AvailableDemodWaveModes[0];
        }

        return AvailableDemodWaveModes[(currentIndex + 1) % AvailableDemodWaveModes.Length];
    }

    [RelayCommand]
    private void ZoomWindowClick(string region) => HandleZoomWindowRegionClick(region, false);

    public void HandleZoomWindowRegionClick(string region, bool skipTeleport)
    {
        RadioControl radioControl = _engine.Control;
        int oldFreqOffset = radioControl.FreqOffsetHz;
        int oldHistorySec = radioControl.HistorySec;

        if (_zoomWindowClickHandler != null)
            _zoomWindowClickHandler.ProcessZoomRegionClick(region, ref radioControl, 1, GetMaxHistorySec());

        if (!skipTeleport)
        {
            ApplyZoomWindowAdjustment(ref radioControl, oldFreqOffset, oldHistorySec);
        }
        else
        {
            radioControl.ApplyPrimaryReceiverTuning();
            _engine.Control = radioControl;
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
    }

    private void ApplyZoomWindowAdjustment(ref RadioControl radioControl, int oldFreqOffset, int oldHistorySec)
    {
        radioControl.ApplyPrimaryReceiverTuning();

        int currentFreqOffset = radioControl.FreqOffsetHz;
        int currentHistorySec = radioControl.HistorySec;
        int df = currentFreqOffset - oldFreqOffset;
        int deltaSec = currentHistorySec - oldHistorySec;

        if (WaterfallWidth > 0 && WaterfallHeight > 0 && (df != 0 || deltaSec != 0))
        {
            double logicalDx = (double)df * WaterfallWidth / Display.CurrentMainSpanHz;
            double logicalDy = WaterfallTimeModel.SecondsToY(deltaSec, WaterfallHeight, CurrentWaterfallHistorySeconds);
            TeleportCursorRelative(logicalDx, logicalDy);
        }

        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
    }

    private void TeleportCursorRelative(double dx, double dy)
    {
        var source = PresentationSource.FromVisual(Application.Current.MainWindow);
        if (source?.CompositionTarget == null) return;

        double dpiX = source.CompositionTarget.TransformToDevice.M11;
        double dpiY = source.CompositionTarget.TransformToDevice.M22;

        _ = Task.Run(async () => 
        {
            await Task.Delay(40);
            if (GetCursorPos(out Win32Point latestPos))
            {
                int newX = latestPos.X + (int)Math.Round(dx * dpiX);
                int newY = latestPos.Y + (int)Math.Round(dy * dpiY);
                SetCursorPos(newX, newY);
            }
        });
    }

    [RelayCommand]
    private void ZoomWindowImageClick(System.Windows.Point position) => BeginZoomDrag(position);

    private void BeginZoomDrag(Point position)
    {
        if (Application.Current?.MainWindow == null) return;
        
        _isZoomDragging = true;
        _zoomDragStartAbsolute = Mouse.GetPosition(Application.Current.MainWindow);
        _zoomDragStartFreqOffset = _engine.Control.FreqOffsetHz;
        _zoomDragStartHistorySec = _engine.Control.HistorySec;
    }

    [RelayCommand]
    private void ZoomMouseUp(System.Windows.Point position) => CompleteZoomDrag(position);

    private void CompleteZoomDrag(Point position)
    {
        if (!_isZoomDragging || Application.Current?.MainWindow == null) return;

        _isZoomDragging = false;

        Point startAbs = _zoomDragStartAbsolute;
        Point currentAbsolute = Mouse.GetPosition(Application.Current.MainWindow);
        double dx = currentAbsolute.X - startAbs.X;
        double dy = currentAbsolute.Y - startAbs.Y;
        
        if (Math.Abs(dx) < 3 && Math.Abs(dy) < 3)
        {
            RadioControl radioControl = _engine.Control;
            int oldFreq = radioControl.FreqOffsetHz;
            int oldSec = radioControl.HistorySec;

            if (_zoomWindowClickHandler != null)
            {
                _zoomWindowClickHandler.OnClick(position);
                _zoomWindowClickHandler.SyncClickParameters(ref radioControl, 1, GetMaxHistorySec());
                ApplyZoomWindowAdjustment(ref radioControl, oldFreq, oldSec);
            }
        }
    }

    [RelayCommand]
    private void ZoomMouseMove(System.Windows.Point position) => SyncZoomDrag(position);

    private void SyncZoomDrag(Point position)
    {
        ClearHoverCursor();
        if (!_isZoomDragging || Application.Current?.MainWindow == null) return;

        Point startAbs = _zoomDragStartAbsolute;
        Point currentAbsolute = Mouse.GetPosition(Application.Current.MainWindow);
        double dx = currentAbsolute.X - startAbs.X;
        double dy = currentAbsolute.Y - startAbs.Y;
        
        double wfWidth = WfActualWidth > 0 ? WfActualWidth : WaterfallWidth;
        if (wfWidth > 0 && WaterfallHeight > 0)
        {
            double df = dx * Display.CurrentMainSpanHz / wfWidth;
            double dSec = dy * CurrentWaterfallHistorySeconds / WaterfallHeight;
            
            RadioControl radioControl = _engine.Control;
            radioControl.FreqOffsetHz = _zoomDragStartFreqOffset + (int)df;
            radioControl.HistorySec = Math.Clamp(_zoomDragStartHistorySec + (int)dSec, 0, GetMaxHistorySec());
            radioControl.ApplyPrimaryReceiverTuning();
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
    }

    [RelayCommand]
    private void DemodWaveClick(object commandObject) => HandleDemodWaveCommand(commandObject, 1);

    private void HandleDemodWaveCommand(object commandObject, int index)
    {
        if (commandObject is DemodWaveCommandType commandType)
        {
            RadioControl radioControl = _engine.Control;
            ApplyDemodWaveCommand(commandType, ref radioControl);
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
    }

    private void ApplyDemodWaveCommand(DemodWaveCommandType commandType, ref RadioControl parameters)
    {
        switch (commandType)
        {
            case DemodWaveCommandType.Debug:
                parameters.IsDebugVisible = !parameters.IsDebugVisible;
                break;
            case DemodWaveCommandType.Station:
                parameters.DemodWaveDisplayMode = GetNextDemodWaveMode(parameters.DemodWaveDisplayMode);
                DemodWaveDisplayMode = parameters.DemodWaveDisplayMode;
                break;
            case DemodWaveCommandType.Color:
            case DemodWaveCommandType.Green:
            case DemodWaveCommandType.Amber:
                parameters.WaterfallColorMode = 0;
                break;
        }
    }

    public void ApplyDemodWaveModeDirect(int receiverIndex, DemodWaveMode mode)
    {
        RadioControl radioControl = _engine.Control;
        if (receiverIndex == 1)
        {
            radioControl.DemodWaveDisplayMode = mode;
            DemodWaveDisplayMode = radioControl.DemodWaveDisplayMode;
        }
        _engine.Control = radioControl;
        WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
    }

    [RelayCommand]
    private void Hotkey(string action)
    {
        RadioControl radioControl = _engine.Control;

        if (action.StartsWith("Q"))
        {
            ApplyReceiverCommand(ReceiverCommandType.SquelchToggle, ref radioControl);
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
        else if (action.StartsWith("F"))
        {
            OpenFrequencyInputDialog();
        }
        else if (action.StartsWith("M"))
        {
            ApplyReceiverCommand(ReceiverCommandType.MuteToggle, ref radioControl);
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
        else if (action.StartsWith("P"))
        {
            ApplyReceiverCommand(ReceiverCommandType.PowerToggle, ref radioControl);
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
        else if (action.StartsWith("R"))
        {
            ToggleReceiver1VisibilityCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void CloseHelp() => IsHelpVisible = false;

    [RelayCommand]
    private void ToggleHelp() => IsHelpVisible = !IsHelpVisible;

    [RelayCommand]
    internal void ArrowKey(string direction)
    {
        RadioControl radioControl = _engine.Control;
        
        bool changed = true;
        switch (direction)
        {
            case "Left": radioControl.FreqOffsetHz -= radioControl.StepHz; break;
            case "Right": radioControl.FreqOffsetHz += radioControl.StepHz; break;
            case "Up": radioControl.HistorySec = Math.Max(0, radioControl.HistorySec - 1); break;
            case "Down": radioControl.HistorySec = Math.Min(GetMaxHistorySec(), radioControl.HistorySec + 1); break;
            default: changed = false; break;
        }
        if (changed)
        {
            radioControl.ApplyPrimaryReceiverTuning();
            WeakReferenceMessenger.Default.Send(new RadioControlUpdateMessage(radioControl));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] private static extern bool GetCursorPos(out Win32Point lpPoint);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Win32Point { public int X; public int Y; }

    private static int GetMaxHistorySec() => AppConstants.MAX_HISTORY_SEC;
}

