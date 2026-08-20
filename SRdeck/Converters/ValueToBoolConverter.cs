using System;
using System.Globalization;
using System.Windows.Data;

namespace SRdeck.Converters
{
    public class ValueToBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
            {
                return false;
            }

            if (double.TryParse(value.ToString(), out double parsedValue) &&
                double.TryParse(parameter.ToString(), out double parsedThreshold))
            {
                return parsedValue >= parsedThreshold;
            }

            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
