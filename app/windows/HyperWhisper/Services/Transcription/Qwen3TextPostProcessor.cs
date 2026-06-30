namespace HyperWhisper.Services;

/// <summary>
/// Qwen3-specific cleanup applied to raw daemon text before it reaches the user.
///
/// Qwen3-ASR has two documented failure modes that the offline transducer
/// (Parakeet) does not, so this is applied ONLY to Qwen3 output:
///
/// 1. Truncated UTF-8: an unpatched sherpa Decode() leak can emit a partial
///    multi-byte sequence, which .NET surfaces as U+FFFD replacement characters.
/// 2. Repetition loops (#129, ~1/10 files): the autoregressive decoder gets
///    stuck repeating a short unit to the end of the segment.
/// </summary>
public static class Qwen3TextPostProcessor
{
    /// <summary>
    /// Strips U+FFFD replacement chars and collapses a trailing repetition loop.
    /// Safe to call on any string; returns the input unchanged when nothing matches.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var cleaned = text.Replace("�", string.Empty);
        cleaned = CollapseTrailingRepetition(cleaned);
        return cleaned;
    }

    /// <summary>
    /// Returns true when <paramref name="text"/> is expected to be a CJK language
    /// (ja/zh/ko/yue) but contains no CJK characters at all — a strong signal of a
    /// wrong-script hallucination. Used only to log a warning, never to drop text.
    /// </summary>
    public static bool LooksLikeWrongScript(string? text, string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(languageCode))
            return false;

        var expectsCjk = languageCode is "ja" or "zh" or "ko" or "yue";
        if (!expectsCjk) return false;

        foreach (var ch in text)
        {
            if (IsCjk(ch)) return false; // found CJK — looks fine
        }
        return true; // expected CJK, found none
    }

    private static bool IsCjk(char ch)
    {
        // Hiragana, Katakana, CJK Unified Ideographs (+ Ext A), Hangul.
        return (ch >= 0x3040 && ch <= 0x30FF)   // kana
            || (ch >= 0x3400 && ch <= 0x4DBF)   // CJK ext A
            || (ch >= 0x4E00 && ch <= 0x9FFF)   // CJK unified
            || (ch >= 0xAC00 && ch <= 0xD7A3);  // Hangul syllables
    }

    /// <summary>
    /// If the text ends with a 2..40-char unit repeated 4+ times consecutively
    /// (the classic decoder loop), reduce that run to two occurrences. Conservative
    /// by design: it only touches a trailing run and never deletes content earlier
    /// in the string.
    /// </summary>
    private static string CollapseTrailingRepetition(string text)
    {
        const int minUnit = 2;
        const int maxUnit = 40;
        const int minRepeats = 4;

        for (int unit = minUnit; unit <= maxUnit && unit * minRepeats <= text.Length; unit++)
        {
            int repeats = 1;
            int pos = text.Length - 2 * unit;
            while (pos >= 0 && string.CompareOrdinal(text, pos, text, text.Length - unit, unit) == 0)
            {
                repeats++;
                pos -= unit;
            }

            if (repeats >= minRepeats)
            {
                int runStart = pos + unit;       // first index of the repeated run
                int keepEnd = runStart + 2 * unit; // keep two occurrences
                return text.Substring(0, keepEnd);
            }
        }

        return text;
    }
}
