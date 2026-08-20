using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SRdeck.Models;

namespace SRdeck.Converters
{
    public class StepColorConverter : IMultiValueConverter
    {
        private static readonly SolidColorBrush BrandGreenBrush;
        private static readonly SolidColorBrush BorderGrayBrush;

        static StepColorConverter()
        {
            BrandGreenBrush = new SolidColorBrush(AppConstants.COLOR_BRAND_GREEN);
            BrandGreenBrush.Freeze();
            BorderGrayBrush = new SolidColorBrush(AppConstants.COLOR_BORDER_GRAY);
            BorderGrayBrush.Freeze();
        }

        public object Convert(object[]? values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values != null && values.Length >= 2 && values[0] is int stepMode && values[1] is int itemIndex && stepMode == itemIndex)
            {
                return BrandGreenBrush;
            }

            return BorderGrayBrush;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
