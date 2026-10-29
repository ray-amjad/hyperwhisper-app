using System.Globalization;
using Avalonia.Data.Converters;
using HyperWhisper.CloudPostProcessing;

namespace HyperWhisper.Linux;

/// <summary>
/// The History page's post-processing badge. Since issue #1158 a transcript row stores the
/// provider ID the run used (<c>openai</c>, <c>local_llm</c>, <c>hyperwhispercloud</c>,
/// <c>custom:&lt;uuid&gt;</c>), the value macOS and Windows store and <c>/recordings</c>
/// returns, so the label is made here at display time. The row carries no model, so the badge
/// names the provider alone.
///
/// Rows written before #1158 hold a display label ("OpenAI · gpt-5.6-luna") and are not
/// migrated (Ray, 2026-10-19). Anything that is not a known ID is shown as it is.
/// </summary>
public sealed class PostProcessingProviderLabelConverter : IValueConverter
{
    public static readonly PostProcessingProviderLabelConverter Instance = new();

    public static string Label(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return string.Empty;
        if (string.Equals(stored.Trim(), LinuxLocalPostProcessor.LocalLlmProviderId, StringComparison.OrdinalIgnoreCase))
            return "Local LLM";
        // An old label never parses: "OpenAI · gpt-x" is not an ID, so it falls through unchanged.
        return LinuxPostProcessingRouter.TryResolveProvider(stored, out var provider, out _)
            ? CloudPostProcessingService.ProviderDisplayName(provider)
            : stored;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Label(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
