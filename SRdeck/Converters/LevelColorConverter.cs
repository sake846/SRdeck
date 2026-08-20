using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SRdeck.Converters
{
    public class LevelColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double decibels)
            {
                // -1 dBFS以上は飽和警告、-18 dBFS以下は低レベル警告。
                // 飽和（クリップ）または低レベル警告として赤を表示します。
                if (decibels >= -1.0)
                {
                    // 赤色 (クリッピング)
                    return new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromRgb(255, 100, 100), 0.0),
                            new GradientStop(Color.FromRgb(255, 0, 0), 0.5),
                            new GradientStop(Color.FromRgb(150, 0, 0), 1.0)
                        },
                        new Point(0, 0),
                        new Point(0, 1));
                }
                else if (decibels <= -18.0)
                {
                    // 黄色 (低レベル警告)
                    return new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromRgb(255, 255, 100), 0.0),
                            new GradientStop(Color.FromRgb(255, 255, 0), 0.5),
                            new GradientStop(Color.FromRgb(150, 150, 0), 1.0)
                        },
                        new Point(0, 0),
                        new Point(0, 1));
                }
                else
                {
                    // 黄緑色 (グラデーション)
                    return new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromRgb(200, 255, 0), 0.0),
                            new GradientStop(Color.FromRgb(153, 204, 0), 0.5), // #99CC00
                            new GradientStop(Color.FromRgb(100, 150, 0), 1.0)
                        },
                        new Point(0, 0),
                        new Point(0, 1));
                }
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
