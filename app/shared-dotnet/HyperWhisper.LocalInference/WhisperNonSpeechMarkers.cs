using System.Text.RegularExpressions;

namespace HyperWhisper.LocalInference;

/// <summary>
/// Whisper's non-speech annotations: a bracketed or parenthesised marker it
/// emits in place of words when it hears none ([BLANK_AUDIO] and kin). Only
/// these fixed names match, so a bracket the speaker dictated survives. The
/// same list as the Windows head's TranscriptionService (issues #1449, #1742).
/// </summary>
internal static class WhisperNonSpeechMarkers
{
    private static readonly Regex Marker = new(
        @"[\[(]\s*(?:BLANK[_ ]AUDIO|silence|no speech|inaudible|music)\s*[\])]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRun = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Removes the markers and trims. A transcript made only of markers comes
    /// back empty, which the workflow reports as no speech.
    /// </summary>
    internal static string Strip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var trimmed = text.Trim();
        if (!Marker.IsMatch(trimmed)) return trimmed;

        return WhitespaceRun.Replace(Marker.Replace(trimmed, " "), " ").Trim();
    }
}
