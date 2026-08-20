using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeck.Models;
using SRdeck.Renderers;
using SRdeck.Services;
using SRdeckPlugin.Contracts;

namespace SRdeck.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private void SyncOverlayVisuals(RadioControl radioControl, RadioState radioState)
    {
        double spectrumWidth = SpActualWidth > 0 ? SpActualWidth : SpectrumWidth;
        double waterfallWidth = WfActualWidth > 0 ? WfActualWidth : WaterfallWidth;
        int mainSpan = Display.CurrentMainSpanHz;
        double waterfallHistorySeconds = CurrentWaterfallHistorySeconds;

        float? configGridTopDb = SelectedGridTopDb?.Value;
        IReadOnlyList<FrequencyOverlayItem>? receiverBands = null;
        IReadOnlyList<WaterfallAnnotationItem>? waterfallAnnotations = null;
        DateTimeOffset? waterfallReferenceTime = null;
        if (_pluginManager.TryGetActiveCapability<IFrequencyOverlayProvider>(out IFrequencyOverlayProvider? overlayProvider) &&
            overlayProvider is not null)
        {
            receiverBands = overlayProvider.FrequencyOverlays;
        }
        if (_pluginManager.TryGetActiveCapability<IWaterfallAnnotationProvider>(
                out IWaterfallAnnotationProvider? annotationProvider) &&
            annotationProvider is not null)
        {
            waterfallAnnotations = annotationProvider.WaterfallAnnotations;
            waterfallReferenceTime = annotationProvider.WaterfallReferenceTime;
        }
        SpectrumOverlay.SyncOverlayLayout(radioControl, radioState, Display.SpectrumBiasAdj, Display.WaterfallBiasAdj, IsAnySourceActive, spectrumWidth, SpectrumHeight, IsReceiver1Visible, false, configGridTopDb, mainSpan, receiverBands);
        WaterfallOverlay.SyncOverlayLayout(
            radioControl,
            waterfallWidth,
            WaterfallHeight,
            ZoomWindowWidth,
            IsAnySourceActive,
            IsReceiver1Visible,
            false,
            mainSpan,
            Display.CurrentMainRoundingHz,
            MainGridAnchorEnabled,
            MainGridAnchorFrequencyHz,
            MainGridAnchorRatio,
            waterfallAnnotations,
            waterfallReferenceTime,
            waterfallHistorySeconds,
            WaterfallDisplayTimeMode);

        if (waterfallWidth > 100)
            SyncZoomWindowLayouts(radioControl, waterfallWidth, waterfallHistorySeconds);
    }

    private void SyncCursorOverlayVisuals(RadioControl radioControl)
    {
        double spectrumWidth = SpActualWidth > 0 ? SpActualWidth : SpectrumWidth;
        double waterfallWidth = WfActualWidth > 0 ? WfActualWidth : WaterfallWidth;
        int mainSpan = Display.CurrentMainSpanHz;

        SpectrumOverlay.SyncCursorLayout(radioControl, spectrumWidth, SpectrumHeight, IsReceiver1Visible, false, mainSpan);
        WaterfallOverlay.SyncCursorLayout(radioControl, waterfallWidth, WaterfallHeight, IsReceiver1Visible, false, mainSpan, CurrentWaterfallHistorySeconds);
    }

    private void SyncZoomWindowLayouts(RadioControl radioControl, double waterfallWidth, double waterfallHistorySeconds)
    {
        double zoomWindowWidth1 = ZoomOverlay.IsEmbedded ? ZoomWindowWidth : 418;
        double zoomWindowHeight1 = ZoomOverlay.IsEmbedded ? ZoomWindowHeight : 198;

        var (x1, y1, v1) = ZoomOverlay.GetDesiredLayout(radioControl, zoomWindowWidth1, zoomWindowHeight1, waterfallWidth, WaterfallHeight, 1, IsReceiver1Visible, waterfallHistorySeconds);

        if (v1)
        {
            double bw = Display.CurrentMainSpanHz;
            double halfBw = bw / 2.0;

            double boxWidth1 = Math.Max(0, Math.Round(((radioControl.FreqOffsetHz + halfBw + radioControl.SpanHz / 2.0) / bw) * waterfallWidth) - Math.Round(((radioControl.FreqOffsetHz + halfBw - radioControl.SpanHz / 2.0) / bw) * waterfallWidth) + 3.0);
            double boxRectX1 = Math.Round(((radioControl.FreqOffsetHz + halfBw - radioControl.SpanHz / 2.0) / bw) * waterfallWidth) - 2.0;
            double boxRectY1 = Math.Round(RenderUtils.SecToY(radioControl.HistorySec, WaterfallHeight, waterfallHistorySeconds)) - WaterfallHeight - 1.0;
            double boxHeight = Math.Round(RenderUtils.SecToY(10, WaterfallHeight, waterfallHistorySeconds)) + 2.0;
            Rect boxRect1 = v1 ? new Rect(boxRectX1, boxRectY1, boxWidth1, boxHeight) : Rect.Empty;

            ResolveZoomConflict(ref x1, ref y1, Rect.Empty, boxRect1, Rect.Empty, waterfallWidth, zoomWindowWidth1, zoomWindowHeight1);
        }

        ZoomOverlay.SyncLayout(x1 + 8.0, y1, v1);
        ZoomOverlay.SyncOverlayContent(radioControl, zoomWindowWidth1, 1);
    }

    private void ResolveMutualRepulsion(ref double x1, ref double x2, double waterfallWidth, double zoomWindowWidth1, double zoomWindowWidth2)
    {
        double minX = ZOOM_WINDOW_MIN_PADDING;
        double maxX1 = waterfallWidth - zoomWindowWidth1 - ZOOM_WINDOW_MIN_PADDING;
        double maxX2 = waterfallWidth - zoomWindowWidth2 - ZOOM_WINDOW_MIN_PADDING;
        double minDist = ((zoomWindowWidth1 + zoomWindowWidth2) / 2.0) + ZOOM_WINDOW_CONFLICT_DIST;
        double currentDist = Math.Abs(x1 - x2);
        
        if (currentDist < minDist)
        {
            double push = minDist - currentDist;
            if (x1 <= x2) { x1 -= push / 2.0; x2 += push / 2.0; }
            else { x1 += push / 2.0; x2 -= push / 2.0; }
            
            if (x1 < minX) { double extra = minX - x1; x1 = minX; x2 += extra; }
            if (x1 > maxX1) { double extra = x1 - maxX1; x1 = maxX1; x2 -= extra; }
            if (x2 < minX) { double extra = minX - x2; x2 = minX; x1 += extra; }
            if (x2 > maxX2) { double extra = x2 - maxX2; x2 = maxX2; x1 -= extra; }
        }
    }

    private void ResolveZoomConflict(ref double x, ref double y, Rect otherWindow, Rect boxRect1, Rect boxRect2, double waterfallWidth, double zoomWindowWidth, double zoomWindowHeight)
    {
        Rect self = new Rect(x, y, zoomWindowWidth, zoomWindowHeight);
        if (!self.IntersectsWith(otherWindow) && !self.IntersectsWith(boxRect1) && !self.IntersectsWith(boxRect2)) return;

        double originalX = x;
        double originalY = y;

        // --- Step 1: ABSOLUTE PRIORITY - Horizontal Shifting ---
        double hMargin = 20; // 20px gap as requested
        double edgePadding = 8;
        double minX = edgePadding; 
        double maxX = waterfallWidth - zoomWindowWidth - edgePadding;
        
        var candidates = new List<double> { minX, maxX };
        
        // Add positions just outside obstacles with 20px gap
        void AddEdges(Rect r) {
            if (r.IsEmpty) return;
            candidates.Add(r.Left - zoomWindowWidth - hMargin);
            candidates.Add(r.Right + hMargin);
        }
        AddEdges(otherWindow);
        AddEdges(boxRect1);
        AddEdges(boxRect2);

        // Sort by distance from original X
        foreach (var c in candidates.OrderBy(val => Math.Abs(val - originalX)))
        {
            double safeMaxX = Math.Max(minX, maxX);
            double tcx = Math.Clamp(c, minX, safeMaxX);
            Rect testX = new Rect(tcx, originalY, zoomWindowWidth, zoomWindowHeight);
            
            // Check collision with 19px horizontal margin to allow 20px gap to pass
            double checkH = hMargin - 1; 
            if (!testX.IntersectsWith(InflateRect(otherWindow, checkH, 5)) && 
                !testX.IntersectsWith(InflateRect(boxRect1, checkH, 5)) && 
                !testX.IntersectsWith(InflateRect(boxRect2, checkH, 5))) 
            { 
                x = tcx; 
                return; 
            }
        }

        // --- Step 2: FALLBACK - Vertical Shifting ---
        double vMargin = 10;
        double pushDir = (!otherWindow.IsEmpty && originalY > otherWindow.Top) ? 1 : -1;
        double offsetStep = zoomWindowHeight + vMargin;
        double[] yOffsets = { pushDir * offsetStep, -pushDir * offsetStep };
        
        foreach (double dy in yOffsets)
        {
            double ty = originalY + dy;
            // Ensure bottom buttons are not hidden: Keep 5px margin from top/bottom edges
            if (ty < -WaterfallHeight + 5 || ty > -zoomWindowHeight - 5) continue; 
            Rect testY = new Rect(originalX, ty, zoomWindowWidth, zoomWindowHeight);

            if (!testY.IntersectsWith(InflateRect(otherWindow, 10, 5)) && 
                !testY.IntersectsWith(InflateRect(boxRect1, 10, 5)) && 
                !testY.IntersectsWith(InflateRect(boxRect2, 10, 5))) 
            { 
                y = ty; 
                return; 
            }
        }
    }

    private Rect InflateRect(Rect rect, double deltaX, double deltaY)
    {
        if (rect.IsEmpty) return Rect.Empty;
        Rect res = rect;
        res.Inflate(deltaX, deltaY);
        return res;
    }
}
