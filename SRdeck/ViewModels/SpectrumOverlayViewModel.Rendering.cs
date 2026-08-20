using System;
using System.Windows;
using System.Windows.Media;
using SRdeck.Models;
using SRdeck.Renderers;

namespace SRdeck.ViewModels
{
    public partial class SpectrumOverlayViewModel
    {
        private void SyncWaterfallColorScale(RadioControl radioControl, RadioState radioState, int spectrumBiasAdj, int waterfallBiasAdj)
        {
            var colorLookUpTable = ColorLUT.GetLutBgr32(radioControl.WaterfallColorMode);
            var gradientBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };

            for (int i = 0; i <= 10; i++)
            {
                double offset = i / 10.0;
                
                // Y軸の物理ラベル L を計算 (上端 -50, 下端 -130 の 80dB 幅を仮定)
                // L = -80.0 * offset - 50.0 + SpectrumBias
                double physicalLevel = -80.0 * offset - 50.0 - spectrumBiasAdj;
                
                // ウォーターフォールのインデックス計算式と完全同期
                // 受信停止中 (radioState.Min2FftPwr == 0) の場合は、標準的なノイズフロア (-120dBm) を仮定して表示する
                float noiseFloor = (radioState.Min2FftPwr == 0) ? -120.0f : radioState.Min2FftPwr;
                int index = (int)((physicalLevel - noiseFloor) * 4.0 + waterfallBiasAdj);
                index = Math.Clamp(index, 0, 255);
                
                uint bgrColor = colorLookUpTable[index];
                byte red = (byte)((bgrColor >> 16) & 0xFF);
                byte green = (byte)((bgrColor >> 8) & 0xFF);
                byte blue = (byte)(bgrColor & 0xFF);
                
                gradientBrush.GradientStops.Add(new GradientStop(Color.FromRgb(red, green, blue), offset));
            }

            WaterfallColorScaleBrush = gradientBrush;
        }

        private double MeasureTextWidth(string text)
        {
            var formattedText = new System.Windows.Media.FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface((System.Windows.Media.FontFamily)System.Windows.Application.Current.Resources["MainFontFamily"], System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal),
                11,
                System.Windows.Media.Brushes.Black,
                1.0);
            return formattedText.Width;
        }

        private string GetStationLabelColor(float frequency, RadioControl radioControl)
        {
            if (frequency == (float)radioControl.CursorFreqHz) return "#FFC8C8C8"; // Cursor (Light Gray)
            if (radioControl.IsR1Visible && radioControl.IsPowerOn && frequency == (float)radioControl.TunedFreqHz) return "#FF64C8C8"; // Receiver 1 (Cyan)
            return "#FFC8C864"; // Default (Yellow)
        }
    }
}
