using System.Linq;
using System.Text.RegularExpressions;

namespace HyperWhisper.Utilities;

/// <summary>
/// Helpers for streaming transcript cleanup and voice commands.
/// </summary>
public static class TranscriptionTextProcessing
{
    private static readonly Regex NewLineCommandRegex = new(
        @"\bnew\s*line[.,!?]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static readonly Regex ThreeOrMoreNewlinesRegex = new(
        @"\n{3,}",
        RegexOptions.Compiled
    );

    /// <summary>
    /// Replaces spoken text commands with formatting. Matches the macOS streaming behavior.
    /// </summary>
    public static string ProcessVoiceCommands(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return NewLineCommandRegex.Replace(text, "\n\n");
    }

    /// <summary>
    /// Final cleanup before saving a completed streaming session to history.
    /// </summary>
    public static string FinalizeStreamingText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').Select(line => line.TrimEnd());
        normalized = string.Join("\n", lines).Trim();
        return ThreeOrMoreNewlinesRegex.Replace(normalized, "\n\n");
    }
}
