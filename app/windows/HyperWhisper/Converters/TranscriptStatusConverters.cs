using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;

// FontFamily is in System.Windows.Media

namespace HyperWhisper.Converters;

/// <summary>
/// TRANSCRIPT STATUS TO VISIBILITY CONVERTER
///
/// Shows/hides elements based on transcript status.
/// ConverterParameter specifies which status to show for:
/// - "Processing" → Visible when Processing
/// - "Completed" → Visible when Completed
/// - "Failed" → Visible when Failed
/// </summary>
public class TranscriptStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TranscriptStatus status) return Visibility.Collapsed;
        if (parameter is not string targetStatus) return Visibility.Collapsed;

        bool isMatch = targetStatus.ToLowerInvariant() switch
        {
            "processing" => status == TranscriptStatus.Processing,
            "completed" => status == TranscriptStatus.Completed,
            "failed" => status == TranscriptStatus.Failed,
            _ => false
        };

        return isMatch ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}

/// <summary>
/// TRANSCRIPT STATUS TO BRUSH CONVERTER
///
/// Converts transcript status to a color brush for badges/indicators.
/// </summary>
public class TranscriptStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TranscriptStatus status)
        {
            return WpfBrushes.Gray;
        }

        return status switch
        {
            TranscriptStatus.Processing => new SolidColorBrush(WpfColor.FromRgb(147, 112, 219)), // Purple
            TranscriptStatus.Completed => new SolidColorBrush(WpfColor.FromRgb(46, 139, 87)),   // Green
            TranscriptStatus.Failed => new SolidColorBrush(WpfColor.FromRgb(220, 53, 69)),      // Red
            _ => WpfBrushes.Gray
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}

/// <summary>
/// TRANSCRIPT STATUS TO TEXT CONVERTER
///
/// Converts transcript status to display text.
/// </summary>
public class TranscriptStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TranscriptStatus status)
        {
            return Loc.S("status.unknown");
        }

        return status switch
        {
            TranscriptStatus.Processing => Loc.S("history.status.processing"),
            TranscriptStatus.Completed => Loc.S("history.status.completed"),
            TranscriptStatus.Failed => Loc.S("history.status.failed"),
            _ => Loc.S("status.unknown")
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}

/// <summary>
/// NULL TO VISIBILITY CONVERTER
///
/// Shows element when value is not null, hides when null.
/// Use ConverterParameter="Inverse" to invert the logic.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNotNull = value != null && !string.IsNullOrEmpty(value.ToString());

        // Check for inverse parameter
        if (parameter is string param && param.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            isNotNull = !isNotNull;
        }

        return isNotNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}

/// <summary>
/// COUNT TO VISIBILITY CONVERTER
///
/// Shows element when count > 0, hides when count is 0.
/// Use ConverterParameter="Inverse" to invert the logic.
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasItems = value is int count && count > 0;

        // Check for inverse parameter
        if (parameter is string param && param.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            hasItems = !hasItems;
        }

        return hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}

/// <summary>
/// BOOL TO PLAY/PAUSE CONVERTER
///
/// Converts IsPlaying state to play/pause icon.
/// </summary>
public class BoolToPlayPauseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isPlaying && isPlaying)
        {
            return "\u23F8"; // Pause icon
        }
        return "\u25B6"; // Play icon
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}

/// <summary>
/// BOOL TO RETRY TEXT CONVERTER
///
/// Shows "Retry" or "Retry again" based on retry info.
/// </summary>
public class BoolToRetryTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // This is a simple version - ideally we'd bind to RetryCount directly
        if (value is bool hasRetryInfo && hasRetryInfo)
        {
            return Loc.S("history.context.retryAgain");
        }
        return Loc.S("history.context.retry");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}

/// <summary>
/// BOOL TO FONT FAMILY CONVERTER
///
/// Returns monospace font when showing raw text.
/// </summary>
public class BoolToFontFamilyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool showRaw && showRaw)
        {
            return new WpfFontFamily("Consolas");
        }
        return new WpfFontFamily("Segoe UI");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}

/// <summary>
/// PROVIDER NAME DISPLAY CONVERTER
///
/// Converts provider identifiers to properly formatted display names.
/// Handles special cases like "openai" → "OpenAI", "gpt-4o" → "GPT-4o".
/// </summary>
public class ProviderNameDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string name || string.IsNullOrEmpty(name))
        {
            return value ?? "";
        }

        var trimmedName = name.Trim();
        var lowerName = trimmedName.ToLowerInvariant();

        var streamingSuffix = "";
        var streamingMatch = Regex.Match(trimmedName, @"\s*\((streaming)\)\s*$", RegexOptions.IgnoreCase);
        if (streamingMatch.Success)
        {
            trimmedName = trimmedName[..streamingMatch.Index].Trim();
            lowerName = trimmedName.ToLowerInvariant();
            streamingSuffix = " (Streaming)";
        }

        return (lowerName switch
        {
            "hyperwhispercloud" or "hyperwhisper cloud" or "hyperwhisper_cloud" or "hyperwhisper" => "HyperWhisper Cloud",
            "openai" => "OpenAI",
            "anthropic" => "Anthropic",
            "groq" => "Groq",
            "grok" => "Grok",
            "xai" => "xAI",
            "elevenlabs" or "eleven labs" or "eleven_labs" => "ElevenLabs",
            "deepgram" => "Deepgram",
            "gemini" or "google gemini" => "Google Gemini",
            "cerebras" => "Cerebras",
            "local_llm" or "local llm" => "Local LLM",
            // GPT model names - preserve case for model number
            _ when trimmedName.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) =>
                "GPT-" + trimmedName[4..],
            // Claude model names
            _ when trimmedName.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) && trimmedName.Length > 7 =>
                "Claude " + char.ToUpper(trimmedName[7]) + trimmedName[8..],
            // Default: capitalize first letter of each word
            _ => CapitalizeWords(SplitIdentifier(trimmedName))
        }) + streamingSuffix;
    }

    private static string CapitalizeWords(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var words = input.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
            {
                words[i] = char.ToUpper(words[i][0]) + words[i][1..].ToLower();
            }
        }
        return string.Join(" ", words);
    }

    private static string SplitIdentifier(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;

        var builder = new StringBuilder(input.Length + 8);
        for (var i = 0; i < input.Length; i++)
        {
            var current = input[i];
            if (i > 0
                && char.IsUpper(current)
                && (char.IsLower(input[i - 1]) || (i + 1 < input.Length && char.IsLower(input[i + 1]))))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}
