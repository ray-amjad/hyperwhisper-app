using uniffi.hyperwhisper_core;

namespace HyperWhisper.Utilities;

/// <summary>
/// Helpers for transcript cleanup, spacing and voice commands. Thin wrappers
/// over the shared Rust core (hw-text) so the behavior can never drift from
/// macOS — the previous local regex copy had already missed the core's
/// mid-word guard fix ("newlines" firing the "new line" command), and the
/// local SmartSpacing / AutocapitalizeInsert copies this class replaced had
/// drifted the same way (issue #278): filler words stripped in every language,
/// the pronoun "I" lowercased mid-sentence, half the CJK range table, and a
/// culture-sensitive `char.ToLower`.
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

    /// <summary>
    /// Removes the filler words "uh", "um" and "er" (with an optional trailing
    /// comma) when they stand alone, and restores the capital on the next word
    /// when a sentence-opening filler was stripped.
    /// <para>
    /// English only. The core no-ops for every other language and for "auto" /
    /// an unset language, because "er" and "um" are real words in (for example)
    /// German. Always pass the mode's or session's language — the local copy
    /// had no language parameter and stripped fillers from German transcripts.
    /// </para>
    /// </summary>
    public static string RemoveFillerWords(string text, string? language)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return HyperwhisperCoreMethods.RemoveFillerWords(text, language);
    }

    /// <summary>
    /// Removes a single trailing period, preserving an ellipsis ("...").
    /// </summary>
    public static string RemoveTrailingPeriod(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return HyperwhisperCoreMethods.RemoveTrailingPeriod(text);
    }

    /// <summary>
    /// Appends a language-aware trailing space so consecutive transcriptions
    /// don't run together, and skips it for languages that write without word
    /// spaces (CJK, Thai). An empty, null or "auto" language makes the core
    /// detect CJK from the text itself.
    /// </summary>
    public static string AppendTrailingSpace(string text, string? modeLanguage)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        return HyperwhisperCoreMethods.AppendTrailingSpace(text, modeLanguage ?? string.Empty);
    }

    /// <summary>
    /// Lowercases the first letter of an inserted fragment when the caret sits
    /// mid-sentence. Acronyms ("API") and the first-person pronoun ("I", "I'm",
    /// "I'll", "I've", "I'd", straight or curly apostrophe) are left alone, and
    /// every other cursor context is a pass-through.
    /// </summary>
    public static string ApplyAutocapitalize(string text, TextFieldContext context)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return HyperwhisperCoreMethods.ApplyAutocapitalize(text, ToCursorContext(context));
    }

    /// <summary>
    /// Maps the native UIA probe's verdict onto the core's cursor context. The
    /// generated binding's enum is internal to this assembly, so the public
    /// surface stays on <see cref="TextFieldContext"/>.
    /// </summary>
    private static CursorContext ToCursorContext(TextFieldContext context) => context switch
    {
        TextFieldContext.StartOfSentence => CursorContext.StartOfSentence,
        TextFieldContext.MidSentence => CursorContext.MidSentence,
        _ => CursorContext.Unknown,
    };
}
