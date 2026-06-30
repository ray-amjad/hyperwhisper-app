using System.Globalization;
using System.Windows.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Converters;

/// <summary>
/// LANGUAGE CODE TO DISPLAY NAME CONVERTER
///
/// Converts ISO language codes to human-readable names:
/// - "en" → "English"
/// - "ja" → "Japanese"
/// - "auto" → "Automatic"
///
/// Uses LanguageInfo.GetDisplayName() for lookup.
/// </summary>
public class LanguageCodeToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string code || string.IsNullOrEmpty(code))
            return Loc.S("language.automatic");

        return LanguageInfo.GetDisplayName(code);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}
