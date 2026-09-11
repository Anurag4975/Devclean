using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DevClean.Converters
{
    public class SafetyToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var safety = value?.ToString()?.ToUpperInvariant() ?? "";
            return safety switch
            {
                "SAFE"                           => new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x4A)), // green
                "REVIEW"                         => new SolidColorBrush(Color.FromRgb(0xE0, 0x9A, 0x1E)), // orange
                "RISKY" or "UNSAFE" or "DANGER"  => new SolidColorBrush(Color.FromRgb(0xD6, 0x33, 0x33)), // red
                _                                 => new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)) // gray
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}