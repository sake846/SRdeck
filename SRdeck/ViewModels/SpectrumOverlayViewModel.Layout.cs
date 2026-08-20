using System;
using System.Windows;
using SRdeck.Models;
using SRdeckPlugin.Contracts;

namespace SRdeck.ViewModels
{
    public partial class SpectrumOverlayViewModel
    {
        public void SyncOverlayLayout(RadioControl radioControl, RadioState radioState, int spectrumBiasAdj, int waterfallBiasAdj, bool isStarted, double spectrumWidth, double spectrumHeight, bool isReceiver1Visible = true, bool isReceiver2Visible = false, float? configGridTopDb = null, double displayBw = 7000000.0, IReadOnlyList<FrequencyOverlayItem>? receiverBands = null)
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
            ReceiverBands.Clear();
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
                        ReceiverBands.Add(new ReceiverBandRendererItem
                        {
                            Left = finalLeft,
                            Width = finalWidth,
                            Height = band.Lane < 0 ? spectrumHeight : 13,
                            Label = band.Label,
                            Fill = band.Fill,
                            Stroke = band.Stroke,
                            LabelColor = band.LabelColor,
                            Top = band.Lane < 0 ? 0 : band.Lane * 13
                        });
                    }
                }
                SpBandVisible = Visibility.Hidden;
            }
            else if (isReceiver1Visible && spectrumWidth > 0 && radioControl.SpanHz > 0)
            {
                double rawLeft = ((radioControl.FreqOffsetHz + halfDisplayBw - bandwidthHz1 / 2.0) / displayBw) * spectrumWidth;
                double rawRight = ((radioControl.FreqOffsetHz + halfDisplayBw + bandwidthHz1 / 2.0) / displayBw) * spectrumWidth;
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

            SpBand2Visible = Visibility.Hidden;

            if (radioControl.CursorFreqHz >= 0 && spectrumWidth > 0 && radioControl.SpanHz > 0)
            {
                SpCsVisible = Visibility.Visible;
                int zoomSpan = radioControl.SpanHz;

                double rawLeft = ((radioControl.CursorFreqOffsetHz + halfDisplayBw - zoomSpan / 2.0) / displayBw) * spectrumWidth;
                double rawRight = ((radioControl.CursorFreqOffsetHz + halfDisplayBw + zoomSpan / 2.0) / displayBw) * spectrumWidth;
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
                for (int i = 0; i < 9; i++)
                {
                    SpectrumYLabels[i].Text = Math.Round((double)(GridTopDb - 10.0 * (i + 1))).ToString();
                    SpectrumYLabels[i].Y = Math.Round(spectrumHeight / 10.0 * (i + 1));
                    SpectrumYLabels[i].XRight = Math.Round(spectrumWidth - 33);
                    SpectrumYLabels[i].TextColor = "#FFD6D6D6";
                }
                SyncWaterfallColorScale(radioControl, radioState, spectrumBiasAdj, waterfallBiasAdj);
                DebugBiasText = $"S:{spectrumBiasAdj} W:{waterfallBiasAdj}";
                DebugPwrText = $"P_fft:{radioState.AveFftPwr:F1} P_rx:{radioState.AveRxPwr:F1} MinF:{radioState.Min2FftPwr:F1}";
            }

            StationLabels.Clear();
            BandPlanRegions.Clear();
        }

    }
}
