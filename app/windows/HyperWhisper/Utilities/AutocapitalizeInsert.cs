// AUTOCAPITALIZE INSERT
//
// Adjusts the first word of inserted transcript text to match the cursor's
// surrounding context: lowercase mid-sentence, untouched at sentence start.
// Mirrors macOS hyperwhisper/Utilities/AutocapitalizeInsert.swift.

namespace HyperWhisper.Utilities;

public enum TextFieldContext
{
    StartOfSentence,
    MidSentence,
    Unknown
}

public static class AutocapitalizeInsert
{
    /// <summary>
    /// Apply case adjustment to a transcript fragment based on cursor context.
    ///
    /// - StartOfSentence / Unknown -> return text unchanged (Whisper/LLM
    ///   output is already capitalized at sentence start; safe pass-through).
    /// - MidSentence -> lowercase the first letter of the first word, unless
    ///   the first token looks like an acronym (>=2 leading uppercase letters,
    ///   e.g. "API", "USA"), in which case leave it alone.
    /// </summary>
    public static string Apply(string text, TextFieldContext context)
    {
        if (context != TextFieldContext.MidSentence) return text;
        if (string.IsNullOrEmpty(text)) return text;

        int firstIdx = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i])) { firstIdx = i; break; }
        }
        if (firstIdx < 0) return text;

        char firstChar = text[firstIdx];
        if (!char.IsUpper(firstChar)) return text;

        // Acronym guard: if the next character is also an uppercase letter,
        // assume the user dictated an acronym and don't touch it.
        int nextIdx = firstIdx + 1;
        if (nextIdx < text.Length && char.IsLetter(text[nextIdx]) && char.IsUpper(text[nextIdx]))
        {
            return text;
        }

        var lowered = char.ToLower(firstChar);
        return text.Substring(0, firstIdx) + lowered + text.Substring(firstIdx + 1);
    }
}
