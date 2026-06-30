using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HyperWhisper.Converters;

/// <summary>
/// BOOL TO VISIBILITY CONVERTER
///
/// Converts boolean values to Visibility:
/// - true → Visible
/// - false → Collapsed
///
/// Use ConverterParameter="Inverse" to invert the logic:
/// - true → Collapsed
/// - false → Visible
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;

        // Check for inverse parameter
        if (parameter is string param && param.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            boolValue = !boolValue;
        }

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isVisible = value is Visibility v && v == Visibility.Visible;

        // Check for inverse parameter
        if (parameter is string param && param.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            isVisible = !isVisible;
        }

        return isVisible;
    }
}
