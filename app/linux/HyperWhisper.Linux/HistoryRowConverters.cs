using System.Globalization;
using Avalonia.Data.Converters;

namespace HyperWhisper.Linux;

/// <summary>
/// The history list on Windows prints the local clock time of a transcript, not its stored UTC
/// stamp and not the date. The date belongs to the day the row sits under.
/// </summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DateTime utc => (utc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                : utc).ToLocalTime().ToString("t", culture),
            DateTimeOffset offset => offset.ToLocalTime().ToString("t", culture),
            _ => string.Empty,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// A history row shows a compact duration — "20s", "1m 05s" — where the detail pane shows the
/// long "Duration: 20.0s" form.
/// </summary>
public sealed class ShortDurationConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var seconds = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => -1d,
        };
        if (seconds < 0) return string.Empty;
        var whole = (int)Math.Round(seconds);
        return whole < 60
            ? string.Format(culture, "{0}s", whole)
            : string.Format(culture, "{0}m {1:00}s", whole / 60, whole % 60);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The Source column pill. Windows maps the stored source string to a localized badge —
/// vocabulary.autoLearned.badge or vocabulary.manual.badge — and falls back to the raw value for
/// anything else (VocabularyPage.xaml.cs:288-294). Binding the raw column straight into the pill
/// printed the storage tokens, so the rows read "manual" and "auto-learn" in lower case.
/// </summary>
public sealed class VocabularySourceConverter : IValueConverter
{
    private readonly HyperWhisper.Linux.Localization.AvaloniaLocalizationBridge _localization;

    public VocabularySourceConverter(HyperWhisper.Linux.Localization.AvaloniaLocalizationBridge localization)
        => _localization = localization ?? throw new ArgumentNullException(nameof(localization));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var source = value as string;
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        // The same three spellings Windows accepts; the repository has written more than one.
        if (string.Equals(source, "auto-learn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "auto-learned", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, "autoLearned", StringComparison.OrdinalIgnoreCase))
            return _localization["vocabulary.autoLearned.badge"];
        return string.Equals(source, "manual", StringComparison.OrdinalIgnoreCase)
            ? _localization["vocabulary.manual.badge"]
            : source;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
