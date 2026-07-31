using System.Linq;
using uniffi.hyperwhisper_core;

namespace HyperWhisper.Services;

/// <summary>
/// VOCABULARY PROCESSOR
/// Applies replacement values from vocabulary after transcription/post-processing.
/// Thin loop over the shared Rust core's <c>ApplyHardenedReplacement</c> (hw-text)
/// so macOS and Windows apply vocabulary identically: the core owns the trimming,
/// empty-guards, \b-anchored case-insensitive matching, LITERAL replacement
/// ($-tokens are never expanded as regex templates), and its own process-wide
/// compiled-regex cache. Mirrors macOS VocabularyProcessor.swift.
/// </summary>
public class VocabularyProcessor
{
    /// <summary>
    /// Applies replacements to the provided text using global vocabulary entries.
    /// Only entries with a non-empty replacement are applied. One FFI call per
    /// replacement-bearing entry per transcription — the macOS shipped shape; no
    /// batching (that would diverge from macOS and need new UniFFI surface).
    /// </summary>
    public string ApplyReplacements(string text)
    {
        var vocab = VocabularyService.Instance.GetAll()
            .Where(v => !string.IsNullOrWhiteSpace(v.Word) && !string.IsNullOrWhiteSpace(v.Replacement))
            .ToList();

        if (vocab.Count == 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        // NFC-normalize the transcript once and each search word (mirrors macOS
        // precomposedStringWithCanonicalMapping): regex matching is code-unit
        // based and does not treat canonically equivalent accented text as
        // equal. The replacement is passed through unnormalized — it is inserted
        // literally, never matched.
        var processed = text.Normalize(System.Text.NormalizationForm.FormC);

        foreach (var entry in vocab)
        {
            // No pre-trim: the core owns trimming + the empty-word/-replacement
            // guards, keeping the two platforms byte-identical.
            var updated = HyperwhisperCoreMethods.ApplyHardenedReplacement(
                processed,
                entry.Word!.Normalize(System.Text.NormalizationForm.FormC),
                entry.Replacement!);

            // Ordinal compare, not ReferenceEquals — the FFI always returns a
            // fresh string, so a reference check would log every entry.
            if (!string.Equals(updated, processed, StringComparison.Ordinal))
            {
                LoggingService.Debug($"VocabularyProcessor: Replaced '{entry.Word}' -> '{entry.Replacement}'");
            }

            processed = updated;
        }

        return processed.Trim();
    }
}
