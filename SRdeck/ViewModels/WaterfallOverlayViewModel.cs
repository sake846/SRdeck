using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using SRdeckPlugin.Contracts;
using SRdeck.Models;
using SRdeck.Renderers;
using SRdeck.ViewModels.Components;

namespace SRdeck.ViewModels
{
    public partial class WaterfallOverlayViewModel : ObservableObject
    {
        [ObservableProperty] private double _wfRpY;
        [ObservableProperty] private double _wfRpH;
        [ObservableProperty] private double _wfZoomLeft;
        private double _wfRawZoomLeft;
        public double WfRawZoomLeft { get => _wfRawZoomLeft; set => SetProperty(ref _wfRawZoomLeft, value); }
        [ObservableProperty] private double _wfZoomWidth;
        [ObservableProperty] private double _wfCsY;
        [ObservableProperty] private Visibility _wfCsYVisible = Visibility.Hidden;
        [ObservableProperty] private string _wfCsText = "";
        [ObservableProperty] private double _wfCsTextX;
        [ObservableProperty] private double _wfCsTextY;
        [ObservableProperty] private double _spCsHotspotX;
        [ObservableProperty] private double _spCsHotspotYMin;
        [ObservableProperty] private double _spCsHotspotYMax;

        [ObservableProperty] private Visibility _wfShortCsVisible = Visibility.Collapsed;
        [ObservableProperty] private double _wfShortCsX1;
        [ObservableProperty] private double _wfShortCsX2;
        [ObservableProperty] private double _wfShortCsY1;
        [ObservableProperty] private double _wfShortCsY2;
        [ObservableProperty] private double _wfShortCsCenterX;
        [ObservableProperty] private double _wfShortCsCenterY;


        [ObservableProperty] private double _spCsLineX;
        [ObservableProperty] private double _spCsRightEdge;
        [ObservableProperty] private Visibility _spCsVisible = Visibility.Hidden;
        [ObservableProperty] private double _spCsOpacity = 1.0;
        
        [ObservableProperty] private Visibility _zwBandVisible = Visibility.Hidden;

        [ObservableProperty] private double _wfRpY2;
        [ObservableProperty] private double _wfRpH2;
        [ObservableProperty] private double _wfZoomLeft2;
        [ObservableProperty] private double _wfZoomWidth2;
        [ObservableProperty] private Visibility _zwBandVisible2 = Visibility.Hidden;

        [ObservableProperty] private double _waterfallWidth = 10;
        [ObservableProperty] private double _waterfallHeight = 10;
        [ObservableProperty] private bool _isWfCsDashed;

        public ObservableCollection<WaterfallXLabel> WaterfallXLabels { get; } = new ObservableCollection<WaterfallXLabel>();
        public ObservableCollection<WaterfallYLabel> WaterfallYLabels { get; } = new ObservableCollection<WaterfallYLabel>();
        public ObservableCollection<WaterfallAnnotationVisual> Annotations { get; } = new();

        public void SyncOverlayLayout(RadioControl radioControl, double waterfallWidth, double waterfallHeight, double zoomWidth, bool isStarted, bool isReceiver1Visible = true, bool isReceiver2Visible = false, double displayBw = 7000000.0, int roundingHz = 500000, bool gridAnchorEnabled = false, double gridAnchorFrequencyHz = 0.0, double gridAnchorRatio = 0.5, IReadOnlyList<WaterfallAnnotationItem>? annotations = null, DateTimeOffset? annotationReferenceTime = null, double totalHistorySeconds = WaterfallTimeModel.TotalHistorySeconds, WaterfallTimeMode timeMode = WaterfallTimeMode.ThreeMinutes)
        {
            double halfDisplayBw = displayBw / 2.0;
            double spectrumWidth = waterfallWidth; 
            bool isZoomActive = isReceiver1Visible && radioControl.IsPowerOn && zoomWidth > 0 && radioControl.SpanHz > 0 && radioControl.IsZoomWindowVisible;

            if (isReceiver1Visible && radioControl.IsPowerOn && spectrumWidth > 0 && radioControl.SpanHz > 0) {
                double height = Math.Max(1.0, waterfallHeight);
                WfRpY = Math.Round(RenderUtils.SecToY(radioControl.HistorySec, height, totalHistorySeconds)) - 1;
                
                if (isZoomActive) {
                    WfRpH = Math.Round(RenderUtils.SecToY(10, height, totalHistorySeconds)) + 1.0 + 1;
                    double rawLeftX = ((radioControl.FreqOffsetHz + halfDisplayBw - radioControl.SpanHz / 2.0) / displayBw) * spectrumWidth;
                    double rawRightX = ((radioControl.FreqOffsetHz + halfDisplayBw + radioControl.SpanHz / 2.0) / displayBw) * spectrumWidth;
                    double finalLeftX = Math.Max(0, Math.Round(rawLeftX));
                    double finalRightX = Math.Min(spectrumWidth, Math.Round(rawRightX));
                    WfRawZoomLeft = rawLeftX;
                    WfZoomLeft = finalLeftX - 1;
                    WfZoomWidth = Math.Max(0, finalRightX - finalLeftX + 1);
                } else {
                    WfRpH = 0; WfZoomLeft = -1000; WfZoomWidth = 0;
                }
            } else {
                WfRpH = 0; WfZoomLeft = -1000; WfZoomWidth = 0;
            }

            WfRpH2 = 0; WfZoomLeft2 = -1000; WfZoomWidth2 = 0;

            UpdateCursorLayoutInternal(radioControl, waterfallWidth, waterfallHeight, displayBw, totalHistorySeconds);

            ZwBandVisible = isZoomActive ? Visibility.Visible : Visibility.Hidden;
            ZwBandVisible2 = Visibility.Hidden;

            SyncAnnotations(radioControl, waterfallWidth, waterfallHeight, displayBw,
                annotations, annotationReferenceTime, totalHistorySeconds);

            // Waterfall X Labels (周波数軸ラベル)
            if (spectrumWidth > 50 && waterfallHeight > 0) {
                double safeDisplayBw = Math.Max(1.0, displayBw);
                double mainStepHz = RenderUtils.GetFrequencyGridSteps(safeDisplayBw).MainHz;
                double halfSpan = safeDisplayBw * 0.5;
                long kMinValue = (long)Math.Ceiling((radioControl.CenterFreqHz - halfSpan) / mainStepHz);
                long kMaxValue = (long)Math.Floor((radioControl.CenterFreqHz + halfSpan) / mainStepHz);
                int neededCount = (int)Math.Max(0L, kMaxValue - kMinValue + 1L);

                int decimalPlaces = 1;
                for (; decimalPlaces < 6; decimalPlaces++)
                {
                    double quantumHz = Math.Pow(10.0, 6 - decimalPlaces);
                    bool canRepresentAllTicks = true;
                    for (long k = kMinValue; k <= kMaxValue; k++)
                    {
                        double absoluteHz = k * mainStepHz;
                        double relHz = absoluteHz - radioControl.CenterFreqHz;
                        if (relHz < -halfSpan || relHz > halfSpan) continue;
                        double labelHz = Math.Round(radioControl.CenterFreqHz == 0 ? relHz : absoluteHz);
                        double representedHz = Math.Round(labelHz / quantumHz) * quantumHz;
                        if (Math.Abs(representedHz - labelHz) > 0.5)
                        {
                            canRepresentAllTicks = false;
                            break;
                        }
                    }
                    if (canRepresentAllTicks) break;
                }
                string labelFormat = $"F{decimalPlaces}";
                
                // コレクションの個数を調整
                while (WaterfallXLabels.Count < neededCount) WaterfallXLabels.Add(new WaterfallXLabel());
                while (WaterfallXLabels.Count > neededCount) WaterfallXLabels.RemoveAt(WaterfallXLabels.Count - 1);

                int index = 0;
                for (long k = kMinValue; k <= kMaxValue; k++) {
                    double absoluteHz = k * mainStepHz;
                    double relHz = absoluteHz - radioControl.CenterFreqHz;
                    if (relHz < -halfSpan || relHz > halfSpan) continue;
                    double relativeHz = Math.Round(relHz);
                    double currentFreqHz = Math.Round(absoluteHz);
                    double relativeMhz = relativeHz / 1000000.0;
                    double currentFreqMhz = currentFreqHz / 1000000.0;
                    
                    string labelText = (radioControl.CenterFreqHz == 0) 
                        ? (relativeMhz == 0 ? "0" : (relativeMhz > 0 ? "+" + relativeMhz.ToString(labelFormat) : relativeMhz.ToString(labelFormat)))
                        : currentFreqMhz.ToString(labelFormat);
                    
                    double textWidth = labelText.Length * 7.5;
                    double pixelX = ((relHz + halfSpan) / safeDisplayBw) * spectrumWidth;
                    
                    double minLimit = 2.0;
                    double maxLimit = Math.Max(minLimit, spectrumWidth - textWidth);
                    double finalX = Math.Clamp(pixelX - (textWidth / 2.0), minLimit, maxLimit);

                    WaterfallXLabels[index].X = Math.Round(finalX);
                    WaterfallXLabels[index].Text = labelText;
                    index++;
                }
            }

            // Waterfall Y Labels
            if (waterfallHeight > 0 && spectrumWidth > 0) {
                int labelCount = 17;
                int tickIntervalSeconds = 0;
                if (timeMode == WaterfallTimeMode.Uncompressed)
                {
                    tickIntervalSeconds = WaterfallTimeModel.GetUncompressedTickIntervalSeconds(totalHistorySeconds);
                    labelCount = Math.Max(0, (int)Math.Ceiling(totalHistorySeconds / tickIntervalSeconds) - 1);
                }

                while (WaterfallYLabels.Count < labelCount) WaterfallYLabels.Add(new WaterfallYLabel());
                while (WaterfallYLabels.Count > labelCount) WaterfallYLabels.RemoveAt(WaterfallYLabels.Count - 1);
                for (int index = 0; index < labelCount; index++) {
                    int i = index + 1;
                    double seconds = timeMode == WaterfallTimeMode.Uncompressed
                        ? i * tickIntervalSeconds
                        : totalHistorySeconds * i / 18.0;
                    double yBase = Math.Round((seconds / Math.Max(double.Epsilon, totalHistorySeconds)) * waterfallHeight);
                    WaterfallYLabels[i-1].Y = yBase - 6;
                    WaterfallYLabels[i-1].YLine = yBase;
                    string timeLabel = FormatHistoryLabel(seconds);
                    WaterfallYLabels[i-1].TextLeft = timeLabel;
                    WaterfallYLabels[i-1].TextRight = timeLabel;
                    WaterfallYLabels[i-1].XRightText = Math.Round(spectrumWidth - 27);
                    WaterfallYLabels[i-1].XRightLine1 = Math.Round(spectrumWidth - 5);
                    WaterfallYLabels[i-1].XRightLine2 = Math.Round(spectrumWidth);
                }
            }
        }

        private void SyncAnnotations(RadioControl radioControl, double waterfallWidth,
            double waterfallHeight, double displayBw,
            IReadOnlyList<WaterfallAnnotationItem>? annotations,
            DateTimeOffset? referenceTime,
            double totalHistorySeconds)
        {
            if (annotations is null || referenceTime is null || waterfallWidth <= 0 ||
                waterfallHeight <= 0 || displayBw <= 0)
            {
                Annotations.Clear();
                return;
            }

            double halfDisplayBw = displayBw / 2.0;
            double displayMinimumHz = radioControl.CenterFreqHz - halfDisplayBw;
            double displayMaximumHz = radioControl.CenterFreqHz + halfDisplayBw;
            const double markerWidth = 12.0;
            const double markerHeight = 8.0;
            const double labelWidth = 16.0;
            var visible = new List<(WaterfallAnnotationItem Item, double X, double Y,
                double Width, double Height)>();
            foreach (WaterfallAnnotationItem item in annotations)
            {
                if (item.FrequencyHz < displayMinimumHz ||
                    item.FrequencyHz > displayMaximumHz) continue;
                double ageSeconds = (referenceTime.Value - item.Time).TotalSeconds;
                if (ageSeconds < 0 ||
                    ageSeconds > totalHistorySeconds) continue;

                double centerX = ((item.FrequencyHz - radioControl.CenterFreqHz +
                    halfDisplayBw) / displayBw) * waterfallWidth;
                bool hasLabel = !string.IsNullOrEmpty(item.Label);
                double width = hasLabel ? labelWidth : markerWidth;
                double x = Math.Clamp(centerX - width / 2.0,
                    0.0, Math.Max(0.0, waterfallWidth - width));
                // Put the triangle tip on the decoded signal's end time.
                double tipY = WaterfallTimeModel.SecondsToY(
                    Math.Max(0, ageSeconds - 1.0), waterfallHeight, totalHistorySeconds);
                double y = hasLabel ? tipY : tipY - markerHeight;
                visible.Add((item, Math.Round(x), Math.Round(y), width,
                    hasLabel ? 0.0 : markerHeight));
            }

            var existing = Annotations.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var ordered = new List<WaterfallAnnotationVisual>(visible.Count);
            foreach (var geometry in visible)
            {
                if (!existing.TryGetValue(geometry.Item.Id,
                        out WaterfallAnnotationVisual? visual))
                    visual = new WaterfallAnnotationVisual(geometry.Item.Id);
                visual.X = geometry.X;
                visual.Y = geometry.Y;
                visual.Width = geometry.Width;
                visual.Height = geometry.Height;
                visual.Color = geometry.Item.Color;
                visual.Label = geometry.Item.Label;
                visual.RotationDegrees = visual.HasLabel ? -90.0 : 0.0;
                visual.ToolTip = geometry.Item.ToolTip;
                ordered.Add(visual);
            }

            for (int index = 0; index < ordered.Count; index++)
            {
                if (index < Annotations.Count &&
                    ReferenceEquals(Annotations[index], ordered[index])) continue;
                int current = Annotations.IndexOf(ordered[index]);
                if (current >= 0) Annotations.Move(current, index);
                else Annotations.Insert(index, ordered[index]);
            }
            while (Annotations.Count > ordered.Count)
                Annotations.RemoveAt(Annotations.Count - 1);
        }

        public void SyncCursorLayout(RadioControl radioControl, double waterfallWidth, double waterfallHeight, bool isReceiver1Visible = true, bool isReceiver2Visible = false, double displayBw = 7000000.0, double totalHistorySeconds = WaterfallTimeModel.TotalHistorySeconds)
        {
            UpdateCursorLayoutInternal(radioControl, waterfallWidth, waterfallHeight, displayBw, totalHistorySeconds);
        }

        private void UpdateCursorLayoutInternal(RadioControl radioControl, double waterfallWidth, double waterfallHeight, double displayBw, double totalHistorySeconds)
        {
            double spectrumWidth = waterfallWidth;
            if (radioControl.CursorFreqHz >= 0 && spectrumWidth > 0 && radioControl.SpanHz > 0) {
                double halfDisplayBw = displayBw / 2.0;
                SpCsVisible = Visibility.Visible;
                int zoomSpan = radioControl.SpanHz;

                double rawLeftX = ((radioControl.CursorFreqOffsetHz + halfDisplayBw - zoomSpan / 2.0) / displayBw) * spectrumWidth;
                double rawRightX = ((radioControl.CursorFreqOffsetHz + halfDisplayBw + zoomSpan / 2.0) / displayBw) * spectrumWidth;
                double leftX = Math.Max(0, Math.Round(rawLeftX));
                double rightX = Math.Min(spectrumWidth, Math.Round(rawRightX));
                SpCsLineX = leftX;
                SpCsRightEdge = rightX;
                double cursorX = Math.Round(Math.Clamp(
                    ((radioControl.CursorFreqOffsetHz + halfDisplayBw) / displayBw) * spectrumWidth,
                    0.0,
                    spectrumWidth));
                
                double height = Math.Max(1.0, waterfallHeight);
                bool isScaleArea = radioControl.CursorPoint.Y < 18.0;

                SpCsHotspotX = cursorX;

                if (isScaleArea) {
                    WfShortCsVisible = Visibility.Visible;
                    WfCsYVisible = Visibility.Hidden;
                    double cursorXPosition = cursorX;
                    double cursorYPosition = Math.Round(radioControl.CursorPoint.Y) - 18.0;
                    WfShortCsCenterX = cursorXPosition;
                    WfShortCsCenterY = cursorYPosition;
                    WfShortCsX1 = Math.Max(0, cursorXPosition - 5);
                    WfShortCsX2 = Math.Min(spectrumWidth, cursorXPosition + 5);
                    WfShortCsY1 = cursorYPosition - 5;
                    WfShortCsY2 = cursorYPosition + 5;
                } else if (radioControl.CursorHistorySec >= 0) {
                    double clampedY = Math.Round(RenderUtils.SecToY(radioControl.CursorHistorySec, height, totalHistorySeconds));
                    WfCsY = clampedY;
                    WfCsYVisible = Visibility.Visible;
                    WfCsText = $"{(radioControl.CursorFreqHz / 1000000.0):F3} MHz  {radioControl.CursorHistorySec} sec";
                    WfCsTextX = SpCsHotspotX + 2;
                    WfCsTextY = clampedY - 18;

                    WfShortCsVisible = Visibility.Collapsed;
                    SpCsHotspotYMin = clampedY - 5;
                    SpCsHotspotYMax = clampedY + 5;

                    IsWfCsDashed = false;
                } else {
                    WfShortCsVisible = Visibility.Collapsed; WfCsYVisible = Visibility.Hidden; WfCsText = ""; IsWfCsDashed = false;
                }
            } else {
                SpCsVisible = Visibility.Hidden; WfCsYVisible = Visibility.Hidden; WfShortCsVisible = Visibility.Collapsed;
            }
        }

        private static string FormatHistoryLabel(double seconds)
            => Math.Abs(seconds - Math.Round(seconds)) < 0.05
                ? Math.Round(seconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
