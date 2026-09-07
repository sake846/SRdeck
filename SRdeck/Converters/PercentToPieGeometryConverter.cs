using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SRdeck.Converters
{
    /// <summary>
    /// Converts a percentage (0-100) into a pie slice PathGeometry starting from 12 o'clock clockwise.
    /// </summary>
    public class PercentToPieGeometryConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double percent = 0.0;
            if (value is double d)
            {
                percent = d;
            }
            else if (value is int i)
            {
                percent = i;
            }
            else if (value is float f)
            {
                percent = f;
            }

            double radius = 11.0;
            if (parameter is string paramStr && double.TryParse(paramStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedR))
            {
                radius = parsedR;
            }

            double centerX = radius;
            double centerY = radius;

            if (percent <= 0.01)
            {
                return Geometry.Empty;
            }

            if (percent >= 99.99)
            {
                var ellipse = new EllipseGeometry(new Point(centerX, centerY), radius, radius);
                ellipse.Freeze();
                return ellipse;
            }

            // 12 o'clock is -90 degrees
            double startAngle = -90.0;
            double sweepAngle = Math.Clamp(percent / 100.0 * 360.0, 0.0, 360.0);
            double endAngle = startAngle + sweepAngle;

            double startRad = startAngle * Math.PI / 180.0;
            double endRad = endAngle * Math.PI / 180.0;

            Point startPoint = new Point(centerX + radius * Math.Cos(startRad), centerY + radius * Math.Sin(startRad));
            Point endPoint = new Point(centerX + radius * Math.Cos(endRad), centerY + radius * Math.Sin(endRad));
            Point center = new Point(centerX, centerY);

            bool isLargeArc = sweepAngle > 180.0;

            var figure = new PathFigure
            {
                StartPoint = center,
                IsClosed = true,
                IsFilled = true
            };

            figure.Segments.Add(new LineSegment(startPoint, true));
            figure.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(center, true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();
            return geometry;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
