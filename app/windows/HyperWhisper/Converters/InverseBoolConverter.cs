using System.Globalization;
using System.Windows.Data;

namespace HyperWhisper.Converters;

/// <summary>
/// INVERSE BOOL CONVERTER
///
/// Inverts boolean values:
/// - true → false
/// - false → true
///
/// Useful for binding IsEnabled to a "busy" state property.
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }
        return false;
    }
}
