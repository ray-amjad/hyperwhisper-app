using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HyperWhisper.Data.Entities;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// PHONETIC VOCABULARY MATCHER
///
/// Uses the Beider-Morse phonetic algorithm (via the shared Rust core,
/// <c>hw-core</c>) to match misrecognized words in local transcription output to
/// user-defined vocabulary entries. Catches phonetically similar errors that
/// exact string matching misses, e.g. "hyper wisper" → "HyperWhisper".
///
/// Direct port of the macOS <c>PhoneticVocabularyMatcher.swift</c>; both
/// platforms now call the same <c>phonetic_encode</c> export, so behaviour stays
/// in lockstep. This capability is new on Windows.
///
/// Entries WITH a replacement are skipped here — those are handled by
/// <see cref="VocabularyProcessor"/>'s regex replacement.
/// </summary>
public sealed class PhoneticVocabularyMatcher
{
    private readonly record struct EncodedEntry(string OriginalWord, IReadOnlyList<string> PhoneticCodes);

    private readonly List<EncodedEntry> _encodedVocabulary = new();

    /// <summary>
    /// Build a matcher from vocabulary items, pre-encoding each word phonetically.
    /// </summary>
    /// <param name="vocabulary">
    /// Vocabulary items to match against. Items with an explicit replacement, or
    /// words of 2 characters or fewer, are ignored to avoid false positives.
    /// </param>
    public PhoneticVocabularyMatcher(IEnumerable<VocabularyItem> vocabulary)
    {
        if (vocabulary is null)
        {
            throw new ArgumentException("Vocabulary must not be null.", nameof(vocabulary));
        }

        foreach (var item in vocabulary)
        {
            var word = item.Word?.Trim();
            if (string.IsNullOrEmpty(word))
            {
                continue;
            }

            // Items with explicit replacements are handled by regex replacement.
            if (!string.IsNullOrEmpty(item.Replacement))
            {
                continue;
            }

            // Skip very short words (<= 2 chars) to avoid false positives.
            if (word.Length <= 2)
            {
                continue;
            }

            var codes = BeiderMorse.Encode(word);
            if (codes.Count == 0)
            {
                continue;
            }

            _encodedVocabulary.Add(new EncodedEntry(word, codes));
        }

        if (_encodedVocabulary.Count > 0)
        {
            LoggingService.Info($"Phonetic matcher initialized with {_encodedVocabulary.Count} vocabulary entries");
        }
    }

    /// <summary>
    /// Apply phonetic vocabulary matching to transcribed text. For each word,
    /// checks if it phonetically matches a vocabulary entry and, if so, replaces
    /// the transcribed word with the correct vocabulary spelling.
    /// </summary>
    public string Apply(string text)
    {
        if (_encodedVocabulary.Count == 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var corrected = text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            // Skip very short words to avoid false positives.
            if (word.Length <= 2)
            {
                continue;
            }

            // Strip trailing punctuation for matching; the \b-anchored regex
            // replacement preserves the original punctuation (it sits outside the
            // word characters), so no manual re-attachment is needed.
            var cleanWord = StripTrailingPunctuation(word);
            if (string.IsNullOrEmpty(cleanWord))
            {
                continue;
            }

            var candidateCodes = BeiderMorse.Encode(cleanWord);
            if (candidateCodes.Count == 0)
            {
                continue;
            }

            foreach (var entry in _encodedVocabulary)
            {
                // Already the correct spelling (case-insensitive) — nothing to do.
                if (string.Equals(cleanWord, entry.OriginalWord, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var hasMatch = entry.PhoneticCodes.Any(dictCode => candidateCodes.Contains(dictCode));
                if (!hasMatch)
                {
                    continue;
                }

                // Word-boundary anchored, escaped regex replace (matches
                // VocabularyProcessor). Replace only the cleanWord that matched so
                // substrings of other words are left intact.
                var pattern = $@"\b{Regex.Escape(cleanWord)}\b";
                corrected = Regex.Replace(
                    corrected,
                    pattern,
                    entry.OriginalWord.Replace("$", "$$"),
                    // CultureInvariant so IgnoreCase folds ASCII consistently under
                    // any locale (e.g. the Turkish dotless-i) — matches the
                    // convention in VocabularyProcessor.cs.
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                LoggingService.Debug($"Phonetic match: '{word}' -> '{entry.OriginalWord}'");
                break; // Use first match.
            }
        }

        return corrected;
    }

    /// <summary>Strip trailing punctuation from a word, returning the clean word.</summary>
    private static string StripTrailingPunctuation(string word)
    {
        var end = word.Length;
        while (end > 0 && char.IsPunctuation(word[end - 1]))
        {
            end--;
        }
        return word[..end];
    }
}

/// <summary>
/// Thin C# wrapper over the shared Rust core's Beider-Morse phonetic encoder,
/// mirroring macOS <c>BeiderMorse</c>. Backed by <c>phonetic_encode</c> from the
/// UniFFI-generated <c>hyperwhisper_core.cs</c> binding.
/// </summary>
internal static class BeiderMorse
{
    /// <summary>
    /// Encode a word into its Beider-Morse phonetic representations.
    /// Returns an empty list for empty input.
    /// </summary>
    public static IReadOnlyList<string> Encode(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return Array.Empty<string>();
        }
        return HyperwhisperCoreMethods.PhoneticEncode(word);
    }
}
