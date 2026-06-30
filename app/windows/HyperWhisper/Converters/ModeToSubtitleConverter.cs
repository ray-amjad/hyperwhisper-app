using System.Globalization;
using System.Windows.Data;
using HyperWhisper.Data.Entities;
using HyperWhisper.Localization;
using HyperWhisper.Models;

namespace HyperWhisper.Converters;

/// <summary>
/// MODE TO SUBTITLE CONVERTER
///
/// Converts a Mode object to a display subtitle string:
/// - Cloud modes: "Provider · Model" (e.g., "OpenAI · Whisper-1", "Deepgram · Nova-3")
/// - HyperWhisper Cloud: Just "HyperWhisper Cloud" (single model)
/// - Local modes: "Local - {ModelType}" (shows model size like "Medium", "Large")
/// </summary>
public class ModeToSubtitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Mode mode)
            return string.Empty;

        if (mode.ProviderType == "cloud")
        {
            // Cloud mode: show provider and model (e.g., "OpenAI · Whisper-1")
            var provider = CloudTranscriptionProviderExtensions.FromIdentifier(mode.CloudProvider);
            var providerName = provider.GetDisplayName();

            // HyperWhisper Cloud: just show provider name (only one model)
            if (provider == CloudTranscriptionProvider.HyperWhisperCloud)
            {
                return providerName;
            }

            // Other providers: show "Provider · Model"
            var model = CloudTranscriptionModels.GetById(mode.CloudTranscriptionModel, provider);
            if (model != null)
            {
                return $"{providerName} · {model.DisplayName}";
            }

            return providerName;
        }
        else
        {
            // Local mode: show provider type and model size
            // Capitalize the model type for display (e.g., "base" -> "Base")
            var modelDisplay = string.IsNullOrEmpty(mode.ModelType)
                ? Loc.S("status.model.unknown")
                : char.ToUpper(mode.ModelType[0]) + mode.ModelType[1..];
            return Loc.S("modes.subtitle.local", modelDisplay);
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return WpfBinding.DoNothing;
    }
}
