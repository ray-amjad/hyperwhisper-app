// SMART SPACING FOR CONSECUTIVE TRANSCRIPTIONS
//
// Problem Being Solved:
// When users record multiple sentences consecutively (stop recording, start recording),
// the text from each transcription gets pasted without any space between them:
// - Recording 1: "Hello world."
// - Recording 2: "How are you?"
// - Result: "Hello world.How are you?" (missing space!)
//
// Solution:
// Automatically append a trailing space to transcriptions for space-delimited languages,
// while respecting languages that don't use word spaces (CJK).
//
// Language-Aware Rules:
// - Space-delimited languages (English, Danish, German, etc.): Add trailing space
// - CJK languages (Japanese, Chinese, Korean): No trailing space (words aren't separated by spaces)
// - Detection method: Uses both the mode's language setting AND text content analysis
//
// Why Text Content Analysis:
// When the mode is set to "auto" detect language, we can't rely on the mode setting alone.
// Instead, we analyze the actual text content to detect if it contains CJK characters.
// This ensures correct behavior even with auto-detect or mixed-language content.

using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HyperWhisper.Utilities;

/// <summary>
/// Provides language-aware trailing space handling for consecutive transcriptions.
/// Matches the macOS SmartSpacing.swift implementation.
/// </summary>
public static class SmartSpacing
{
    // Language codes that don't use spaces between words
    // These languages use continuous script where words flow together
    private static readonly HashSet<string> NoSpaceLanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ja",       // Japanese
        "zh",       // Chinese (Simplified)
        "zh-TW",    // Chinese (Traditional)
        "zh-Hans",  // Chinese (Simplified, alternate code)
        "zh-Hant",  // Chinese (Traditional, alternate code)
        "ko",       // Korean (modern Korean uses spaces, but less strictly)
        "th",       // Thai (traditionally no word spaces)
    };

    /// <summary>
    /// Checks if a language code represents a no-space language.
    /// </summary>
    /// <param name="languageCode">ISO language code (e.g., "en", "ja", "zh-TW")</param>
    /// <returns>true if the language doesn't use word spaces</returns>
    private static bool IsNoSpaceLanguage(string languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
            return false;

        // Check exact match first
        if (NoSpaceLanguageCodes.Contains(languageCode))
            return true;

        // Check prefix match for variants (e.g., "zh-CN" matches "zh")
        if (languageCode.Length >= 2)
        {
            var prefix = languageCode.Substring(0, 2);
            return NoSpaceLanguageCodes.Contains(prefix);
        }

        return false;
    }

    /// <summary>
    /// Detects if text contains CJK (Chinese, Japanese, Korean) characters.
    /// </summary>
    /// <param name="text">The text to analyze</param>
    /// <returns>true if the text primarily contains CJK characters</returns>
    /// <remarks>
    /// How It Works:
    /// Scans the text for characters in CJK Unicode ranges:
    /// - CJK Unified Ideographs (Chinese/Japanese/Korean characters)
    /// - Hiragana &amp; Katakana (Japanese)
    /// - Hangul (Korean)
    ///
    /// Why "Primarily":
    /// Mixed content (e.g., Japanese with some English words) should still
    /// be treated as CJK because the dominant language doesn't use spaces.
    /// </remarks>
    private static bool ContainsCjkCharacters(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        int cjkCount = 0;
        int totalCount = 0;

        foreach (char c in text)
        {
            // Skip whitespace and punctuation
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c))
                continue;

            totalCount++;

            // Check if this character is in any CJK range
            if (IsCjkCharacter(c))
            {
                cjkCount++;
            }
        }

        // If more than 30% of characters are CJK, treat as CJK text
        // This handles mixed content like "これはtestです" (Japanese with English)
        if (totalCount == 0)
            return false;

        double cjkRatio = (double)cjkCount / totalCount;
        return cjkRatio > 0.3;
    }

    /// <summary>
    /// Checks if a character is in a CJK Unicode range.
    /// </summary>
    private static bool IsCjkCharacter(char c)
    {
        int value = c;

        // CJK Unified Ideographs (most common Chinese/Japanese characters)
        if (value >= 0x4E00 && value <= 0x9FFF)
            return true;

        // CJK Unified Ideographs Extension A
        if (value >= 0x3400 && value <= 0x4DBF)
            return true;

        // Hiragana (Japanese)
        if (value >= 0x3040 && value <= 0x309F)
            return true;

        // Katakana (Japanese)
        if (value >= 0x30A0 && value <= 0x30FF)
            return true;

        // Hangul Syllables (Korean)
        if (value >= 0xAC00 && value <= 0xD7AF)
            return true;

        // Hangul Jamo (Korean)
        if (value >= 0x1100 && value <= 0x11FF)
            return true;

        return false;
    }

    /// <summary>
    /// Removes a single trailing period from text, preserving ellipsis ("...").
    /// Matches macOS TranscriptionTextProcessing.removeTrailingPeriod().
    /// </summary>
    public static string RemoveTrailingPeriod(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith('.') && !trimmed.EndsWith(".."))
        {
            int idx = text.LastIndexOf('.');
            if (idx >= 0)
                return text.Remove(idx, 1);
        }
        return text;
    }

    /// <summary>
    /// Removes common filler words ("uh", "um", "er") when surrounded by whitespace.
    /// Applied when post-processing is off to clean up raw STT output.
    /// Matches macOS TranscriptionTextProcessing.removeFillerWords().
    /// </summary>
    public static string RemoveFillerWords(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Port of macOS TranscriptionTextProcessing.removeFillerWords. Each alternative ends the
        // filler with an OPTIONAL trailing comma (",?") so "uh," is stripped with its comma:
        //   1. (?:^\s*|(?<=\s))\b(uh|um|er)\b,?\s+  - start of text (after leading whitespace) OR
        //      preceded by whitespace, then trailing whitespace. The "^\s*" branch catches a
        //      sentence-opening "Uh," / "Um " that the old pattern missed.
        //   2. \s+\b(uh|um|er)\b,?(?=\s|$)          - preceded by whitespace, followed by whitespace
        //      or end of text — catches a filler that ends a line/sentence.
        // Lookbehind/lookahead are not consumed, so consecutive fillers ("uh um") both match.
        // Whitespace is replaced with nothing (not a space) so newlines/tabs are preserved.
        var result = Regex.Replace(
            text,
            @"(?i)(?:^\s*|(?<=\s))\b(uh|um|er)\b,?\s+|\s+\b(uh|um|er)\b,?(?=\s|$)",
            "");

        // If a sentence-opening filler was stripped, the next word may now start lowercase
        // (the STT had capitalized the filler as the opener). Restore the leading capital.
        if (Regex.IsMatch(text, @"(?i)^\s*\b(uh|um|er)\b")
            && result.Length > 0 && char.IsLower(result[0]))
        {
            result = char.ToUpper(result[0]) + result.Substring(1);
        }

        return result;
    }

    /// <summary>
    /// Appends a smart trailing space based on language.
    /// </summary>
    /// <param name="text">The transcribed text to process</param>
    /// <param name="modeLanguage">The language code from the mode (e.g., "en", "ja", "auto")</param>
    /// <returns>Text with appropriate trailing space (or unchanged for CJK)</returns>
    /// <remarks>
    /// Decision Logic:
    /// 1. If text already ends with whitespace → return unchanged
    /// 2. If mode language is explicitly CJK → no space
    /// 3. If mode language is "auto" → analyze text content for CJK characters
    /// 4. Otherwise → add trailing space
    ///
    /// Examples:
    /// <code>
    /// // English text → adds space
    /// AppendTrailingSpace("Hello world.", "en")
    /// // Returns: "Hello world. "
    ///
    /// // Japanese text → no space
    /// AppendTrailingSpace("今日はいい天気ですね。", "ja")
    /// // Returns: "今日はいい天気ですね。"
    ///
    /// // Auto-detect with Japanese content → no space
    /// AppendTrailingSpace("今日はいい天気ですね。", "auto")
    /// // Returns: "今日はいい天気ですね。" (detected CJK content)
    ///
    /// // Auto-detect with English content → adds space
    /// AppendTrailingSpace("Hello world.", "auto")
    /// // Returns: "Hello world. "
    /// </code>
    /// </remarks>
    public static string AppendTrailingSpace(string text, string? modeLanguage)
    {
        // STEP 1: Already ends with whitespace? Don't double up
        if (!string.IsNullOrEmpty(text) && char.IsWhiteSpace(text[^1]))
        {
            return text;
        }

        // STEP 2: Empty text? Return as-is
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        // STEP 3: Determine if we should add space based on language
        bool shouldAddSpace;

        if (string.IsNullOrEmpty(modeLanguage) || modeLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // AUTO-DETECT MODE:
            // Analyze the actual text content to determine if it's CJK
            // This handles cases where the user has "auto" language selected
            shouldAddSpace = !ContainsCjkCharacters(text);
        }
        else
        {
            // EXPLICIT LANGUAGE MODE:
            // Use the mode's language setting to determine behavior
            shouldAddSpace = !IsNoSpaceLanguage(modeLanguage);
        }

        // STEP 4: Apply spacing decision
        if (shouldAddSpace)
        {
            return text + " ";
        }
        else
        {
            return text;
        }
    }
}
