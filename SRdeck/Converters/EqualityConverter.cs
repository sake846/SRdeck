using System;
using System.Globalization;
using System.Windows.Data;

namespace SRdeck.Converters
{
    /// <summary>
    /// 2つの値が等しいかどうかを判定し、bool値を返すコンバーターです。
    /// マルチバインディングにも対応しています。
    /// </summary>
    public class EqualityConverter : IMultiValueConverter, IValueConverter
    {
        // IMultiValueConverter Implementation
        public object Convert(object[]? values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2)
            {
                return false;
            }

            return values[0] == null ? values[1] == null : values[0].Equals(values[1]);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        // IValueConverter Implementation (Value vs Parameter)
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value == null ? parameter == null : value.Equals(parameter);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
