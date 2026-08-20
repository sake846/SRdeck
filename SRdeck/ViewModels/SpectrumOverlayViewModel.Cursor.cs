using System;
using System.Windows;
using SRdeck.Models;

namespace SRdeck.ViewModels
{
    public partial class SpectrumOverlayViewModel
    {
        public void SyncCursorLayout(RadioControl radioControl, double spectrumWidth, double spectrumHeight, bool isReceiver1Visible = true, bool isReceiver2Visible = false, double displayBw = 7000000.0)
        {
            if (radioControl.CursorFreqHz >= 0 && spectrumWidth > 0 && radioControl.SpanHz > 0)
            {
                double halfDisplayBw = displayBw / 2.0;
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

                SpCsHotspotX = Math.Round(Math.Clamp(
                    ((radioControl.CursorFreqOffsetHz + halfDisplayBw) / displayBw) * spectrumWidth,
                    0.0,
                    spectrumWidth));

                if (radioControl.CursorPowerDb >= 0)
                {
                    SpCsDbVisible = Visibility.Visible;
                    double cursorYPosition = Math.Round((radioControl.CursorPowerDb / 100.0) * Math.Max(1.0, spectrumHeight - 3.0));
                    SpCsDbY = cursorYPosition;

                    double cursorDbm = (cursorYPosition / Math.Max(1.0, spectrumHeight)) * -AppConstants.SPECTRUM_VIEW_RANGE_DB + GridTopDb;
                    SpCsText = $"{(radioControl.CursorFreqHz / 1000000.0):F3} MHz  {Math.Round(cursorDbm):F0} dBm";
                    SpCsTextX = SpCsHotspotX + 2;
                    SpCsTextY = cursorYPosition - 18;

                    SpCsHotspotYMin = cursorYPosition - 5;
                    SpCsHotspotYMax = cursorYPosition + 5;
                }
                else
                {
                    SpCsDbVisible = Visibility.Hidden;
                    SpCsText = "";
                }
            }
            else
            {
                SpCsVisible = Visibility.Hidden;
                SpCsDbVisible = Visibility.Hidden;
                SpCsText = "";
            }

            SyncStationLabelColors(radioControl);
            SpCursorFreqText = radioControl.CursorFreqHz >= 0 ? (radioControl.CursorFreqHz / 1000000.0).ToString("F3").PadLeft(10) : "";
        }

        private void SyncStationLabelColors(RadioControl radioControl)
        {
        }

    }
}
