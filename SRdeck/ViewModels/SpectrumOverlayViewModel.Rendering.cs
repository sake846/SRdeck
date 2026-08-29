using System;
using System.Windows;
using System.Windows.Media;
using SRdeck.Models;
using SRdeck.Renderers;

namespace SRdeck.ViewModels
{
    public partial class SpectrumOverlayViewModel
    {
        private void SyncWaterfallColorScale(RadioControl radioControl, RadioState radioState, int waterfallBiasAdj)
        {
            var colorLookUpTable = ColorLUT.GetLutBgr32(radioControl.WaterfallColorMode);
            var gradientBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };

            const int steps = 20;
            for (int i = 0; i <= steps; i++)
            {
                double offset = (double)i / steps;
                
                // Y軸の物理レベル L を計算 (上端 GridTopDb, レンジ SPECTRUM_VIEW_RANGE_DB)
                double physicalLevel = GridTopDb - AppConstants.SPECTRUM_VIEW_RANGE_DB * offset;
                
                // ウォーターフォールのインデックス計算式と完全同期
                // 受信停止中やリセット時 (MIN_RSSI_DB または 0) の場合は、標準的なノイズフロア (-120dBm) を仮定して表示する。
                // MIN_RSSI_DB より小さい値は実測された有効なノイズフロアなので、未初期化扱いしない。
                float noiseFloor = WaterfallColorScale.ResolveNoiseFloor(radioState);
                int index = WaterfallColorScale.GetColorIndex((float)physicalLevel, noiseFloor, waterfallBiasAdj);
                
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
