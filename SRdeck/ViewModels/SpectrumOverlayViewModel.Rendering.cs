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
            const int steps = 20;
            Span<uint> colors = stackalloc uint[steps + 1];
            var previous = WaterfallColorScaleBrush as LinearGradientBrush;
            bool unchanged = previous?.GradientStops.Count == colors.Length;
            // Compare the effective colors, since noise-floor changes can quantize
            // to the same LUT entries (or different entries with identical colors).
            float noiseFloor = WaterfallColorScale.ResolveNoiseFloor(radioState);
            for (int i = 0; i <= steps; i++)
            {
                double offset = (double)i / steps;
                
                // Y軸の物理レベル L を計算 (上端 GridTopDb, レンジ SPECTRUM_VIEW_RANGE_DB)
                double physicalLevel = GridTopDb - AppConstants.SPECTRUM_VIEW_RANGE_DB * offset;
                
                // ウォーターフォールのインデックス計算式と完全同期
                // 受信停止中やリセット時 (MIN_RSSI_DB または 0) の場合は、標準的なノイズフロア (-120dBm) を仮定して表示する。
                // MIN_RSSI_DB より小さい値は実測された有効なノイズフロアなので、未初期化扱いしない。
                int index = WaterfallColorScale.GetColorIndex((float)physicalLevel, noiseFloor, waterfallBiasAdj);
                colors[i] = colorLookUpTable[index];
                if (unchanged)
                {
                    Color color = previous!.GradientStops[i].Color;
                    unchanged = ((uint)color.R << 16 | (uint)color.G << 8 | color.B) == (colors[i] & 0xFFFFFFu);
                }
            }
            if (unchanged) return;

            var gradientBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            for (int i = 0; i <= steps; i++)
            {
                uint bgrColor = colors[i];
                byte red = (byte)((bgrColor >> 16) & 0xFF);
                byte green = (byte)((bgrColor >> 8) & 0xFF);
                byte blue = (byte)(bgrColor & 0xFF);
                
                gradientBrush.GradientStops.Add(new GradientStop(Color.FromRgb(red, green, blue), (double)i / steps));
            }

            gradientBrush.Freeze();
            WaterfallColorScaleBrush = gradientBrush;
        }

        private double MeasureTextWidth(string text)
        {
            var fontFamily = System.Windows.Application.Current?.TryFindResource("MainFontFamily") as System.Windows.Media.FontFamily
                ?? System.Windows.SystemFonts.MessageFontFamily;
            var formattedText = new System.Windows.Media.FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(fontFamily, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal),
                11,
                System.Windows.Media.Brushes.Black,
                1.0);
            return formattedText.Width;
        }

        private string GetStationLabelColor(long frequency, RadioControl radioControl)
        {
            if (frequency == radioControl.CursorFreqHz) return "#FFC8C8C8"; // Cursor (Light Gray)
            if (radioControl.IsR1Visible && radioControl.IsPowerOn && frequency == radioControl.TunedFreqHz) return "#FF64C8C8"; // Receiver 1 (Cyan)
            return "#FFC8C864"; // Default (Yellow)
        }
    }
}
