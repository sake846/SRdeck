using System.Windows;
using SRdeck.Models;
using SRdeckPlugin.Contracts;

namespace SRdeck.ViewModels
{
    public partial class SpectrumOverlayViewModel
    {
        public void SyncOverlayLayout(RadioControl radioControl, RadioState radioState, int spectrumBiasAdj, int waterfallBiasAdj, bool isStarted, double spectrumWidth, double spectrumHeight, bool isReceiver1Visible = true, bool isReceiver2Visible = false, float? configGridTopDb = null, double displayBw = 7000000.0, IReadOnlyList<FrequencyOverlayItem>? receiverBands = null, int receiveBandCenterOffsetHz = 0)
        {
            double halfDisplayBw = displayBw / 2.0;
            float halfDisplayBwF = (float)halfDisplayBw;
            float displayBwF = (float)displayBw;

            float baseTopDb = configGridTopDb.HasValue ? configGridTopDb.Value : AppConstants.DEFAULT_GRID_TOP_DB;
            GridTopDb = baseTopDb - spectrumBiasAdj;
            // Receiver 1 Bandwidth
            double bandwidthHz1 = radioControl.SpanHz > 0
                ? radioControl.SpanHz
                : radioControl.DemodMode switch
                {
                    DemodulationMode.USB => 500,
                    DemodulationMode.LSB => 500,
                    DemodulationMode.USB_Wide => 3000,
                    DemodulationMode.LSB_Wide => 3000,
                    DemodulationMode.AM => 6000,
                    DemodulationMode.AM_Wide => 11000,
                    DemodulationMode.FM_Narrow => 15000,
                    DemodulationMode.FM_Wide => 200000,
                    _ => 3000
                };
            bool showMultipleBands = receiverBands is { Count: > 0 };
            int bandIndex = 0;
            // Plugin bands describe the receive plan, so keep them visible even
            // while the receiver itself is stopped.
            if (showMultipleBands && isReceiver1Visible && spectrumWidth > 0)
            {
                long displayStartHz = (long)radioControl.CenterFreqHz - (long)halfDisplayBw;
                foreach (FrequencyOverlayItem band in receiverBands!)
                {
                    long frequencyHz = band.CenterFrequencyHz;
                    int bandwidthHz = band.BandwidthHz;
                    double rawLeft = ((frequencyHz - bandwidthHz / 2.0 - displayStartHz) / displayBw) * spectrumWidth;
                    double rawRight = ((frequencyHz + bandwidthHz / 2.0 - displayStartHz) / displayBw) * spectrumWidth;
                    if (rawRight >= 0 && rawLeft <= spectrumWidth)
                    {
                        double width = rawRight - rawLeft;
                        if (width < 4.0)
                        {
                            double center = (rawLeft + rawRight) / 2.0;
                            rawLeft = center - 2.0;
                            rawRight = center + 2.0;
                        }
                        double finalLeft = Math.Max(0, Math.Round(rawLeft));
                        double finalRight = Math.Min(spectrumWidth, Math.Round(rawRight));
                        double finalWidth = Math.Max(4.0, finalRight - finalLeft);
                        double height = band.Lane < 0 ? spectrumHeight : 13;
                        double top = band.Lane < 0 ? 0 : band.Lane * 13;
                        var current = bandIndex < ReceiverBands.Count ? ReceiverBands[bandIndex] : null;
                        if (current is null || current.Left != finalLeft || current.Width != finalWidth ||
                            current.Height != height || current.Top != top || current.Label != band.Label ||
                            current.Fill != band.Fill || current.Stroke != band.Stroke || current.LabelColor != band.LabelColor)
                        {
                            var item = new ReceiverBandRendererItem
                            {
                                Left = finalLeft, Width = finalWidth, Height = height, Top = top,
                                Label = band.Label, Fill = band.Fill, Stroke = band.Stroke, LabelColor = band.LabelColor
                            };
                            if (current is null) ReceiverBands.Add(item);
                            else ReceiverBands[bandIndex] = item;
                        }
                        bandIndex++;
                    }
                }
                SpBandVisible = Visibility.Hidden;
            }
            else if (isReceiver1Visible && spectrumWidth > 0 && radioControl.SpanHz > 0)
            {
                double rawLeft = ((radioControl.FreqOffsetHz + receiveBandCenterOffsetHz + halfDisplayBw - bandwidthHz1 / 2.0) / displayBw) * spectrumWidth;
                double rawRight = ((radioControl.FreqOffsetHz + receiveBandCenterOffsetHz + halfDisplayBw + bandwidthHz1 / 2.0) / displayBw) * spectrumWidth;
                if (rawRight >= 0 && rawLeft <= spectrumWidth)
                {
                    SpBandVisible = Visibility.Visible;
                    double width = rawRight - rawLeft;
                    if (width < 4.0)
                    {
                        double center = (rawLeft + rawRight) / 2.0;
                        rawLeft = center - 2.0;
                        rawRight = center + 2.0;
                    }
                    double finalLeft = Math.Max(0, Math.Round(rawLeft));
                    double finalRight = Math.Min(spectrumWidth, Math.Round(rawRight));
                    SpBandLeft = finalLeft;
                    SpBandWidth = Math.Max(4.0, finalRight - finalLeft);
                }
                else
                {
                    SpBandVisible = Visibility.Hidden;
                }
            }
            else
            {
                SpBandVisible = Visibility.Hidden;
            }

            while (ReceiverBands.Count > bandIndex) ReceiverBands.RemoveAt(ReceiverBands.Count - 1);

            SpBand2Visible = Visibility.Hidden;

            if (radioControl.CursorFreqHz >= 0 && spectrumWidth > 0 && radioControl.SpanHz > 0)
            {
                SpCsVisible = Visibility.Visible;
                int zoomSpan = radioControl.SpanHz;

                double rawLeft = ((radioControl.CursorFreqOffsetHz + receiveBandCenterOffsetHz + halfDisplayBw - zoomSpan / 2.0) / displayBw) * spectrumWidth;
                double rawRight = ((radioControl.CursorFreqOffsetHz + receiveBandCenterOffsetHz + halfDisplayBw + zoomSpan / 2.0) / displayBw) * spectrumWidth;
                double finalLeft = Math.Max(0, Math.Round(rawLeft));
                double finalRight = Math.Min(spectrumWidth, Math.Round(rawRight));
                SpRawCsLeft = rawLeft;
                SpCsLeft = finalLeft;
                SpCsWidth = Math.Max(0, finalRight - finalLeft);
                SpCsLineX = finalLeft;
                SpCsRightEdge = finalRight;

                if (radioControl.CursorPowerDb >= 0)
                {
                    SpCsDbVisible = Visibility.Visible;
                    double cursorYPosition = Math.Round((radioControl.CursorPowerDb / 100.0) * Math.Max(1.0, spectrumHeight - 3.0));
                    SpCsDbY = cursorYPosition;
                    
                    double cursorDbm = (cursorYPosition / Math.Max(1.0, spectrumHeight)) * -AppConstants.SPECTRUM_VIEW_RANGE_DB + GridTopDb;
                    SpCsHotspotX = Math.Round(Math.Clamp(
                        ((radioControl.CursorFreqOffsetHz + halfDisplayBw) / displayBw) * spectrumWidth,
                        0.0,
                        spectrumWidth));
                    SpCsHotspotYMin = cursorYPosition - 5;
                    SpCsHotspotYMax = cursorYPosition + 5;

                    SpCsText = $"{radioControl.CursorFreqHz:#,0} Hz  {Math.Round(cursorDbm):F0} dBm";
                    SpCsTextX = SpCsHotspotX + 2;
                    SpCsTextY = cursorYPosition - 18;
                }
                else { SpCsDbVisible = Visibility.Hidden; SpCsText = ""; }
            }
            else
            {
                SpCsVisible = Visibility.Hidden;
                SpCsDbVisible = Visibility.Hidden;
            }

            SpCursorFreqText = radioControl.CursorFreqHz >= 0 ? radioControl.CursorFreqHz.ToString("#,0").PadLeft(13) : "";

            if (spectrumHeight > 0 && spectrumWidth > 0)
            {
                SpColorBarLeft = Math.Round(spectrumWidth - 5);
                SpColorBarHeight = Math.Round(spectrumHeight);
                const double rangeDb = AppConstants.SPECTRUM_VIEW_RANGE_DB;
                int labelIndex = 0;
                for (int db = (int)Math.Floor(GridTopDb / 10.0) * 10; db > GridTopDb - rangeDb; db -= 10)
                {
                    if (db >= GridTopDb) continue;
                    double y = Math.Round((GridTopDb - db) / rangeDb * spectrumHeight);
                    if (y <= 0 || y >= spectrumHeight) continue;

                    while (labelIndex >= SpectrumYLabels.Count)
                    {
                        SpectrumYLabels.Add(new SpectrumYLabel());
                    }

                    var label = SpectrumYLabels[labelIndex];
                    label.Text = db.ToString();
                    label.Y = y;
                    label.XRight = Math.Round(spectrumWidth - 33);
                    label.TextColor = "#FFF2F2F2";
                    label.Visibility = Visibility.Visible;
                    labelIndex++;
                }

                for (int i = labelIndex; i < SpectrumYLabels.Count; i++)
                {
                    SpectrumYLabels[i].Text = "";
                    SpectrumYLabels[i].Visibility = Visibility.Collapsed;
                }

                SyncWaterfallColorScale(radioControl, radioState, waterfallBiasAdj);
                DebugBiasText = $"S:{spectrumBiasAdj} W:{waterfallBiasAdj}";
                DebugPwrText = $"P_fft:{radioState.AveFftPwr:F1} P_rx:{radioState.AveRxPwr:F1} MinF:{radioState.Min2FftPwr:F1}";
            }

            SyncFrequencyCatalogLayout(radioControl, spectrumWidth, displayBw);
        }

        private void SyncFrequencyCatalogLayout(
            RadioControl radioControl,
            double spectrumWidth,
            double displayBandwidthHz)
        {
            if (spectrumWidth <= 0 || displayBandwidthHz <= 0)
            {
                if (StationLabels.Count > 0) StationLabels.Clear();
                if (BandPlanRegions.Count > 0) BandPlanRegions.Clear();
                _stationLabelsDirty = true;
                _bandPlanRegionsDirty = true;
                return;
            }

            int widthKey = (int)Math.Round(spectrumWidth);
            int bandwidthKey = (int)Math.Round(displayBandwidthHz);
            double minimumHz = radioControl.CenterFreqHz - displayBandwidthHz / 2.0;
            double maximumHz = radioControl.CenterFreqHz + displayBandwidthHz / 2.0;

            if (radioControl.IsBandPlanVisible)
            {
                bool rebuild = _bandPlanRegionsDirty ||
                    _lastBandPlanCenterFrequencyHz != radioControl.CenterFreqHz ||
                    _lastBandPlanWidth != widthKey ||
                    _lastBandPlanDisplayBandwidthHz != bandwidthKey;
                if (rebuild)
                {
                    BandPlanRegions.Clear();
                    foreach (BandPlanItem band in _bandPlans)
                    {
                        if (band.EndHz <= minimumHz || band.StartHz >= maximumHz) continue;
                        double left = Math.Max(0, (band.StartHz - minimumHz) / displayBandwidthHz * spectrumWidth);
                        double right = Math.Min(spectrumWidth, (band.EndHz - minimumHz) / displayBandwidthHz * spectrumWidth);
                        if (right <= left) continue;
                        BandPlanRegions.Add(new BandPlanRendererItem
                        {
                            Left = left,
                            Width = right - left,
                            Label = band.Label,
                            Color = band.Color
                        });
                    }
                    _bandPlanRegionsDirty = false;
                    _lastBandPlanCenterFrequencyHz = radioControl.CenterFreqHz;
                    _lastBandPlanWidth = widthKey;
                    _lastBandPlanDisplayBandwidthHz = bandwidthKey;
                }
            }
            else if (BandPlanRegions.Count > 0)
            {
                BandPlanRegions.Clear();
                _bandPlanRegionsDirty = true;
            }

            if (radioControl.IsStationNameVisible)
            {
                bool rebuild = _stationLabelsDirty ||
                    _lastStationCenterFrequencyHz != radioControl.CenterFreqHz ||
                    _lastStationTunedFrequencyHz != radioControl.TunedFreqHz ||
                    _lastStationWidth != widthKey ||
                    _lastStationDisplayBandwidthHz != bandwidthKey ||
                    _lastStationBandPlanVisible != radioControl.IsBandPlanVisible;
                if (rebuild)
                {
                    StationLabels.Clear();
                    double[] occupiedRightEdges = new double[10];
                    int yOffset = radioControl.IsBandPlanVisible ? 17 : 2;
                    foreach (StationItem station in _stations)
                    {
                        if (station.FrequencyHz < minimumHz || station.FrequencyHz > maximumHz) continue;
                        double markerX = (station.FrequencyHz - minimumHz) / displayBandwidthHz * spectrumWidth;
                        string text = $"\u25BC{station.Name}";
                        double textWidth = MeasureTextWidth(text);
                        double left = markerX - MeasureTextWidth("\u25BC") / 2.0 - 1;
                        if (left + textWidth > spectrumWidth)
                        {
                            text = $"{station.Name}\u25BC";
                            textWidth = MeasureTextWidth(text);
                            left = markerX - textWidth + MeasureTextWidth("\u25BC") / 2.0 - 1;
                        }
                        left = Math.Clamp(left, 0, Math.Max(0, spectrumWidth - textWidth));

                        int lane = Array.FindIndex(occupiedRightEdges, edge => edge <= left);
                        if (lane < 0) continue;
                        occupiedRightEdges[lane] = left + textWidth + 4;
                        StationLabels.Add(new StationLabel
                        {
                            Name = text,
                            X = Math.Round(left),
                            Y = lane * 15 + yOffset,
                            LineX = Math.Round(markerX - left),
                            FrequencyHz = station.FrequencyHz,
                            Color = GetStationLabelColor(station.FrequencyHz, radioControl)
                        });
                    }
                    _stationLabelsDirty = false;
                    _lastStationCenterFrequencyHz = radioControl.CenterFreqHz;
                    _lastStationTunedFrequencyHz = radioControl.TunedFreqHz;
                    _lastStationWidth = widthKey;
                    _lastStationDisplayBandwidthHz = bandwidthKey;
                    _lastStationBandPlanVisible = radioControl.IsBandPlanVisible;
                }
            }
            else if (StationLabels.Count > 0)
            {
                StationLabels.Clear();
                _stationLabelsDirty = true;
            }

            SyncStationLabelColors(radioControl);
        }

    }
}
