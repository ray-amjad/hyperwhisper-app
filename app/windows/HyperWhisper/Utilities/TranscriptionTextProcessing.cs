using uniffi.hyperwhisper_core;

namespace HyperWhisper.Utilities;

/// <summary>
/// Helpers for streaming transcript cleanup and voice commands. Thin wrappers
/// over the shared Rust core (hw-text) so the behavior can never drift from
/// macOS — the previous local regex copy had already missed the core's
/// mid-word guard fix ("newlines" firing the "new line" command).
/// </summary>
public static class TranscriptionTextProcessing
{
    /// <summary>
    /// Replaces spoken break commands ("new line" / "newline" / "new paragraph",
    /// with optional trailing punctuation) with a paragraph break.
    /// </summary>
    public static string ProcessVoiceCommands(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return HyperwhisperCoreMethods.ProcessVoiceCommands(text);
    }

    /// <summary>
    /// Splits a transcript at the breaks the user dictated ("new line" /
    /// "new paragraph"), returning one segment per paragraph. A transcript with no
    /// dictated break yields a single segment (the text itself).
    /// <para>
    /// Reuses the shared core's command regex: <see cref="ProcessVoiceCommands"/>
    /// turns each command into a paragraph break, so splitting on the break it
    /// produced keeps Windows and macOS on exactly one definition of "what counts
    /// as a command".
    /// </para>
    /// </summary>
    public static List<string> SplitOnDictatedBreaks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return ProcessVoiceCommands(text)
            .Split("\n\n")
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Final cleanup before saving a completed streaming session to history.
    /// </summary>
    public static string FinalizeStreamingText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return HyperwhisperCoreMethods.FinalizeStreamingText(text);
    }
}
