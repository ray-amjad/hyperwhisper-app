using uniffi.hyperwhisper_core;

namespace HyperWhisper.SharedCore;

/// <summary>
/// One row of the user's vocabulary, as the two vocabulary passes below want it.
/// <paramref name="Replacement"/> is null (or empty) for a spelling-hint row —
/// only those are corrected towards by
/// <see cref="SharedCoreBridge.ApplyPhoneticVocabulary"/>. A row that carries a
/// replacement belongs to
/// <see cref="SharedCoreBridge.ApplySubstringVocabulary"/> instead.
///
/// Deliberately NOT <c>PortableVocabularyReplacement</c> (HyperWhisper.SpeechOutput):
/// that record requires a non-null replacement, and the whole point of the
/// phonetic pass is the rows that have none.
/// </summary>
public sealed record PortableVocabularyEntry(string Word, string? Replacement);

/// <summary>One phonetic correction, for the caller to log.</summary>
public sealed record PortablePhoneticMatch(string Token, string Replacement);

/// <summary>
/// The phonetic pass's answer for one transcription:
/// <paramref name="Text"/> is the corrected transcript,
/// <paramref name="Matches"/> is every correction in order, and
/// <paramref name="EntryCount"/> is how many vocabulary rows survived the core's
/// build filters and were actually matched against.
/// </summary>
public sealed record PortablePhoneticResult(
    string Text,
    IReadOnlyList<PortablePhoneticMatch> Matches,
    uint EntryCount);

public static partial class SharedCoreBridge
{
    public static string RemoveTrailingPeriod(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.RemoveTrailingPeriod(text);
    }

    public static string RemoveFillerWords(string text, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.RemoveFillerWords(text, language);
    }

    /// <summary>
    /// The canonical vocabulary-egress normalization: sanitize each term (strip
    /// <c>&lt;</c>/<c>&gt;</c>, collapse whitespace runs, cap at the core's
    /// 80-character term limit), drop the ones that sanitize to nothing,
    /// de-duplicate case-insensitively keeping first-seen order and casing, and
    /// optionally stop after <paramref name="limit"/> terms.
    ///
    /// <paramref name="limit"/> is <c>null</c> for "no cap"; <c>0</c> means zero
    /// terms, exactly like <c>.Take(0)</c>. Each call site owns its own cap and
    /// its own join separator — only this rule is shared.
    /// </summary>
    public static IReadOnlyList<string> NormalizeVocabularyTerms(
        IReadOnlyList<string>? words,
        uint? limit)
        => HyperwhisperCoreMethods.NormalizeVocabularyTerms(
            // Guarding empties here keeps a null/blank row out of the FFI call;
            // the core drops them anyway once they sanitize to nothing.
            words?.Where(word => !string.IsNullOrEmpty(word)).ToList() ?? [],
            limit);

    public static string ProcessVoiceCommands(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.ProcessVoiceCommands(text);
    }

    public static string ApplyHardenedReplacement(string text, string word, string replacement)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(replacement);
        return HyperwhisperCoreMethods.ApplyHardenedReplacement(text, word, replacement);
    }

    /// <summary>
    /// Beider-Morse phonetic vocabulary matching over a WHOLE transcript, in one
    /// call. Whole-word (<c>\b</c>-anchored), case-insensitive, literal
    /// replacement, diacritic-SENSITIVE.
    ///
    /// Rows that carry a replacement, and words of 2 Unicode scalars or fewer,
    /// are skipped; a token that already equals ANY vocabulary word is left
    /// alone. The core returns every correction rather than logging it, so each
    /// head logs through its own logger (issue #283).
    ///
    /// One call, not one per word. The retired native matchers encoded a single
    /// word per call — ~340 round trips for a 40-entry vocabulary over a
    /// 300-word transcript — which is why Windows carried both a cached matcher
    /// object and a per-call token memo. Neither is needed now.
    /// </summary>
    public static PortablePhoneticResult ApplyPhoneticVocabulary(
        string text,
        IReadOnlyList<PortableVocabularyEntry>? entries)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = HyperwhisperCoreMethods.PhoneticApplyVocabulary(text, ToNativeEntries(entries));
        return new PortablePhoneticResult(
            result.text,
            result.matches
                .Select(match => new PortablePhoneticMatch(match.token, match.replacement))
                .ToList(),
            result.entryCount);
    }

    /// <summary>
    /// The on-device providers' vocabulary pass: unanchored substring
    /// replacement, case-insensitive AND diacritic-insensitive, over the rows
    /// that DO carry a replacement, in list order.
    ///
    /// Deliberately NOT <see cref="ApplyHardenedReplacement"/>. That one anchors
    /// on <c>\b…\b</c> and is diacritic-sensitive, and it runs later over the
    /// pipeline's own vocabulary list. This one runs first, inside the provider,
    /// over its raw output — the split macOS made explicit in
    /// <c>VocabularyProcessor.swift</c> after finding four identical copies of
    /// it. Windows and Linux never had it; issue #283 gives it to them.
    ///
    /// Text outside a match comes back byte-identical: its case, its accents and
    /// its normalization form are untouched.
    /// </summary>
    public static string ApplySubstringVocabulary(
        string text,
        IReadOnlyList<PortableVocabularyEntry>? entries)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HyperwhisperCoreMethods.ApplySubstringVocabulary(text, ToNativeEntries(entries));
    }

    private static List<HwVocabularyEntry> ToNativeEntries(
        IReadOnlyList<PortableVocabularyEntry>? entries)
        => entries?
            // A null row cannot cross the FFI, and the core would drop it anyway
            // once its word sanitized to nothing.
            .Where(entry => entry is not null)
            .Select(entry => new HwVocabularyEntry(entry.Word ?? string.Empty, entry.Replacement))
            .ToList() ?? [];
}
