using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SRdeck.Converters
{
    public class CpuColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double cpuUsagePercentage)
            {
                bool isSolidColorRequested = parameter as string == "Solid";

                // 指示: 75以下の時は黄緑に、75%以上の時は黄色に、さらに90%以上の時は赤にしてください。
                if (cpuUsagePercentage >= 90.0)
                {
                    if (isSolidColorRequested) return new SolidColorBrush(Color.FromRgb(255, 100, 100)); // 視認性の良い明るい赤
                    // 赤色
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
                else if (cpuUsagePercentage >= 75.0)
                {
                    if (isSolidColorRequested) return new SolidColorBrush(Color.FromRgb(255, 255, 100)); // 視認性の良い明るい黄
                    // 黄色
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
                    if (isSolidColorRequested) return new SolidColorBrush(Color.FromRgb(180, 230, 50)); // 元の固定色に近い黄緑 (#FFB4E632)
                    // 黄緑色
                    return new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromRgb(200, 255, 0), 0.0),
                            new GradientStop(Color.FromRgb(153, 204, 0), 0.5),
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
