using System.Globalization;
using System.Windows;
using System.Windows.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Converters;

/// <summary>
/// POST-PROCESSING VISIBILITY CONVERTER
///
/// Converts a Mode to Visibility for the post-processing section.
/// - Visible when PostProcessingMode != 0 (off) AND LanguageModel is set
/// - Collapsed otherwise
/// </summary>
public class PostProcessingVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Mode mode)
            return Visibility.Collapsed;

        var provider = PostProcessingProviderExtensions.FromString(mode.PostProcessingProvider);
        var hasModel = provider == PostProcessingProvider.LocalLlm
            ? !string.IsNullOrEmpty(mode.LocalPostProcessingModel ?? mode.LanguageModel)
            : !string.IsNullOrEmpty(mode.LanguageModel);

        // Show only if post-processing is enabled (not 0=off) and has a provider/model
        // HyperWhisper Cloud and custom endpoints don't require a language model selection
        bool isEnabled = mode.PostProcessingMode != 0
            && (provider == PostProcessingProvider.HyperWhisperCloud
                || CustomPostProcessingEndpoint.IsCustomProviderString(mode.PostProcessingProvider)
                || hasModel);
        return isEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}
