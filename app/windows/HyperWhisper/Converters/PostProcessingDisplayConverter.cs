using System.Globalization;
using System.Windows.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Models;

namespace HyperWhisper.Converters;

/// <summary>
/// POST-PROCESSING DISPLAY CONVERTER
///
/// Converts a Mode to the post-processing model display name.
/// Returns the language model's display name for showing in mode cards.
/// </summary>
public class PostProcessingDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Mode mode)
            return string.Empty;

        var provider = PostProcessingProviderExtensions.FromString(mode.PostProcessingProvider);

        // Check if using HyperWhisper Cloud post-processing
        if (provider == PostProcessingProvider.HyperWhisperCloud)
            return "HyperWhisper";

        // Check if using a custom endpoint
        if (CustomPostProcessingEndpoint.IsCustomProviderString(mode.PostProcessingProvider))
        {
            var endpoint = Services.CustomEndpointManager.Instance
                .EndpointFromProviderString(mode.PostProcessingProvider);
            return endpoint?.Name ?? "Custom";
        }

        var modelId = provider == PostProcessingProvider.LocalLlm
            ? mode.LocalPostProcessingModel ?? mode.LanguageModel
            : mode.LanguageModel;

        if (string.IsNullOrEmpty(modelId))
            return string.Empty;

        // Get display name from LanguageModelInfo
        var modelInfo = LanguageModelInfo.GetById(modelId);
        return modelInfo?.DisplayName ?? modelId;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}
